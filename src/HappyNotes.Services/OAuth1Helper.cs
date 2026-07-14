using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HappyNotes.Services;

/// <summary>
/// Minimal OAuth 1.0a (HMAC-SHA1) request signer per RFC 5849. Fanfou has no
/// maintained .NET SDK, so this builds the signed <c>Authorization</c> header
/// used for the three-legged OAuth handshake and for signed API calls.
/// </summary>
public static class OAuth1Helper
{
    private const string SignatureMethod = "HMAC-SHA1";
    private const string OAuthVersion = "1.0";

    /// <summary>
    /// RFC 3986 percent-encoding: unreserved characters pass through, everything
    /// else is upper-hex escaped.
    /// </summary>
    public static string PercentEncode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var bytes = Encoding.UTF8.GetBytes(value);
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '-' or '.' or '_' or '~')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%');
                sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the signature base string: METHOD&amp;encode(baseUrl)&amp;encode(sorted params).
    /// </summary>
    internal static string BuildSignatureBaseString(string httpMethod, string baseUrl,
        IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var normalized = string.Join("&", parameters
            .Select(p => new KeyValuePair<string, string>(PercentEncode(p.Key), PercentEncode(p.Value)))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));

        return $"{httpMethod.ToUpperInvariant()}&{PercentEncode(baseUrl)}&{PercentEncode(normalized)}";
    }

    internal static string ComputeSignature(string baseString, string consumerSecret, string? tokenSecret)
    {
        var key = $"{PercentEncode(consumerSecret)}&{PercentEncode(tokenSecret ?? string.Empty)}";
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(baseString));
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Builds the full <c>Authorization: OAuth ...</c> header value for a request.
    /// The nonce and timestamp are passed in so the result is deterministic and testable;
    /// callers use <see cref="Sign"/> for the runtime path.
    /// </summary>
    internal static string BuildAuthorizationHeader(
        string httpMethod,
        string url,
        string consumerKey,
        string consumerSecret,
        string? token,
        string? tokenSecret,
        string nonce,
        long timestamp,
        IEnumerable<KeyValuePair<string, string>>? requestParams = null,
        string? callback = null,
        string? verifier = null)
    {
        // Any query string on the url participates in the signature but not the header.
        var baseUrl = url;
        var collected = new List<KeyValuePair<string, string>>();
        var qIndex = url.IndexOf('?');
        if (qIndex >= 0)
        {
            baseUrl = url[..qIndex];
            foreach (var pair in url[(qIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                collected.Add(new KeyValuePair<string, string>(
                    Uri.UnescapeDataString(kv[0]),
                    kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty));
            }
        }

        if (requestParams != null) collected.AddRange(requestParams);

        var oauthParams = new Dictionary<string, string>
        {
            ["oauth_consumer_key"] = consumerKey,
            ["oauth_nonce"] = nonce,
            ["oauth_signature_method"] = SignatureMethod,
            ["oauth_timestamp"] = timestamp.ToString(CultureInfo.InvariantCulture),
            ["oauth_version"] = OAuthVersion,
        };
        if (!string.IsNullOrEmpty(token)) oauthParams["oauth_token"] = token;
        if (!string.IsNullOrEmpty(callback)) oauthParams["oauth_callback"] = callback;
        if (!string.IsNullOrEmpty(verifier)) oauthParams["oauth_verifier"] = verifier;

        var signatureParams = new List<KeyValuePair<string, string>>(collected);
        signatureParams.AddRange(oauthParams);

        var baseString = BuildSignatureBaseString(httpMethod, baseUrl, signatureParams);
        oauthParams["oauth_signature"] = ComputeSignature(baseString, consumerSecret, tokenSecret);

        // Only oauth_* params belong in the Authorization header.
        var header = string.Join(", ", oauthParams
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{PercentEncode(p.Key)}=\"{PercentEncode(p.Value)}\""));

        return "OAuth " + header;
    }

    /// <summary>
    /// Runtime convenience: signs a request with a fresh nonce + current timestamp.
    /// </summary>
    public static string Sign(
        string httpMethod,
        string url,
        string consumerKey,
        string consumerSecret,
        string? token,
        string? tokenSecret,
        IEnumerable<KeyValuePair<string, string>>? requestParams = null,
        string? callback = null,
        string? verifier = null)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return BuildAuthorizationHeader(httpMethod, url, consumerKey, consumerSecret, token, tokenSecret,
            nonce, timestamp, requestParams, callback, verifier);
    }
}
