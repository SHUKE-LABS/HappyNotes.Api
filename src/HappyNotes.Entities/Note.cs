using Api.Framework;
using HappyNotes.Models;
using SqlSugar;

namespace HappyNotes.Entities;

public class Note : EntityBase
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Content { get; set; } = string.Empty;
    public bool IsLong { get; set; }
    public bool IsMarkdown { get; set; }
    public bool IsPrivate { get; set; }
    public string Tags { get; set; } = string.Empty;
    public string? TelegramMessageIds { get; set; }
    public string? MastodonTootIds { get; set; }
    public string? FanfouStatusIds { get; set; }

    [SugarColumn(IsIgnore = true)] public User User { get; set; } = default!;
    [SugarColumn(IsIgnore = true)] public List<string> TagList { get; set; } = [];

    public void UpdateMastodonInstanceIds(List<MastodonSyncedInstance> instances)
    {
        MastodonTootIds = string.Join(",", instances.Select(r => $"{r.UserAccountId}:{r.TootId}"));
    }
    public void UpdateFanfouInstanceIds(List<FanfouSyncedInstance> instances)
    {
        FanfouStatusIds = string.Join(",", instances.Select(r => $"{r.UserAccountId}:{r.StatusId}"));
    }
    public void UpdateTelegramMessageIds(List<SyncedTelegramChannel> channels)
    {
        TelegramMessageIds = string.Join(",", channels.Select(r => $"{r.ChannelId}:{r.MessageId}"));
    }

    public void AddMastodonTootId(long userAccountId, string tootId)
    {
        var tootIdEntry = $"{userAccountId}:{tootId}";

        if (string.IsNullOrWhiteSpace(MastodonTootIds))
        {
            MastodonTootIds = tootIdEntry;
        }
        else
        {
            var tootIds = MastodonTootIds.Split(',').ToList();
            var existingIndex = tootIds.FindIndex(id => id.StartsWith($"{userAccountId}:"));

            if (existingIndex >= 0)
            {
                tootIds[existingIndex] = tootIdEntry;
            }
            else
            {
                tootIds.Add(tootIdEntry);
            }

            MastodonTootIds = string.Join(",", tootIds);
        }
    }

    public void RemoveMastodonTootId(long userAccountId, string tootId)
    {
        if (string.IsNullOrWhiteSpace(MastodonTootIds))
        {
            return;
        }

        var tootIds = MastodonTootIds.Split(',').ToList();
        var targetId = $"{userAccountId}:{tootId}";

        tootIds.RemoveAll(id => id.Equals(targetId, StringComparison.OrdinalIgnoreCase));

        MastodonTootIds = tootIds.Any() ? string.Join(",", tootIds) : null;
    }

    public void AddFanfouStatusId(long userAccountId, string statusId)
    {
        var statusIdEntry = $"{userAccountId}:{statusId}";

        if (string.IsNullOrWhiteSpace(FanfouStatusIds))
        {
            FanfouStatusIds = statusIdEntry;
        }
        else
        {
            var statusIds = FanfouStatusIds.Split(',').ToList();
            var existingIndex = statusIds.FindIndex(id => id.StartsWith($"{userAccountId}:"));

            if (existingIndex >= 0)
            {
                statusIds[existingIndex] = statusIdEntry;
            }
            else
            {
                statusIds.Add(statusIdEntry);
            }

            FanfouStatusIds = string.Join(",", statusIds);
        }
    }

    public void RemoveFanfouStatusId(long userAccountId, string statusId)
    {
        if (string.IsNullOrWhiteSpace(FanfouStatusIds))
        {
            return;
        }

        var statusIds = FanfouStatusIds.Split(',').ToList();
        var targetId = $"{userAccountId}:{statusId}";

        statusIds.RemoveAll(id => id.Equals(targetId, StringComparison.OrdinalIgnoreCase));

        FanfouStatusIds = statusIds.Any() ? string.Join(",", statusIds) : null;
    }
}
