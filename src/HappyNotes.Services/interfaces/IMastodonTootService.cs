
using Mastonet.Entities;

namespace HappyNotes.Services.interfaces;

public interface IMastodonTootService
{
    Task<Status> SendTootAsync(string instanceUrl, string accessToken, string text, bool isPrivate, bool isMarkdown = false, long noteId = 0, long userId = 0);
    Task<Status> EditTootAsync(string instanceUrl, string accessToken, string tootId, string newText, bool isPrivate, bool isMarkdown = false, long noteId = 0, long userId = 0);
    Task DeleteTootAsync(string instanceUrl, string accessToken, string tootId);
}
