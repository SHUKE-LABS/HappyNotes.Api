using System.Text.Json;
using Api.Framework.Helper;
using Api.Framework.Models;
using HappyNotes.Common;
using HappyNotes.Entities;
using HappyNotes.Repositories.interfaces;
using HappyNotes.Services.interfaces;
using HappyNotes.Services.SyncQueue.Configuration;
using HappyNotes.Services.SyncQueue.Interfaces;
using HappyNotes.Services.SyncQueue.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HappyNotes.Services.SyncQueue.Handlers;

public class FanfouSyncHandler : ISyncHandler
{
    private readonly IFanfouService _fanfouService;
    private readonly IFanfouUserAccountCacheService _fanfouUserAccountCacheService;
    private readonly INoteRepository _noteRepository;
    private readonly SyncQueueOptions _options;
    private readonly JwtConfig _jwtConfig;
    private readonly ILogger<FanfouSyncHandler> _logger;

    public string ServiceName => Constants.FanfouService;

    public int MaxRetryAttempts => _options.Handlers.TryGetValue(ServiceName, out var config)
        ? config.MaxRetries
        : 5;

    public FanfouSyncHandler(
        IFanfouService fanfouService,
        IFanfouUserAccountCacheService fanfouUserAccountCacheService,
        INoteRepository noteRepository,
        IOptions<SyncQueueOptions> options,
        IOptions<JwtConfig> jwtConfig,
        ILogger<FanfouSyncHandler> logger)
    {
        _fanfouService = fanfouService;
        _fanfouUserAccountCacheService = fanfouUserAccountCacheService;
        _noteRepository = noteRepository;
        _options = options.Value;
        _jwtConfig = jwtConfig.Value;
        _logger = logger;
    }

    public async Task<SyncResult> ProcessAsync(SyncTask task, CancellationToken cancellationToken)
    {
        try
        {
            FanfouSyncPayload? payload;

            // Handle both typed and untyped task objects
            if (task.Payload is JsonElement jsonElement)
            {
                payload = JsonSerializer.Deserialize<FanfouSyncPayload>(jsonElement.GetRawText(), JsonSerializerConfig.Default);
            }
            else if (task.Payload is FanfouSyncPayload fanfouPayload)
            {
                payload = fanfouPayload;
            }
            else
            {
                return SyncResult.Failure("Invalid payload type", shouldRetry: false);
            }

            if (payload == null)
            {
                return SyncResult.Failure("Failed to deserialize payload", shouldRetry: false);
            }

            // Security: Get access credentials from user account cache instead of payload
            var userAccounts = await _fanfouUserAccountCacheService.GetAsync(task.UserId);
            var userAccount = userAccounts.FirstOrDefault(a => a.Id == payload.UserAccountId);

            if (userAccount == null)
            {
                return SyncResult.Failure($"No Fanfou user account found for user {task.UserId} and account {payload.UserAccountId}", shouldRetry: false);
            }

            var accessToken = userAccount.DecryptedAccessToken(_jwtConfig.SymmetricSecurityKey);
            var accessTokenSecret = userAccount.DecryptedAccessTokenSecret(_jwtConfig.SymmetricSecurityKey);

            return task.Action.ToUpper() switch
            {
                "CREATE" => await ProcessCreateAction(task, payload, accessToken, accessTokenSecret),
                "UPDATE" => await ProcessUpdateAction(task, payload, accessToken, accessTokenSecret),
                "DELETE" => await ProcessDeleteAction(task, payload, accessToken, accessTokenSecret),
                _ => SyncResult.Failure($"Unknown action: {task.Action}", shouldRetry: false)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Fanfou sync task {TaskId} for user {UserId}: {Error}",
                task.Id, task.UserId, ex.Message);
            return SyncResult.Failure(ex.Message);
        }
    }

    private async Task<SyncResult> ProcessCreateAction(SyncTask task, FanfouSyncPayload payload, string accessToken, string accessTokenSecret)
    {
        try
        {
            _logger.LogDebug("Processing CREATE action for task {TaskId}", task.Id);

            var statusId = await _fanfouService.SendStatusAsync(accessToken, accessTokenSecret, payload.FullContent, task.EntityId);

            // Add the status ID to the note
            await AddStatusIdToNote(task.EntityId, payload.UserAccountId, statusId);

            _logger.LogDebug("Successfully created Fanfou status {StatusId} for task {TaskId}", statusId, task.Id);

            return SyncResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Fanfou status for task {TaskId}: {Error}", task.Id, ex.Message);
            return SyncResult.Failure(ex.Message);
        }
    }

