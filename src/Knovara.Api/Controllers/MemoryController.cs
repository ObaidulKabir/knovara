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
}
