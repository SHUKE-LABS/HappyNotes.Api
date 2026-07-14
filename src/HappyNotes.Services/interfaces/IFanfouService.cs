using HappyNotes.Models;

namespace HappyNotes.Services.interfaces;

public interface IFanfouService
{
    /// <summary>
    /// First OAuth leg: obtain temporary request credentials.
    /// </summary>
    Task<FanfouRequestToken> GetRequestTokenAsync();

    /// <summary>
    /// Build the URL the user is sent to in order to authorize the app.
    /// </summary>
    string GetAuthorizeUrl(string requestToken);

    /// <summary>
    /// Final OAuth leg: exchange the authorized request token + verifier for access credentials.
    /// </summary>
    Task<FanfouAccessToken> GetAccessTokenAsync(string requestToken, string requestTokenSecret, string verifier);

    /// <summary>
    /// Post a status to Fanfou. Returns the created status id.
    /// </summary>
    Task<string> SendStatusAsync(string accessToken, string accessTokenSecret, string content, long noteId = 0);

    /// <summary>
    /// Delete a status from Fanfou.
    /// </summary>
    Task DeleteStatusAsync(string accessToken, string accessTokenSecret, string statusId);
}
