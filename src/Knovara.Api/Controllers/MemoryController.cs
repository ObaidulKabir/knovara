using Knovara.Core.Contracts;
using Knovara.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Knovara.Api.Controllers;

[ApiController]
[Route("api/memory")]
public sealed class MemoryController : ControllerBase
{
    private readonly IContextMemoryService _memory;

    public MemoryController(IContextMemoryService memory)
    {
        _memory = memory;
    }

    [HttpGet("sessions/{sessionId}")]
    public async Task<ActionResult<SessionKnowledge>> GetSession(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        string sessionId,
        CancellationToken ct)
    {
        var knowledge = await _memory.GetSessionKnowledgeAsync(tenantId, sessionId, ct);
        return knowledge is null ? NotFound() : Ok(knowledge);
    }

    [HttpPut("sessions/{sessionId}")]
    public async Task<IActionResult> SaveSession(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        string sessionId,
        [FromBody] SessionKnowledge knowledge,
        CancellationToken ct)
    {
        if (sessionId != knowledge.SessionId) return BadRequest("sessionId mismatch");
        await _memory.SaveSessionKnowledgeAsync(tenantId, knowledge, ct);
        return NoContent();
    }

    [HttpPost("sessions/{sessionId}/documents")]
    public async Task<ActionResult<SessionKnowledge>> AddDocument(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        string sessionId,
        [FromBody] ContextDocument doc,
        CancellationToken ct)
    {
        var existing = await _memory.GetSessionKnowledgeAsync(tenantId, sessionId, ct)
            ?? new SessionKnowledge(sessionId, "session-root", [], [], []);

        var title = string.IsNullOrWhiteSpace(doc.Title) ? "Added Document" : doc.Title;
        var facts = new List<Fact>(existing.Facts)
        {
            new Fact(
                Claim: $"{title}: {doc.Content}",
                Source: doc.Source ?? "direct-context-doc",
                Status: FactStatus.Verified)
        };

        var updated = existing with { Facts = facts };
        await _memory.SaveSessionKnowledgeAsync(tenantId, updated, ct);
        return Ok(updated);
    }

    [HttpDelete("sessions/{sessionId}/documents")]
    public async Task<IActionResult> ClearDocuments(
        [FromHeader(Name = TenantHeader.Name)] string tenantId,
        string sessionId,
        CancellationToken ct)
    {
        var existing = await _memory.GetSessionKnowledgeAsync(tenantId, sessionId, ct);
        if (existing is not null)
        {
            var updated = existing with { Facts = [] };
            await _memory.SaveSessionKnowledgeAsync(tenantId, updated, ct);
        }
        return NoContent();
    }
}
