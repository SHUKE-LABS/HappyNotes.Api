using HappyNotes.Common;
using HappyNotes.Services.SyncQueue.Interfaces;
using HappyNotes.Services.SyncQueue.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HappyNotes.Api.Controllers;

[ApiController]
[Route("api/admin/sync-queue")]
[Authorize(Policy = "Admin")]
public class SyncQueueAdminController : ControllerBase
{
    private readonly ISyncQueueService _queueService;
    private readonly ILogger<SyncQueueAdminController> _logger;

    public SyncQueueAdminController(
        ISyncQueueService queueService,
        ILogger<SyncQueueAdminController> logger)
    {
        _queueService = queueService;
        _logger = logger;
    }

    /// <summary>
    /// Get queue statistics for a specific service
    /// </summary>
    [HttpGet("stats/{service}")]
    public async Task<ActionResult<QueueStats>> GetStats(string service)
    {
        try
        {
            var stats = await _queueService.GetStatsAsync(service);
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting stats for service {Service}", service);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get stats for all known services
    /// </summary>
    [HttpGet("stats")]
    public async Task<ActionResult<Dictionary<string, QueueStats>>> GetAllStats()
    {
        try
        {
            var services = Constants.AllSyncServices;
            var stats = new Dictionary<string, QueueStats>();

            foreach (var service in services)
            {
                stats[service] = await _queueService.GetStatsAsync(service);
            }

            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all queue stats");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Retry all failed tasks for a service
    /// </summary>
    [HttpPost("{service}/retry-failed")]
    public async Task<ActionResult> RetryFailed(string service)
    {
        try
        {
            await _queueService.RetryFailedTasksAsync(service);
            _logger.LogInformation("Retried failed tasks for service {Service}", service);
            return Ok(new { message = $"Failed tasks for {service} have been queued for retry" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrying failed tasks for service {Service}", service);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Clear all queues for a service (use with caution!)
    /// </summary>
    [HttpDelete("{service}/clear")]
    public async Task<ActionResult> ClearQueue(string service)
    {
        try
        {
            await _queueService.ClearQueueAsync(service);
            _logger.LogWarning("Cleared all queues for service {Service}", service);
            return Ok(new { message = $"All queues for {service} have been cleared" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing queues for service {Service}", service);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Manually recover expired tasks from processing queue
    /// </summary>
    [HttpPost("{service}/recover")]
    public async Task<ActionResult> RecoverExpiredTasks(string service)
    {
        try
        {
            await _queueService.RecoverExpiredTasksAsync(service);
            _logger.LogInformation("Manually recovered expired tasks for service {Service}", service);
            return Ok(new { message = $"Recovery completed for {service}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recovering expired tasks for service {Service}", service);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Health check endpoint for monitoring
    /// </summary>
    [HttpGet("health")]
    [AllowAnonymous]
    public async Task<ActionResult> HealthCheck()
    {
        try
        {
            var services = Constants.AllSyncServices;
            var healthStatus = new Dictionary<string, object>();

            foreach (var service in services)
            {
                await _queueService.GetStatsAsync(service);
                healthStatus[service] = new { status = "healthy" };
            }

            return Ok(new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow,
                services = healthStatus
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");
            return StatusCode(503, new
            {
                status = "unhealthy",
                error = ex.Message,
                timestamp = DateTime.UtcNow
            });
        }
    }
}
