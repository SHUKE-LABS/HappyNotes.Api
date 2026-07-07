using System.Text.Json;
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

public class ManticoreSearchSyncHandler : ISyncHandler
{
    private readonly ISearchService _searchService;
    private readonly INoteRepository _noteRepository;
    private readonly SyncQueueOptions _options;
    private readonly ILogger<ManticoreSearchSyncHandler> _logger;

    public string ServiceName => Constants.ManticoreSearchService;

    public int MaxRetryAttempts => _options.Handlers.TryGetValue(ServiceName, out var config)
        ? config.MaxRetries
        : 3;

    public ManticoreSearchSyncHandler(
        ISearchService searchService,
        INoteRepository noteRepository,
        IOptions<SyncQueueOptions> options,
        ILogger<ManticoreSearchSyncHandler> logger)
    {
        _searchService = searchService;
        _noteRepository = noteRepository;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SyncResult> ProcessAsync(SyncTask task, CancellationToken cancellationToken = default)
    {
        try
        {
            ManticoreSearchSyncPayload? payload;

            // Handle both typed and untyped task objects
            if (task.Payload is JsonElement jsonElement)
            {
                payload = JsonSerializer.Deserialize<ManticoreSearchSyncPayload>(jsonElement.GetRawText(), JsonSerializerConfig.Default);
            }
            else if (task.Payload is ManticoreSearchSyncPayload manticorePayload)
            {
                payload = manticorePayload;
            }
            else
            {
                return SyncResult.Failure("Invalid payload type", shouldRetry: false);
            }

            if (payload == null)
            {
                return SyncResult.Failure("Failed to deserialize ManticoreSearch sync payload", shouldRetry: false);
            }

            _logger.LogDebug("Processing ManticoreSearch sync for note {NoteId}, action: {Action}", task.EntityId, payload.Action);

            if (payload.Action == "PURGE")
            {
                var purgeSuccess = await HandlePurgeAsync(task.UserId);
                return purgeSuccess ? SyncResult.Success() : SyncResult.Failure("Failed to PURGE user deleted notes from ManticoreSearch");
            }

            var note = await _noteRepository.Get(task.EntityId);
            if (note is null)
            {
                _logger.LogWarning("Note {NoteId} not found for ManticoreSearch sync", task.EntityId);
                // Consider delete successful if note doesn't exist, otherwise it's a failure
                return payload.Action == "DELETE" ? SyncResult.Success() : SyncResult.Failure("Note not found", shouldRetry: false);
            }

            var success = payload.Action switch
            {
                "CREATE" => await HandleCreateAsync(note, payload),
                "UPDATE" => await HandleUpdateAsync(note, payload),
                "DELETE" => await HandleDeleteAsync(note),
                "UNDELETE" => await HandleUndeleteAsync(note),
                _ => throw new InvalidOperationException($"Unknown ManticoreSearch sync action: {payload.Action}")
            };

            return success ? SyncResult.Success() : SyncResult.Failure($"Failed to {payload.Action} note in ManticoreSearch");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing ManticoreSearch sync for note {NoteId}", task.EntityId);
            return SyncResult.Failure(ex.Message);
        }
    }

    private async Task<bool> HandleCreateAsync(Note note, ManticoreSearchSyncPayload payload)
    {
        try
        {
            await _searchService.SyncNoteToIndexAsync(note, payload.FullContent);
            _logger.LogDebug("Successfully created ManticoreSearch index entry for note {NoteId}", note.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create ManticoreSearch index entry for note {NoteId}", note.Id);
            return false;
        }
    }

    private async Task<bool> HandleUpdateAsync(Note note, ManticoreSearchSyncPayload payload)
    {
        try
        {
            await _searchService.SyncNoteToIndexAsync(note, payload.FullContent);
            _logger.LogDebug("Successfully updated ManticoreSearch index entry for note {NoteId}", note.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update ManticoreSearch index entry for note {NoteId}", note.Id);
            return false;
        }
    }

    private async Task<bool> HandleDeleteAsync(Note note)
    {
        try
        {
            await _searchService.DeleteNoteFromIndexAsync(note.Id);
            _logger.LogDebug("Successfully deleted ManticoreSearch index entry for note {NoteId}", note.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete ManticoreSearch index entry for note {NoteId}", note.Id);
            return false;
        }
    }

    private async Task<bool> HandlePurgeAsync(long userId)
    {
        try
        {
            await _searchService.PurgeUserDeletedNotesFromIndexAsync(userId);
            _logger.LogDebug("Successfully purged deleted notes from ManticoreSearch index for user {UserId}", userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to purge deleted notes from ManticoreSearch index for user {UserId}", userId);
            return false;
        }
    }

    private async Task<bool> HandleUndeleteAsync(Note note)
    {
        try
        {
            await _searchService.UndeleteNoteFromIndexAsync(note.Id);
            _logger.LogDebug("Successfully undeleted ManticoreSearch index entry for note {NoteId}", note.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to undelete ManticoreSearch index entry for note {NoteId}", note.Id);
            return false;
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

        // Default exponential backoff: 1min, 2min, 4min
        return TimeSpan.FromMinutes(Math.Pow(2, attemptCount));
    }
}
