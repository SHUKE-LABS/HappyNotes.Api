using HappyNotes.Services.interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HappyNotes.Api.Controllers;

/// <summary>
/// Admin-only trigger for the resumable semantic-search vector backfill. Run off-peak; safe to rerun —
/// a completed run is a no-op and an interrupted run resumes from its cursor.
/// </summary>
[ApiController]
[Route("api/admin/vector-backfill")]
[Authorize(Policy = "Admin")]
public class VectorBackfillAdminController : ControllerBase
{
    private readonly INoteVectorBackfillService _backfillService;
    private readonly ILogger<VectorBackfillAdminController> _logger;

    public VectorBackfillAdminController(
        INoteVectorBackfillService backfillService,
        ILogger<VectorBackfillAdminController> logger)
    {
        _backfillService = backfillService;
        _logger = logger;
    }

    /// <summary>
    /// Runs the backfill to completion (or until the embedding backend becomes unavailable, in which case
    /// it stops and can be rerun to resume). Optional batchSize overrides the configured default.
    /// </summary>
    [HttpPost("run")]
    public async Task<ActionResult<VectorBackfillResult>> Run(int? batchSize = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _backfillService.RunAsync(batchSize, cancellationToken);
            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, "Backfill cancelled by client");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vector backfill failed");
            return StatusCode(500, "Internal server error");
        }
    }
}
