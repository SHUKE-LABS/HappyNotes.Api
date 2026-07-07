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

public class MastodonSyncHandler : ISyncHandler
{
    private readonly IMastodonTootService _mastodonTootService;
    private readonly IMastodonUserAccountCacheService _mastodonUserAccountCacheService;
    private readonly INoteRepository _noteRepository;
    private readonly SyncQueueOptions _options;
    private readonly JwtConfig _jwtConfig;
    private readonly ILogger<MastodonSyncHandler> _logger;

    public string ServiceName => Constants.MastodonService;

    public int MaxRetryAttempts => _options.Handlers.TryGetValue(ServiceName, out var config)
        ? config.MaxRetries
        : 5;

    public MastodonSyncHandler(
        IMastodonTootService mastodonTootService,
        IMastodonUserAccountCacheService mastodonUserAccountCacheService,
        INoteRepository noteRepository,
        IOptions<SyncQueueOptions> options,
        IOptions<JwtConfig> jwtConfig,
        ILogger<MastodonSyncHandler> logger)
    {
        _mastodonTootService = mastodonTootService;
        _mastodonUserAccountCacheService = mastodonUserAccountCacheService;
        _noteRepository = noteRepository;
        _options = options.Value;
        _jwtConfig = jwtConfig.Value;
        _logger = logger;
    }

    public async Task<SyncResult> ProcessAsync(SyncTask task, CancellationToken cancellationToken)
    {
        try
        {
            MastodonSyncPayload? payload;

            // Handle both typed and untyped task objects
            if (task.Payload is JsonElement jsonElement)
            {
                payload = JsonSerializer.Deserialize<MastodonSyncPayload>(jsonElement.GetRawText(), JsonSerializerConfig.Default);
            }
            else if (task.Payload is MastodonSyncPayload mastodonPayload)
            {
                payload = mastodonPayload;
            }
            else
            {
                return SyncResult.Failure("Invalid payload type", shouldRetry: false);
            }

            if (payload == null)
            {
                return SyncResult.Failure("Failed to deserialize payload", shouldRetry: false);
            }

            // Security: Get AccessToken from user account cache instead of payload
            var userAccounts = await _mastodonUserAccountCacheService.GetAsync(task.UserId);
            var userAccount = userAccounts.FirstOrDefault(a => a.Id == payload.UserAccountId);

            if (userAccount == null)
            {
                return SyncResult.Failure($"No Mastodon user account found for user {task.UserId} and account {payload.UserAccountId}", shouldRetry: false);
            }

            var accessToken = userAccount.DecryptedAccessToken(_jwtConfig.SymmetricSecurityKey);

            return task.Action.ToUpper() switch
            {
                "CREATE" => await ProcessCreateAction(task, payload, accessToken),
                "UPDATE" => await ProcessUpdateAction(task, payload, accessToken),
                "DELETE" => await ProcessDeleteAction(task, payload, accessToken),
                _ => SyncResult.Failure($"Unknown action: {task.Action}", shouldRetry: false)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Mastodon sync task {TaskId} for user {UserId}: {Error}",
                task.Id, task.UserId, ex.Message);
            return SyncResult.Failure(ex.Message);
        }
    }

