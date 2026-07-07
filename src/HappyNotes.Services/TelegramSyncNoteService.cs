using HappyNotes.Common;
using HappyNotes.Common.Enums;
using HappyNotes.Entities;
using HappyNotes.Models;
using HappyNotes.Services.interfaces;
using HappyNotes.Services.SyncQueue.Interfaces;
using HappyNotes.Services.SyncQueue.Models;
using Microsoft.Extensions.Logging;

namespace HappyNotes.Services;

public class TelegramSyncNoteService(
    ITelegramSettingsCacheService telegramSettingsCacheService,
    ISyncQueueService syncQueueService,
    ILogger<TelegramSyncNoteService> logger
)
    : ISyncNoteService
{
    public async Task SyncNewNote(Note note, string fullContent)
    {
        try
        {
            var validSettings = await telegramSettingsCacheService.GetAsync(note.UserId);
            if (validSettings.Any())
            {
                var toSyncChannelIds = (await _GetRequiredChannelData(note)).toBeSent;
                if (toSyncChannelIds.Any())
                {
                    // UNIFIED QUEUE MODE: Always use queue for consistent behavior
                    foreach (var channelId in toSyncChannelIds)
                    {
                        await EnqueueSyncTask(note, fullContent, channelId, "CREATE");
                        logger.LogDebug("Queued CREATE task for note {NoteId} to Telegram channel {ChannelId}",
                            note.Id, channelId);
                    }
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
        try
        {
            var settings = await telegramSettingsCacheService.GetAsync(note.UserId);
            if (!settings.Any()) return;

            var channelData = await _GetRequiredChannelData(note);
            var toBeSent = channelData.toBeSent;

            // Queue DELETE operations for channels that should be removed
            foreach (var channel in channelData.toBeRemovedChannels)
            {
                await EnqueueSyncTask(note, string.Empty, channel.ChannelId, "DELETE", channel.MessageId);
                logger.LogDebug("Queued DELETE task for note {NoteId} from Telegram channel {ChannelId}",
                    note.Id, channel.ChannelId);
            }

            var tobeUpdated = channelData.toBeUpdatedChannels;

            // CRITICAL: Preserve the original smart decision logic - only use UPDATE for short messages
            var hasContentChange = _HasContentChanged(existingNote, note, fullContent);
            var isShortMessage = fullContent.Length <= Constants.TelegramMessageLength;
            if (hasContentChange)
            {
                // For short messages, try to UPDATE existing ones
                foreach (var channel in tobeUpdated)
                {
                    if (isShortMessage)
                    {
                        await EnqueueSyncTask(note, fullContent, channel.ChannelId, "UPDATE", channel.MessageId);
                        logger.LogDebug("Queued UPDATE task for note {NoteId} to Telegram channel {ChannelId} (content changed)",
                            note.Id, channel.ChannelId);
                    }
                    else
                    {
                        // For long messages, must DELETE + CREATE (can't edit long messages)
                        // First delete the existing message
                        await EnqueueSyncTask(note, string.Empty, channel.ChannelId, "DELETE", channel.MessageId);
                        logger.LogDebug("Queued DELETE task for long content note {NoteId} from Telegram channel {ChannelId}",
                            note.Id, channel.ChannelId);
                        // Then add this channel to the list that needs new messages created
                        toBeSent.Add(channel.ChannelId);
                    }
                }
            }

            // Queue CREATE operations for new channels (and channels that were converted from UPDATE to DELETE+CREATE)
            foreach (var channelId in toBeSent)
            {
                await EnqueueSyncTask(note, fullContent, channelId, "CREATE");
                logger.LogDebug("Queued CREATE task for note {NoteId} to Telegram channel {ChannelId}",
                    note.Id, channelId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in SyncEditNote for note {NoteId}", note.Id);
        }
    }

    public async Task SyncDeleteNote(Note note)
    {
        if (!string.IsNullOrWhiteSpace(note.TelegramMessageIds))
        {
            try
            {
                var settings = await telegramSettingsCacheService.GetAsync(note.UserId);
                if (!settings.Any()) return;

                var syncedChannels = _GetSyncedChannels(note);
                foreach (var channel in syncedChannels)
                {
                    var channelId = channel.ChannelId;
                    var setting = settings.FirstOrDefault(s => s.ChannelId.Equals(channelId));
                    if (setting == null) continue;

                    // Use queue instead of direct deletion - content not needed for DELETE
                    await EnqueueSyncTask(note, string.Empty, channelId, "DELETE", channel.MessageId);
                }

                // Note: TelegramMessageIds will be cleared by the handler after successful deletion
                // This ensures we can retry if the deletion fails
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error enqueueing Telegram delete tasks for note {NoteId}", note.Id);
            }
        }
    }

    public async Task SyncUndeleteNote(Note note)
    {
        // As per user instruction, no action is taken for undeleting notes on Telegram
        await Task.CompletedTask;
    }

    public Task PurgeDeletedNotes(long userId)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Obtain three groups of channel data:
    /// 1. toBeUpdatedChannels: Channels that need note updates.
    /// 2. toBeRemovedChannels: Channels where synced notes need to be removed.
    /// 3. toBeSent: Channel IDs that need new note synchronization.
    /// </summary>
    /// <param name="note"></param>
    /// <returns></returns>
    private async Task<(List<SyncedTelegramChannel> toBeUpdatedChannels,
        List<SyncedTelegramChannel> toBeRemovedChannels,
        List<string> toBeSent)> _GetRequiredChannelData(Note note)
    {
        var syncedChannels = _GetSyncedChannels(note);
        var toSyncChannelIds = _GetToSyncChannelIds(note, await telegramSettingsCacheService.GetAsync(note.UserId));

        var toBeUpdated = _GetChannelsToBeUpdated(syncedChannels, toSyncChannelIds);
        var toBeRemoved = _GetChannelsToBeRemoved(syncedChannels, toSyncChannelIds);
        var toBeSentIds = _GetChannelsToBeSent(syncedChannels, toSyncChannelIds);

        return (toBeUpdated, toBeRemoved, toBeSentIds);
    }

    /// <summary>
    /// Gets channels that require note updates.
    /// </summary>
    private List<SyncedTelegramChannel> _GetChannelsToBeUpdated(List<SyncedTelegramChannel> syncedChannels,
        List<string> toSyncChannelIds)
    {
        var idsToUpdate = syncedChannels.Select(s => s.ChannelId).Intersect(toSyncChannelIds).ToList();
        return syncedChannels.Where(r => idsToUpdate.Contains(r.ChannelId)).ToList();
    }

    /// <summary>
    /// Gets channels where synced notes need to be removed.
    /// </summary>
    private List<SyncedTelegramChannel> _GetChannelsToBeRemoved(List<SyncedTelegramChannel> syncedChannels,
        List<string> toSyncChannelIds)
    {
        if (!toSyncChannelIds.Any()) return syncedChannels;

        var idsToRemove = syncedChannels.Select(s => s.ChannelId).Except(toSyncChannelIds).ToList();
        return syncedChannels.Where(r => idsToRemove.Contains(r.ChannelId)).ToList();
    }

    /// <summary>
    /// Gets channel IDs that need new note synchronization.
    /// </summary>
    private List<string> _GetChannelsToBeSent(List<SyncedTelegramChannel> syncedChannels, List<string> toSyncChannelIds)
    {
        if (!syncedChannels.Any()) return toSyncChannelIds;
        return toSyncChannelIds.Except(syncedChannels.Select(s => s.ChannelId)).ToList();
    }

    private List<string> _GetToSyncChannelIds(Note note, IList<TelegramSettings> settings)
    {
        var targetChannelList = new List<string>();
        foreach (var setting in settings)
        {
            switch (setting.SyncType)
            {
                case TelegramSyncType.All:
                    targetChannelList.Add(setting.ChannelId);
                    break;
                case TelegramSyncType.Private:
                    if (note.IsPrivate)
                    {
                        targetChannelList.Add(setting.ChannelId);
                    }

                    break;
                case TelegramSyncType.Public:
                    if (!note.IsPrivate)
                    {
                        targetChannelList.Add(setting.ChannelId);
                    }

                    break;
                case TelegramSyncType.Tag:
                    if (note.TagList.Contains(setting.SyncValue))
                    {
                        targetChannelList.Add(setting.ChannelId);
                    }

                    break;
            }
        }

        return targetChannelList.Distinct().ToList();
    }

    private static List<SyncedTelegramChannel> _GetSyncedChannels(Note note)
    {
        if (string.IsNullOrWhiteSpace(note.TelegramMessageIds)) return [];
        return note.TelegramMessageIds.Split(",").Select(s =>
        {
            var sync = s.Split(":");
            return new SyncedTelegramChannel
            {
                ChannelId = sync[0],
                MessageId = int.TryParse(sync[1], out var messageId) ? messageId : 0,
            };
        }).ToList();
    }

    private async Task EnqueueSyncTask(Note note, string fullContent, string channelId, string action, int? messageId = null)
    {
        var payload = new TelegramSyncPayload
        {
            Action = action,
            FullContent = fullContent,
            // Security: BotToken removed - will be retrieved in handler using UserId + ChannelId
            ChannelId = channelId,
            MessageId = messageId,
            IsMarkdown = note.IsMarkdown
        };

        var task = SyncTask.Create("telegram", action, note.Id, note.UserId, payload);

        await syncQueueService.EnqueueAsync("telegram", task);

        logger.LogInformation("Enqueued Telegram sync task {TaskId} for note {NoteId}, action: {Action}",
            task.Id, note.Id, action);
    }

    /// <summary>
    /// Checks if the note content has changed in ways that matter for Telegram synchronization.
    /// Only considers changes that would affect the actual message content in Telegram.
    /// </summary>
    /// <param name="existingNote">The original note from database</param>
    /// <param name="newNote">The updated note</param>
    /// <param name="newFullContent">The new full content to be synced</param>
    /// <returns>True if content changed in ways that require Telegram update</returns>
    private bool _HasContentChanged(Note existingNote, Note newNote, string newFullContent)
    {
        // 1. Check if the actual content changed
        if (existingNote.Content != newFullContent)
        {
            return true;
        }

        // 2. Check if Markdown formatting changed (affects how Telegram renders the message)
        if (existingNote.IsMarkdown != newNote.IsMarkdown)
        {
            return true;
        }

        // Note: IsPrivate, Tags, and other metadata changes don't affect the message content
        // that's already posted to Telegram, so we don't check those here.
        // Those changes only affect which channels the note should be synced to (handled by channel logic).

        return false;
    }
}