    private async Task<SyncResult> ProcessUpdateAction(SyncTask task, FanfouSyncPayload payload, string accessToken, string accessTokenSecret)
    {
        try
        {
            // Fanfou has no edit API, so an update = destroy the old status + post a new one.
            if (string.IsNullOrEmpty(payload.StatusId))
            {
                return SyncResult.Failure("StatusId is required for UPDATE action", shouldRetry: false);
            }

            _logger.LogDebug("Processing UPDATE action for task {TaskId}, replacing status {StatusId}", task.Id, payload.StatusId);

            await _fanfouService.DeleteStatusAsync(accessToken, accessTokenSecret, payload.StatusId);
            var newStatusId = await _fanfouService.SendStatusAsync(accessToken, accessTokenSecret, payload.FullContent, task.EntityId);

            // Replace the old status id with the new one on the note.
            await AddStatusIdToNote(task.EntityId, payload.UserAccountId, newStatusId);

            _logger.LogDebug("Successfully replaced Fanfou status {OldStatusId} with {NewStatusId} for task {TaskId}",
                payload.StatusId, newStatusId, task.Id);

            return SyncResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Fanfou status {StatusId} for task {TaskId}: {Error}",
                payload.StatusId, task.Id, ex.Message);
            return SyncResult.Failure(ex.Message);
        }
    }

    private async Task<SyncResult> ProcessDeleteAction(SyncTask task, FanfouSyncPayload payload, string accessToken, string accessTokenSecret)
    {
        try
        {
            if (string.IsNullOrEmpty(payload.StatusId))
            {
                return SyncResult.Failure("StatusId is required for DELETE action", shouldRetry: false);
            }

            _logger.LogDebug("Processing DELETE action for task {TaskId}, status {StatusId}", task.Id, payload.StatusId);

            await _fanfouService.DeleteStatusAsync(accessToken, accessTokenSecret, payload.StatusId);

            // Remove the status ID from the note
            await RemoveStatusIdFromNote(task.EntityId, payload.UserAccountId, payload.StatusId);

            _logger.LogDebug("Successfully deleted Fanfou status {StatusId} for task {TaskId}", payload.StatusId, task.Id);

            return SyncResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete Fanfou status {StatusId} for task {TaskId}: {Error}",
                payload.StatusId, task.Id, ex.Message);
            return SyncResult.Failure(ex.Message);
        }
    }

    public TimeSpan CalculateRetryDelay(int attemptCount)
    {
        if (_options.Handlers.TryGetValue(ServiceName, out var config))
        {
            var delay = TimeSpan.FromSeconds(config.BaseDelaySeconds * Math.Pow(config.BackoffMultiplier, attemptCount - 1));
            var maxDelay = TimeSpan.FromMinutes(config.MaxDelayMinutes);
            return delay > maxDelay ? maxDelay : delay;
        }

        // Default fallback
        return TimeSpan.FromMinutes(Math.Min(Math.Pow(2, attemptCount - 1), 30));
    }

    private async Task AddStatusIdToNote(long noteId, long userAccountId, string statusId)
    {
        var currentNote = await _noteRepository.GetFirstOrDefaultAsync(
            n => n.Id == noteId,
            orderBy: null);

        if (currentNote == null)
        {
            _logger.LogWarning("Note {NoteId} not found, cannot add Fanfou status ID", noteId);
            return;
        }

        currentNote.AddFanfouStatusId(userAccountId, statusId);

        // Use precise update - only update FanfouStatusIds field
        await _noteRepository.UpdateAsync(
            note => new Note { FanfouStatusIds = currentNote.FanfouStatusIds },
            note => note.Id == noteId);

        _logger.LogDebug("Updated note {NoteId} FanfouStatusIds after adding {UserAccountId}:{StatusId}",
            noteId, userAccountId, statusId);
    }

    private async Task RemoveStatusIdFromNote(long noteId, long userAccountId, string statusId)
    {
        var currentNote = await _noteRepository.GetFirstOrDefaultAsync(
            n => n.Id == noteId,
            orderBy: null);

        if (currentNote == null || string.IsNullOrWhiteSpace(currentNote.FanfouStatusIds))
        {
            _logger.LogWarning("Note {NoteId} not found or has no Fanfou status IDs", noteId);
            return;
        }

        currentNote.RemoveFanfouStatusId(userAccountId, statusId);

        // Use precise update - only update FanfouStatusIds field
        await _noteRepository.UpdateAsync(
            note => new Note { FanfouStatusIds = currentNote.FanfouStatusIds },
            note => note.Id == noteId);

        _logger.LogDebug("Updated note {NoteId} FanfouStatusIds after removing {UserAccountId}:{StatusId}",
            noteId, userAccountId, statusId);
    }
}
