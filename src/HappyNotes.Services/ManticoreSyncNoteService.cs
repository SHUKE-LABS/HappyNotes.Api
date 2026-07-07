using HappyNotes.Entities;
using HappyNotes.Services.interfaces;
using HappyNotes.Services.SyncQueue.Interfaces;
using HappyNotes.Services.SyncQueue.Models;
using Microsoft.Extensions.Logging;

namespace HappyNotes.Services;

public class ManticoreSyncNoteService(
    ISyncQueueService syncQueueService,
    ILogger<ManticoreSyncNoteService> logger
) : ISyncNoteService
{
    public async Task SyncNewNote(Note note, string fullContent)
    {
        try
        {
            var payload = new ManticoreSearchSyncPayload
            {
                Action = "CREATE",
                FullContent = fullContent
            };

            var task = SyncTask.Create("manticoresearch", "CREATE", note.Id, note.UserId, payload);
            await syncQueueService.EnqueueAsync("manticoresearch", task);

            logger.LogDebug("Successfully queued ManticoreSearch CREATE for note {NoteId}", note.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to queue new note sync to ManticoreSearch: {NoteId}", note.Id);
        }
    }

    public async Task SyncEditNote(Note note, string fullContent, Note originalNote)
    {
        try
        {
            var payload = new ManticoreSearchSyncPayload
            {
                Action = "UPDATE",
                FullContent = fullContent
            };

            var task = SyncTask.Create("manticoresearch", "UPDATE", note.Id, note.UserId, payload);
            await syncQueueService.EnqueueAsync("manticoresearch", task);

            logger.LogDebug("Successfully queued ManticoreSearch UPDATE for note {NoteId}", note.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to queue edited note sync to ManticoreSearch: {NoteId}", note.Id);
        }
    }

    public async Task SyncDeleteNote(Note note)
    {
        try
        {
            var payload = new ManticoreSearchSyncPayload
            {
                Action = "DELETE",
                FullContent = string.Empty // Not needed for delete operations
            };

            var task = SyncTask.Create("manticoresearch", "DELETE", note.Id, note.UserId, payload);
            await syncQueueService.EnqueueAsync("manticoresearch", task);

            logger.LogDebug("Successfully queued ManticoreSearch DELETE for note {NoteId}", note.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to queue delete note sync to ManticoreSearch: {NoteId}", note.Id);
        }
    }

    public async Task SyncUndeleteNote(Note note)
    {
        try
        {
            var payload = new ManticoreSearchSyncPayload
            {
                Action = "UNDELETE",
                FullContent = string.Empty // Not needed for undelete operations
            };

            var task = SyncTask.Create("manticoresearch", "UNDELETE", note.Id, note.UserId, payload);
            await syncQueueService.EnqueueAsync("manticoresearch", task);

            logger.LogDebug("Successfully queued ManticoreSearch UNDELETE for note {NoteId}", note.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to queue undelete note sync to ManticoreSearch: {NoteId}", note.Id);
        }
    }

    public async Task PurgeDeletedNotes(long userId)
    {
        try
        {
            var payload = new ManticoreSearchSyncPayload
            {
                Action = "PURGE",
                FullContent = string.Empty
            };

            var task = SyncTask.Create("manticoresearch", "PURGE", 0, userId, payload);
            await syncQueueService.EnqueueAsync("manticoresearch", task);

            logger.LogDebug("Successfully queued ManticoreSearch PURGE for user {UserId}", userId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to queue purge sync to ManticoreSearch for user {UserId}", userId);
        }
    }
}
