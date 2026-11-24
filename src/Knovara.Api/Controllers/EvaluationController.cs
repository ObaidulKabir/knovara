using Knovara.Core.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Knovara.Api.Controllers;

[ApiController]
[Route("api/evaluation")]
public sealed class EvaluationController : ControllerBase
{
    private readonly IEvaluationService _evaluation;

    public EvaluationController(IEvaluationService evaluation)
    {
        _evaluation = evaluation;
    }

    /// <summary>Recent per-task metrics — see system_specifications.md "Evaluation Plan".</summary>
    [HttpGet("recent")]
    public async Task<ActionResult<IReadOnlyList<TaskMetrics>>> Recent([FromQuery] int count = 50, CancellationToken ct = default)
        => Ok(await _evaluation.GetRecentAsync(count, ct));
}
