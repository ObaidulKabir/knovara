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
using Microsoft.Extensions.Logging.Abstractions;
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
        private readonly Mock<IModelService> _mockModels = new();
        private readonly IContextOptimizationService _optimization = new TokenBudgetContextOptimizationService();

        private RetrievalController CreateController()
        {
            // Neutral by default: tests that don't care about relevance scoring get every attached
            // chunk scored 0, so PinnedDocumentBaseScore alone decides pinned-vs-corpus ordering.
            _mockModels.Setup(m => m.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync((string _, IReadOnlyList<string> chunks, CancellationToken _) => chunks.Select(_ => 0.0).ToList());

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
                _mockEval.Object,
                _mockModels.Object,
                NullLogger<RetrievalController>.Instance);
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
            // PinnedDocumentBaseScore keeps the attached document above any corpus score observed.
            Assert.Equal("Custom Spec", contextPackage.Evidence[0].Title);
            Assert.Equal("General Handbook", contextPackage.Evidence[1].Title);
        }

        /// <summary>
        /// The point of actually processing an attachment for retrieval, not just pinning it whole:
        /// when a large document cannot all fit the budget, the chunk that is genuinely relevant to
        /// THIS question must be the one kept - not whichever chunk happens to come first in the file.
        /// </summary>
        [Fact]
        public async Task Ask_WithLargeAddedDocument_KeepsTheRelevantChunkWithinBudget()
        {
            // Arrange
            var controller = CreateController();
            // Each paragraph alone (~700 chars) stays under PinnedDocumentChunker's 1200-char cap, so
            // neither is split further; together (~1400) they exceed it, so the chunker places them
            // in two separate chunks - letting the budget below admit only one of the two.
            var irrelevantChunk = string.Join(" ", Enumerable.Repeat("Unrelated filler text about office furniture procurement.", 12));
            var relevantChunk = string.Join(" ", Enumerable.Repeat("The Fairhaven pilot processed 4812 invoices in March.", 12));
            var content = irrelevantChunk + "\n\n" + relevantChunk;
            var doc = new ContextDocument("Pilot Notes", content, "pilot-notes.md");

            // The real model service would score the relevant chunk higher for this question; the
            // mock stands in for that judgement so the test is deterministic.
            _mockModels.Setup(m => m.RerankAsync("How many invoices did the pilot process?", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync((string _, IReadOnlyList<string> chunks, CancellationToken _) =>
                           chunks.Select(c => c.Contains("invoices") ? 5.0 : -5.0).ToList());

            var request = new AskRequest(
                SessionId: "session-large-doc",
                Question: "How many invoices did the pilot process?",
                TokenBudget: 200, // each chunk is ~170 tokens, so only one of the two fits
                ResourceIds: null,
                Documents: new List<ContextDocument> { doc });

            // Act
            var actionResult = await controller.Ask("test-tenant", request, CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
            var contextPackage = Assert.IsType<ContextPackage>(okResult.Value);

            Assert.NotEmpty(contextPackage.Evidence);
            Assert.All(contextPackage.Evidence, e => Assert.Contains("invoices", e.Content));
            Assert.DoesNotContain(contextPackage.Evidence, e => e.Content.Contains("furniture"));
        }

        [Fact]
        public async Task Ask_WithManyDocuments_ScoresAllChunksInOneCallAndKeepsScoresWithTheirOwnDocument()
        {
            // An attached folder is many documents; scores must land on the right document, and the
            // model service must be called once, not once per file.
            var controller = CreateController();
            var docs = new List<ContextDocument>
            {
                new("notes/furniture.md", "Office furniture procurement schedule.", "furniture.md"),
                new("notes/pilot.md", "The Fairhaven pilot processed 4812 invoices.", "pilot.md"),
                new("notes/parking.md", "Parking permits renew each January.", "parking.md")
            };

            var calls = 0;
            _mockModels.Setup(m => m.RerankAsync("How many invoices?", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync((string _, IReadOnlyList<string> chunks, CancellationToken _) =>
                       {
                           calls++;
                           return chunks.Select(c => c.Contains("invoices") ? 5.0 : -5.0).ToList();
                       });

            var request = new AskRequest("session-folder", "How many invoices?", TokenBudget: 14, ResourceIds: null, Documents: docs);

            var actionResult = await controller.Ask("test-tenant", request, CancellationToken.None);

            var contextPackage = Assert.IsType<ContextPackage>(Assert.IsType<OkObjectResult>(actionResult.Result).Value);
            Assert.Equal(1, calls);
            Assert.Single(contextPackage.Evidence);
            Assert.Equal("notes/pilot.md", contextPackage.Evidence[0].Title);
        }

        [Fact]
        public async Task Ask_WhenModelServiceFails_StillReturnsTheAttachedDocument()
        {
            // Arrange
            var controller = CreateController();
            _mockModels.Setup(m => m.RerankAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                       .ThrowsAsync(new HttpRequestException("model service unreachable"));

            var doc = new ContextDocument("Notes", "Short note that does not need reranking to matter.", "notes.md");
            var request = new AskRequest(
                SessionId: "session-degraded",
                Question: "What does the note say?",
                TokenBudget: 2000,
                ResourceIds: null,
                Documents: new List<ContextDocument> { doc });

            // Act
            var actionResult = await controller.Ask("test-tenant", request, CancellationToken.None);

            // Assert: a model-service outage must not break attaching a document.
            var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
            var contextPackage = Assert.IsType<ContextPackage>(okResult.Value);
            Assert.Single(contextPackage.Evidence);
            Assert.Equal("Notes", contextPackage.Evidence[0].Title);
        }
    }
}
