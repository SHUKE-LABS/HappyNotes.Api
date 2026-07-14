namespace HappyNotes.Models;

/// <summary>
/// Server-side state bound to an OAuth 1.0a request token between the authorize
/// and callback legs: which user started the flow and the request token secret
/// needed to sign the access-token exchange. Cached single-use with a short TTL.
/// </summary>
public class FanfouOAuthState
{
    public long UserId { get; set; }
    public string RequestTokenSecret { get; set; } = string.Empty;
}
