using Api.Framework.Models;
using HappyNotes.Common.Enums;
using HappyNotes.Entities;
using HappyNotes.Models;
using HappyNotes.Repositories.interfaces;
using HappyNotes.Services.interfaces;
using HappyNotes.Services.SyncQueue.Interfaces;
using HappyNotes.Services.SyncQueue.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot.Exceptions;

namespace HappyNotes.Services;

public class MastodonSyncNoteService(
    IMastodonTootService mastodonTootService,
    INoteRepository noteRepository,
    IMastodonUserAccountCacheService mastodonUserAccountCacheService,
    IOptions<JwtConfig> jwtConfig,
    ISyncQueueService syncQueueService,
    ILogger<MastodonSyncNoteService> logger
)
    : ISyncNoteService
{
    private readonly JwtConfig _jwtConfig = jwtConfig.Value;
    private string TokenKey => _jwtConfig.SymmetricSecurityKey;

    public async Task SyncNewNote(Note note, string fullContent)
    {
        logger.LogInformation("Starting sync of new note {NoteId} for user {UserId}. ContentLength: {ContentLength}, IsPrivate: {IsPrivate}, IsMarkdown: {IsMarkdown}",
            note.Id, note.UserId, fullContent.Length, note.IsPrivate, note.IsMarkdown);

        try
        {
            var accounts = await _GetToSyncMastodonUserAccounts(note);
            logger.LogDebug("Found {AccountCount} Mastodon accounts to sync for note {NoteId}",
                accounts.Count, note.Id);

            if (accounts.Any())
            {
                // UNIFIED QUEUE MODE: Always use queue for consistent behavior
                foreach (var account in accounts)
                {
                    await EnqueueSyncTask(note, fullContent, account, "CREATE");
                    logger.LogDebug("Queued CREATE task for note {NoteId} to Mastodon account {AccountId} ({InstanceUrl})",
                        note.Id, account.Id, account.InstanceUrl);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in SyncNewNote for note {NoteId}", note.Id);
        }
    }

    public async Task SyncEditNote(Note note, string fullContent, Note existingNote)
    {
        logger.LogInformation("Starting sync edit of note {NoteId} for user {UserId}. ContentLength: {ContentLength}, IsPrivate: {IsPrivate}, IsMarkdown: {IsMarkdown}",
            note.Id, note.UserId, fullContent.Length, note.IsPrivate, note.IsMarkdown);

        try
        {
            var hasContentChange = _HasContentChanged(existingNote, note, fullContent);
            var accounts = await mastodonUserAccountCacheService.GetAsync(note.UserId);
            if (!accounts.Any())
            {
                logger.LogDebug("No Mastodon accounts found for user {UserId}, skipping sync edit of note {NoteId}",
                    note.UserId, note.Id);
                return;
            }

            var instanceData = await _GetInstancesData(note);
            var toBeSent = instanceData.toBeSent;
            var tobeRemoved = instanceData.toBeRemoved;
            var tobeUpdated = instanceData.toBeUpdated;

            logger.LogDebug("Edit sync plan for note {NoteId}: {ToSendCount} to send, {ToUpdateCount} to update, {ToRemoveCount} to remove",
                note.Id, toBeSent.Count, tobeUpdated.Count, tobeRemoved.Count);

            foreach (var instance in tobeRemoved)
            {
                var account = accounts.FirstOrDefault(s => s.Id.Equals(instance.UserAccountId));
                if (account != null)
                {
                    await EnqueueSyncTask(note, string.Empty, account, "DELETE", instance.TootId);
                    logger.LogDebug("Queued DELETE task for note {NoteId} from Mastodon account {AccountId} ({InstanceUrl})",
                        note.Id, account.Id, account.InstanceUrl);
                }
            }

            foreach (var instance in tobeUpdated)
            {
                var account = accounts.FirstOrDefault(s => s.Id.Equals(instance.UserAccountId));
                if (account == null) continue;
                if (hasContentChange)
                {
                    await EnqueueSyncTask(note, fullContent, account, "UPDATE", instance.TootId);
                    logger.LogDebug("Queued UPDATE task for note {NoteId} to Mastodon account {AccountId} ({InstanceUrl}) (content changed)",
                        note.Id, account.Id, account.InstanceUrl);
                }
            }

            foreach (var account in toBeSent)
            {
                await EnqueueSyncTask(note, fullContent, account, "CREATE");
                logger.LogDebug("Queued CREATE task for note {NoteId} to Mastodon account {AccountId} ({InstanceUrl})",
                    note.Id, account.Id, account.InstanceUrl);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in SyncEditNote for note {NoteId}", note.Id);
        }
    }

    public async Task SyncDeleteNote(Note note)
    {
        logger.LogInformation("Starting sync delete of note {NoteId} for user {UserId}", note.Id, note.UserId);

        if (!string.IsNullOrWhiteSpace(note.MastodonTootIds))
        {
            try
            {
                var accounts = await mastodonUserAccountCacheService.GetAsync(note.UserId);
                if (!accounts.Any())
                {
                    logger.LogDebug("No Mastodon accounts found for user {UserId}, skipping sync delete of note {NoteId}",
                        note.UserId, note.Id);
                    return;
                }

                var syncedInstances = _GetSyncedInstances(note);
                logger.LogDebug("Deleting note {NoteId} from {InstanceCount} Mastodon instances",
                    note.Id, syncedInstances.Count);

                foreach (var instance in syncedInstances)
                {
                    var account = accounts.FirstOrDefault(s => s.Id.Equals(instance.UserAccountId));
                    if (account != null)
                    {
                        await EnqueueSyncTask(note, string.Empty, account, "DELETE", instance.TootId);
                        logger.LogDebug("Queued DELETE task for note {NoteId} from Mastodon account {AccountId} ({InstanceUrl})",
                            note.Id, account.Id, account.InstanceUrl);
                    }
                    else
                    {
                        logger.LogWarning("Account {AccountId} not found for deleting toot {TootId} from note {NoteId}",
                            instance.UserAccountId, instance.TootId, note.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error enqueueing Mastodon delete tasks for note {NoteId}", note.Id);
            }
        }
        else
        {
            logger.LogDebug("Note {NoteId} has no Mastodon sync data, nothing to delete", note.Id);
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

    private async Task<(List<MastodonSyncedInstance> toBeUpdated, List<MastodonSyncedInstance> toBeRemoved,
        List<MastodonUserAccount> toBeSent)> _GetInstancesData(Note note)
    {
        var syncedChannels = _GetSyncedInstances(note);
        var toSyncInstances = await _GetToSyncMastodonUserAccounts(note);

        var toBeUpdated = _getInstancesToBeUpdated(syncedChannels, toSyncInstances);
        var toBeRemoved = _GetInstancesToBeRemoved(syncedChannels, toSyncInstances);
        var toBeSent = _GetAccountsToBeSent(syncedChannels, toSyncInstances);

        return (toBeUpdated, toBeRemoved, toBeSent);
    }

    private async Task<IList<MastodonUserAccount>> _GetToSyncMastodonUserAccounts(Note note)
    {
        var result = new List<MastodonUserAccount>();
        var all = await mastodonUserAccountCacheService.GetAsync(note.UserId);
        foreach (var account in all)
        {
            switch (account.SyncType)
            {
                case MastodonSyncType.All:
                    result.Add(account);
                    break;
                case MastodonSyncType.PublicOnly:
                    if (!note.IsPrivate)
                    {
                        result.Add(account);
                    }

                    break;
                case MastodonSyncType.TagMastodonOnly:
                    if (note.TagList.Contains("mastodon"))
                    {
                        result.Add(account);
                    }

                    break;
            }
        }

        return result;
    }

    private List<MastodonSyncedInstance> _getInstancesToBeUpdated(List<MastodonSyncedInstance> instances,
        IList<MastodonUserAccount> toSyncInstances)
    {
        var idsToUpdate = instances.Select(i => i.UserAccountId).Intersect(toSyncInstances.Select(t => t.Id)).ToList();
        return instances.Where(r => idsToUpdate.Contains(r.UserAccountId)).ToList();
    }

    private List<MastodonSyncedInstance> _GetInstancesToBeRemoved(List<MastodonSyncedInstance> instances,
        IList<MastodonUserAccount> toSyncAccounts)
    {
        if (!toSyncAccounts.Any()) return instances;

        var idsToRemove = instances.Select(s => s.UserAccountId).Except(toSyncAccounts.Select(t => t.Id)).ToList();
        return instances.Where(r => idsToRemove.Contains(r.UserAccountId)).ToList();
    }

    private List<MastodonUserAccount> _GetAccountsToBeSent(List<MastodonSyncedInstance> syncedInstances,
        IList<MastodonUserAccount> toSyncAccounts)
    {
        if (!syncedInstances.Any()) return toSyncAccounts.ToList();
        var toSendUserAccountId = toSyncAccounts.Select(t => t.Id).Except(syncedInstances.Select(s => s.UserAccountId))
            .ToList();
        return toSyncAccounts.Where(t => toSendUserAccountId.Contains(t.Id)).ToList();
    }

    private async Task _DeleteToot(MastodonSyncedInstance instance, MastodonUserAccount account)
    {
        logger.LogDebug("Deleting toot {TootId} from account {AccountId} ({InstanceUrl})",
            instance.TootId, account.Id, account.InstanceUrl);

        try
        {
            await mastodonTootService.DeleteTootAsync(account.InstanceUrl,
                account.DecryptedAccessToken(TokenKey), instance.TootId);

            logger.LogDebug("Successfully deleted toot {TootId} from {InstanceUrl}",
                instance.TootId, account.InstanceUrl);
        }
        catch (ApiRequestException ex)
        {
            logger.LogError(ex, "ApiRequestException while deleting toot {TootId} from {InstanceUrl}: {ErrorMessage}",
                instance.TootId, account.InstanceUrl, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete toot {TootId} from {InstanceUrl}: {ErrorMessage}",
                instance.TootId, account.InstanceUrl, ex.Message);
        }
    }

    private async Task EnqueueSyncTask(Note note, string fullContent, MastodonUserAccount account, string action, string? tootId = null)
    {
        var payload = new MastodonSyncPayload
        {
            InstanceUrl = account.InstanceUrl,
            UserAccountId = account.Id,
            FullContent = fullContent,
            TootId = tootId,
            IsPrivate = note.IsPrivate,
            IsMarkdown = note.IsMarkdown
        };

        var task = SyncTask.Create("mastodon", action, note.Id, note.UserId, payload);
        await syncQueueService.EnqueueAsync("mastodon", task);

        logger.LogInformation("Enqueued Mastodon sync task {TaskId} for note {NoteId}, action: {Action}, account: {AccountId}",
            task.Id, note.Id, action, account.Id);
    }

    private static List<MastodonSyncedInstance> _GetSyncedInstances(Note note)
    {
        if (string.IsNullOrWhiteSpace(note.MastodonTootIds)) return new List<MastodonSyncedInstance>();
        return note.MastodonTootIds.Split(",").Select(s =>
        {
            var sync = s.Split(":");
            return new MastodonSyncedInstance
            {
                UserAccountId = long.Parse(sync[0]),
                TootId = sync[1],
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

