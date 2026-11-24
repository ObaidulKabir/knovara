using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knovara.Api.Controllers;
using Knovara.ContextOptimization;
using Knovara.Core.Contracts;
using Knovara.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Knovara.Api.Tests
{
    public class ContextDocumentAskTests
    {
        private readonly Mock<IIntelligentRetrievalService> _mockRetrieval = new();
        private readonly Mock<IContextMemoryService> _mockMemory = new();
        private readonly Mock<IKnowledgeStore> _mockStore = new();
        private readonly Mock<IEvaluationService> _mockEval = new();
        private readonly IContextOptimizationService _optimization = new TokenBudgetContextOptimizationService();

        private RetrievalController CreateController()
        {
            // Default setup for memory: no cached evidence, empty session knowledge
            _mockMemory.Setup(m => m.GetCachedEvidenceAsync(It.IsAny<EvidenceCacheKey>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync((RetrievalResult?)null);
            _mockMemory.Setup(m => m.GetSessionKnowledgeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync((SessionKnowledge?)null);

            // Default setup for store: current passages empty
            _mockStore.Setup(s => s.GetCurrentPassagesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new List<CurrentPassage>());

            // Default setup for retrieval: returns empty candidates
            _mockRetrieval.Setup(r => r.RetrieveAsync(It.IsAny<RetrievalQuery>(), It.IsAny<CancellationToken>()))
                          .ReturnsAsync(new RetrievalResult("q-1", new List<EvidenceItem>(), false, null));

            // Mirrors ProgressiveRetrievalService.ExpandAsync, which hands the prior result back.
            // Without this an unstubbed Moq call returns null, and because the result above reports
            // EvidenceSufficient: false the controller always expands - so Ask threw a
            // NullReferenceException before reaching any assertion.
            _mockRetrieval.Setup(r => r.ExpandAsync(It.IsAny<RetrievalQuery>(), It.IsAny<RetrievalResult>(), It.IsAny<CancellationToken>()))
                          .ReturnsAsync((RetrievalQuery _, RetrievalResult prior, CancellationToken _) => prior);

            return new RetrievalController(
                _mockRetrieval.Object,
                _optimization,
                _mockMemory.Object,
                _mockStore.Object,
                _mockEval.Object);
        }

        [Fact]
        public async Task Ask_WithAddedDocuments_ReturnsAddedDocumentInContextPackage()
        {
            // Arrange
            var controller = CreateController();
            var addedDoc = new ContextDocument("Quarterly Report", "Q3 Revenue reached 42 million USD.", "finance.pdf");

            var request = new AskRequest(
                SessionId: "test-session-1",
                Question: "What was the Q3 revenue?",
                TokenBudget: 2000,
                ResourceIds: null,
                Documents: new List<ContextDocument> { addedDoc });

            // Act
            var actionResult = await controller.Ask("test-tenant", request, CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
            var contextPackage = Assert.IsType<ContextPackage>(okResult.Value);

            Assert.NotEmpty(contextPackage.Evidence);
            var evidence = contextPackage.Evidence.First();
            Assert.Equal("Quarterly Report", evidence.Title);
            Assert.Equal("Q3 Revenue reached 42 million USD.", evidence.Content);
            Assert.Equal("finance.pdf", evidence.ResourceId);
            Assert.True(contextPackage.EstimatedTokenCount > 0);
        }

        [Fact]
        public async Task Ask_WithMultipleAddedDocuments_IncludesAllWithinTokenBudget()
        {
            // Arrange
            var controller = CreateController();
            var docs = new List<ContextDocument>
            {
                new("Doc A", "Alpha protocol specification details.", "doc_a.txt"),
                new("Doc B", "Beta architecture configuration flags.", "doc_b.txt")
            };

            var request = new AskRequest(
                SessionId: "session-multi",
                Question: "Explain the protocols and architecture.",
                TokenBudget: 2000,
                ResourceIds: null,
                Documents: docs);

            // Act
            var actionResult = await controller.Ask("test-tenant", request, CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
            var contextPackage = Assert.IsType<ContextPackage>(okResult.Value);

            Assert.Equal(2, contextPackage.Evidence.Count);
            Assert.Contains(contextPackage.Evidence, e => e.Title == "Doc A" && e.Content.Contains("Alpha"));
            Assert.Contains(contextPackage.Evidence, e => e.Title == "Doc B" && e.Content.Contains("Beta"));
        }

        [Fact]
        public async Task Ask_WithAddedDocumentsAndCorpusRetrieval_PrioritizesAddedDocsAtTop()
        {
            // Arrange
            var controller = CreateController();

            // Corpus retrieval returns lower score item
            var corpusItem = new EvidenceItem(
                NodeId: "corpus-1",
                ResourceId: "res-corpus",
                Title: "General Handbook",
                Content: "General company overview and background.",
                SourceVersion: "v1",
                RetrievedVia: IndexKind.Semantic,
                RelevanceScore: 0.65);

            _mockRetrieval.Setup(r => r.RetrieveAsync(It.IsAny<RetrievalQuery>(), It.IsAny<CancellationToken>()))
                          .ReturnsAsync(new RetrievalResult("q-1", new List<EvidenceItem> { corpusItem }, true, null));

            _mockStore.Setup(s => s.GetCurrentPassagesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new List<CurrentPassage>
                      {
                          new("corpus-1", "res-corpus", "v1", "General company overview and background.")
                      });

            var pinnedDoc = new ContextDocument("Custom Spec", "Pinned critical implementation rule.", "spec.md");

            var request = new AskRequest(
                SessionId: "session-priority",
                Question: "How should we implement the rule?",
                TokenBudget: 2000,
                ResourceIds: null,
                Documents: new List<ContextDocument> { pinnedDoc });

            // Act
            var actionResult = await controller.Ask("test-tenant", request, CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
            var contextPackage = Assert.IsType<ContextPackage>(okResult.Value);

            Assert.Equal(2, contextPackage.Evidence.Count);
            // Pinned document has score 1.0, so it appears first
            Assert.Equal("Custom Spec", contextPackage.Evidence[0].Title);
            Assert.Equal("General Handbook", contextPackage.Evidence[1].Title);
        }
    }
}
