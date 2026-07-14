namespace HappyNotes.Models;

/// <summary>
/// OAuth 1.0a temporary (request) credentials returned from the first leg.
/// </summary>
public class FanfouRequestToken
{
    public string Token { get; set; } = string.Empty;
    public string TokenSecret { get; set; } = string.Empty;
}

/// <summary>
/// OAuth 1.0a access credentials returned from the final leg.
/// </summary>
public class FanfouAccessToken
{
    public string Token { get; set; } = string.Empty;
    public string TokenSecret { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string ScreenName { get; set; } = string.Empty;
}
