namespace Knovara.Core.Models;

public enum SourceType
{
    Code,
    Document,
    Website
}

public enum ResourceStatus
{
    Pending,
    Processing,
    Ready,
    Failed
}

/// <summary>
/// A registered source. <see cref="SourceVersion"/> is the content hash of the last successfully
/// extracted version, or null until the first extraction completes.
/// </summary>
public record ResourceReference(
    string ResourceId,
    SourceType Type,
    string Uri,
    string? SourceVersion,
    string TenantId,
    ResourceStatus Status);

/// <summary>A node in the hierarchical knowledge representation (document/section/passage).</summary>
public record KnowledgeNode(
    string NodeId,
    string ResourceId,
    string? ParentNodeId,
    string Level,
    string Title,
    string? Summary,
    string? OriginalContent,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// A node as produced by an extractor (e.g. the Docling worker), identified by ids local to one
/// extraction. Parents must appear before their children; the first node is the document root.
/// </summary>
public record ExtractedNode(
    string LocalId,
    string? ParentLocalId,
    string Level,
    string Title,
    string? Content,
    IReadOnlyDictionary<string, string>? Metadata);

/// <summary>An extracted entity or relationship edge (e.g., class -> method, service -> dependency).</summary>
public record RelationshipEdge(
    string FromNodeId,
    string ToNodeId,
    string RelationType);

/// <summary>A passage as it exists in the knowledge store right now, used to verify evidence.</summary>
public record CurrentPassage(
    string NodeId,
    string ResourceId,
    string SourceVersion,
    string Content);

/// <summary>A request for the extraction worker to fetch and process a resource.</summary>
public record IngestionJob(
    string ResourceId,
    string TenantId,
    string Uri,
    SourceType SourceType);