    private async Task<SyncResult> ProcessCreateAction(SyncTask task, MastodonSyncPayload payload, string accessToken)
    {
        try
        {
            _logger.LogDebug("Processing CREATE action for task {TaskId} to {InstanceUrl}",
                task.Id, payload.InstanceUrl);

            var status = await _mastodonTootService.SendTootAsync(
                payload.InstanceUrl,
                accessToken,
                payload.FullContent,
                payload.IsPrivate,
                payload.IsMarkdown,
                task.EntityId,
                task.UserId);

            // Add the toot ID to the note
            await AddTootIdToNote(task.EntityId, payload.UserAccountId, status.Id);

            _logger.LogDebug("Successfully created toot {TootId} for task {TaskId}",
                status.Id, task.Id);

            return SyncResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create toot for task {TaskId}: {Error}",
                task.Id, ex.Message);
            return SyncResult.Failure(ex.Message);
        }
    }

    private async Task<SyncResult> ProcessUpdateAction(SyncTask task, MastodonSyncPayload payload, string accessToken)
    {
        try
        {
            if (string.IsNullOrEmpty(payload.TootId))
            {
                return SyncResult.Failure("TootId is required for UPDATE action", shouldRetry: false);
            }

            _logger.LogDebug("Processing UPDATE action for task {TaskId}, toot {TootId} on {InstanceUrl}",
                task.Id, payload.TootId, payload.InstanceUrl);

            await _mastodonTootService.EditTootAsync(
                payload.InstanceUrl,
                accessToken,
                payload.TootId,
                payload.FullContent,
                payload.IsPrivate,
                payload.IsMarkdown,
                task.EntityId,
                task.UserId);

            _logger.LogDebug("Successfully updated toot {TootId} for task {TaskId}",
                payload.TootId, task.Id);

            return SyncResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update toot {TootId} for task {TaskId}: {Error}",
                payload.TootId, task.Id, ex.Message);
            return SyncResult.Failure(ex.Message);
        }
    }

    private async Task<SyncResult> ProcessDeleteAction(SyncTask task, MastodonSyncPayload payload, string accessToken)
    {
        try
        {
            if (string.IsNullOrEmpty(payload.TootId))
            {
                return SyncResult.Failure("TootId is required for DELETE action", shouldRetry: false);
            }

            _logger.LogDebug("Processing DELETE action for task {TaskId}, toot {TootId} on {InstanceUrl}",
                task.Id, payload.TootId, payload.InstanceUrl);

            await _mastodonTootService.DeleteTootAsync(
                payload.InstanceUrl,
                accessToken,
                payload.TootId);

            // Remove the toot ID from the note
            await RemoveTootIdFromNote(task.EntityId, payload.UserAccountId, payload.TootId);

            _logger.LogDebug("Successfully deleted toot {TootId} for task {TaskId}",
                payload.TootId, task.Id);

            return SyncResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete toot {TootId} for task {TaskId}: {Error}",
                payload.TootId, task.Id, ex.Message);
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

    private async Task AddTootIdToNote(long noteId, long userAccountId, string tootId)
    {
        // First get current MastodonTootIds to compute the new value
        var currentNote = await _noteRepository.GetFirstOrDefaultAsync(
            n => n.Id == noteId,
            orderBy: null);

        if (currentNote == null)
        {
            _logger.LogWarning("Note {NoteId} not found, cannot add Mastodon toot ID", noteId);
            return;
        }

        currentNote.AddMastodonTootId(userAccountId, tootId);

        // Use precise update - only update MastodonTootIds field
        await _noteRepository.UpdateAsync(
            note => new Note { MastodonTootIds = currentNote.MastodonTootIds },
            note => note.Id == noteId);

        _logger.LogDebug("Updated note {NoteId} MastodonTootIds after adding {UserAccountId}:{TootId}",
            noteId, userAccountId, tootId);
    }

    private async Task RemoveTootIdFromNote(long noteId, long userAccountId, string tootId)
    {
        // Get current MastodonTootIds to compute the new value
        var currentNote = await _noteRepository.GetFirstOrDefaultAsync(
            n => n.Id == noteId,
            orderBy: null);

        if (currentNote == null || string.IsNullOrWhiteSpace(currentNote.MastodonTootIds))
        {
            _logger.LogWarning("Note {NoteId} not found or has no Mastodon toot IDs", noteId);
            return;
        }

        currentNote.RemoveMastodonTootId(userAccountId, tootId);

        // Use precise update - only update MastodonTootIds field
        await _noteRepository.UpdateAsync(
            note => new Note { MastodonTootIds = currentNote.MastodonTootIds },
            note => note.Id == noteId);

        _logger.LogDebug("Updated note {NoteId} MastodonTootIds after removing {UserAccountId}:{TootId}",
            noteId, userAccountId, tootId);
    }
}
