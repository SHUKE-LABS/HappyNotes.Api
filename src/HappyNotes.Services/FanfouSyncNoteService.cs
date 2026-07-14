using HappyNotes.Common;
using HappyNotes.Common.Enums;
using HappyNotes.Entities;
using HappyNotes.Models;
using HappyNotes.Services.interfaces;
using HappyNotes.Services.SyncQueue.Interfaces;
using HappyNotes.Services.SyncQueue.Models;
using Microsoft.Extensions.Logging;

namespace HappyNotes.Services;

public class FanfouSyncNoteService(
    IFanfouUserAccountCacheService fanfouUserAccountCacheService,
    ISyncQueueService syncQueueService,
    ILogger<FanfouSyncNoteService> logger
)
    : ISyncNoteService
{
    public async Task SyncNewNote(Note note, string fullContent)
    {
        logger.LogInformation("Starting sync of new note {NoteId} for user {UserId} to Fanfou. ContentLength: {ContentLength}, IsPrivate: {IsPrivate}",
            note.Id, note.UserId, fullContent.Length, note.IsPrivate);

        try
        {
            var accounts = await _GetToSyncFanfouUserAccounts(note);
            if (accounts.Any())
            {
                foreach (var account in accounts)
                {
                    await EnqueueSyncTask(note, fullContent, account, "CREATE");
                    logger.LogDebug("Queued CREATE task for note {NoteId} to Fanfou account {AccountId}",
                        note.Id, account.Id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in SyncNewNote (Fanfou) for note {NoteId}", note.Id);
        }
    }

    public async Task SyncEditNote(Note note, string fullContent, Note existingNote)
    {
        logger.LogInformation("Starting sync edit of note {NoteId} for user {UserId} to Fanfou. ContentLength: {ContentLength}, IsPrivate: {IsPrivate}",
            note.Id, note.UserId, fullContent.Length, note.IsPrivate);

        try
        {
            var hasContentChange = _HasContentChanged(existingNote, note, fullContent);
            var accounts = await fanfouUserAccountCacheService.GetAsync(note.UserId);
            if (!accounts.Any())
            {
                logger.LogDebug("No Fanfou accounts found for user {UserId}, skipping sync edit of note {NoteId}",
                    note.UserId, note.Id);
                return;
            }

            var syncedInstances = _GetSyncedInstances(note);
            var toSyncAccounts = await _GetToSyncFanfouUserAccounts(note);

            var toBeUpdated = _GetInstancesToBeUpdated(syncedInstances, toSyncAccounts);
            var toBeRemoved = _GetInstancesToBeRemoved(syncedInstances, toSyncAccounts);
            var toBeSent = _GetAccountsToBeSent(syncedInstances, toSyncAccounts);

            logger.LogDebug("Edit sync plan for note {NoteId} (Fanfou): {ToSendCount} to send, {ToUpdateCount} to update, {ToRemoveCount} to remove",
                note.Id, toBeSent.Count, toBeUpdated.Count, toBeRemoved.Count);

            foreach (var instance in toBeRemoved)
            {
                var account = accounts.FirstOrDefault(s => s.Id.Equals(instance.UserAccountId));
                if (account != null)
                {
                    await EnqueueSyncTask(note, string.Empty, account, "DELETE", instance.StatusId);
                }
            }

            foreach (var instance in toBeUpdated)
            {
                var account = accounts.FirstOrDefault(s => s.Id.Equals(instance.UserAccountId));
                if (account == null) continue;
                if (hasContentChange)
                {
                    await EnqueueSyncTask(note, fullContent, account, "UPDATE", instance.StatusId);
                }
            }

            foreach (var account in toBeSent)
            {
                await EnqueueSyncTask(note, fullContent, account, "CREATE");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in SyncEditNote (Fanfou) for note {NoteId}", note.Id);
        }
    }

    public async Task SyncDeleteNote(Note note)
    {
        logger.LogInformation("Starting sync delete of note {NoteId} for user {UserId} from Fanfou", note.Id, note.UserId);

        if (string.IsNullOrWhiteSpace(note.FanfouStatusIds))
        {
            logger.LogDebug("Note {NoteId} has no Fanfou sync data, nothing to delete", note.Id);
            return;
        }

        try
        {
            var accounts = await fanfouUserAccountCacheService.GetAsync(note.UserId);
            if (!accounts.Any())
            {
                logger.LogDebug("No Fanfou accounts found for user {UserId}, skipping sync delete of note {NoteId}",
                    note.UserId, note.Id);
                return;
            }

            var syncedInstances = _GetSyncedInstances(note);
            foreach (var instance in syncedInstances)
            {
                var account = accounts.FirstOrDefault(s => s.Id.Equals(instance.UserAccountId));
                if (account != null)
                {
                    await EnqueueSyncTask(note, string.Empty, account, "DELETE", instance.StatusId);
                }
                else
                {
                    logger.LogWarning("Account {AccountId} not found for deleting Fanfou status {StatusId} from note {NoteId}",
                        instance.UserAccountId, instance.StatusId, note.Id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error enqueueing Fanfou delete tasks for note {NoteId}", note.Id);
        }
    }

    public async Task SyncUndeleteNote(Note note)
    {
        await Task.CompletedTask;
    }

    public Task PurgeDeletedNotes(long userId)
    {
        return Task.CompletedTask;
    }

    private async Task<IList<FanfouUserAccount>> _GetToSyncFanfouUserAccounts(Note note)
    {
        var result = new List<FanfouUserAccount>();
        var all = await fanfouUserAccountCacheService.GetAsync(note.UserId);
        foreach (var account in all)
        {
            switch (account.SyncType)
            {
                case FanfouSyncType.All:
                    result.Add(account);
                    break;
                case FanfouSyncType.PublicOnly:
                    if (!note.IsPrivate)
                    {
                        result.Add(account);
                    }

                    break;
                case FanfouSyncType.TagFanfouOnly:
                    if (note.TagList.Contains("fanfou"))
                    {
                        result.Add(account);
                    }

                    break;
            }
        }

        return result;
    }

    private List<FanfouSyncedInstance> _GetInstancesToBeUpdated(List<FanfouSyncedInstance> instances,
        IList<FanfouUserAccount> toSyncAccounts)
    {
        var idsToUpdate = instances.Select(i => i.UserAccountId).Intersect(toSyncAccounts.Select(t => t.Id)).ToList();
        return instances.Where(r => idsToUpdate.Contains(r.UserAccountId)).ToList();
    }

    private List<FanfouSyncedInstance> _GetInstancesToBeRemoved(List<FanfouSyncedInstance> instances,
        IList<FanfouUserAccount> toSyncAccounts)
    {
        if (!toSyncAccounts.Any()) return instances;

        var idsToRemove = instances.Select(s => s.UserAccountId).Except(toSyncAccounts.Select(t => t.Id)).ToList();
        return instances.Where(r => idsToRemove.Contains(r.UserAccountId)).ToList();
    }

    private List<FanfouUserAccount> _GetAccountsToBeSent(List<FanfouSyncedInstance> syncedInstances,
        IList<FanfouUserAccount> toSyncAccounts)
    {
        if (!syncedInstances.Any()) return toSyncAccounts.ToList();
        var toSendUserAccountId = toSyncAccounts.Select(t => t.Id).Except(syncedInstances.Select(s => s.UserAccountId))
            .ToList();
        return toSyncAccounts.Where(t => toSendUserAccountId.Contains(t.Id)).ToList();
    }

    private async Task EnqueueSyncTask(Note note, string fullContent, FanfouUserAccount account, string action, string? statusId = null)
    {
        var payload = new FanfouSyncPayload
        {
            UserAccountId = account.Id,
            FullContent = fullContent,
            StatusId = statusId,
            IsPrivate = note.IsPrivate,
            IsMarkdown = note.IsMarkdown
        };

        var task = SyncTask.Create(Constants.FanfouService, action, note.Id, note.UserId, payload);
        await syncQueueService.EnqueueAsync(Constants.FanfouService, task);

        logger.LogInformation("Enqueued Fanfou sync task {TaskId} for note {NoteId}, action: {Action}, account: {AccountId}",
            task.Id, note.Id, action, account.Id);
    }

    private static List<FanfouSyncedInstance> _GetSyncedInstances(Note note)
    {
        if (string.IsNullOrWhiteSpace(note.FanfouStatusIds)) return new List<FanfouSyncedInstance>();
        return note.FanfouStatusIds.Split(",").Select(s =>
        {
            var sync = s.Split(":");
            return new FanfouSyncedInstance
            {
                UserAccountId = long.Parse(sync[0]),
                StatusId = sync[1],
            };
        }).ToList();
    }

    private bool _HasContentChanged(Note existingNote, Note newNote, string newFullContent)
    {
        if (existingNote.Content != newFullContent)
        {
            return true;
        }

        if (existingNote.IsMarkdown != newNote.IsMarkdown)
        {
            return true;
        }

        return false;
    }
}
