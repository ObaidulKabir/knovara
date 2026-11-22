namespace ICE.Core.Models;

public enum SourceType
{
    Code,
    Document,
    Website
}

public enum IndexingLevel
{
    Basic,
    Enhanced,
    Advanced
}

/// <summary>Identifies an ingested source and the version it was processed at.</summary>
public record ResourceReference(
    string ResourceId,
    SourceType Type,
    string Uri,
    string SourceVersion,
    string TenantId);

/// <summary>A node in the hierarchical knowledge representation (document/chapter/section/topic/passage).</summary>
public record KnowledgeNode(
    string NodeId,
    string ResourceId,
    string? ParentNodeId,
    string Level,
    string Title,
    string? Summary,
    string? OriginalContent,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>An extracted entity or relationship edge (e.g., class -> method, service -> dependency).</summary>
public record RelationshipEdge(
    string FromNodeId,
    string ToNodeId,
    string RelationType);
