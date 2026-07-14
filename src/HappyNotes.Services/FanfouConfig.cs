namespace HappyNotes.Services
{
    /// <summary>
    /// Fanfou is a single service (api.fanfou.com), so the app-level OAuth 1.0a
    /// consumer credentials live in configuration (injected at deploy time,
    /// never committed) rather than in a per-instance database table.
    /// </summary>
    public class FanfouConfig
    {
        public string ConsumerKey { get; set; } = string.Empty;
        public string ConsumerSecret { get; set; } = string.Empty;

        /// <summary>
        /// API base url for signed status calls, e.g. https://api.fanfou.com
        /// </summary>
        public string ApiBaseUrl { get; set; } = "https://api.fanfou.com";

        /// <summary>
        /// OAuth handshake base url (request_token / authorize / access_token),
        /// e.g. https://fanfou.com
        /// </summary>
        public string OAuthBaseUrl { get; set; } = "https://fanfou.com";

        /// <summary>
        /// OAuth callback url registered with the Fanfou app; where Fanfou
        /// redirects after the user authorizes.
        /// </summary>
        public string CallbackUrl { get; set; } = string.Empty;
    }
}
