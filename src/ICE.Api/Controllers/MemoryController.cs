using ICE.Core.Contracts;
using ICE.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ICE.Api.Controllers;

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
    public async Task<ActionResult<SessionKnowledge>> GetSession(string sessionId, CancellationToken ct)
    {
        var knowledge = await _memory.GetSessionKnowledgeAsync(sessionId, ct);
        return knowledge is null ? NotFound() : Ok(knowledge);
    }

    [HttpPut("sessions/{sessionId}")]
    public async Task<IActionResult> SaveSession(string sessionId, [FromBody] SessionKnowledge knowledge, CancellationToken ct)
    {
        if (sessionId != knowledge.SessionId) return BadRequest("sessionId mismatch");
        await _memory.SaveSessionKnowledgeAsync(knowledge, ct);
        return NoContent();
    }
}
