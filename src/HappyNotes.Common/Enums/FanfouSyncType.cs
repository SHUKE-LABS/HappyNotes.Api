namespace HappyNotes.Common.Enums;

public enum FanfouSyncType
{
    /// <summary>
    /// Sync all notes
    /// </summary>
    All = 1,

    /// <summary>
    /// Public notes only
    /// </summary>
    PublicOnly = 2,

    /// <summary>
    /// Sync notes that have a fanfou tag
    /// </summary>
    TagFanfouOnly = 3,
}

public static class FanfouSyncTypeExtensions
{
    public static FanfouSyncType Next(this FanfouSyncType syncType)
    {
        return syncType switch
        {
            FanfouSyncType.All => FanfouSyncType.PublicOnly,
            FanfouSyncType.PublicOnly => FanfouSyncType.TagFanfouOnly,
            FanfouSyncType.TagFanfouOnly => FanfouSyncType.All,
            _ => FanfouSyncType.All,
        };
    }
}
