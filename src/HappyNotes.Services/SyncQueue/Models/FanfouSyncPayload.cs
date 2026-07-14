namespace HappyNotes.Services.SyncQueue.Models;

public class FanfouSyncPayload
{
    public long UserAccountId { get; set; }
    public string FullContent { get; set; } = string.Empty;
    public string? StatusId { get; set; } // For DELETE operations
    public bool IsPrivate { get; set; }
    public bool IsMarkdown { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}
