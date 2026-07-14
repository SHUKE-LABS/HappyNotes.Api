using System.Text.Json;
using HappyNotes.Common;
using HappyNotes.Models;
using HappyNotes.Services.interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HappyNotes.Services;

public class FanfouService(
    IHttpClientFactory httpClientFactory,
    IOptions<FanfouConfig> fanfouConfig,
    ILogger<FanfouService> logger
) : IFanfouService
{
    private readonly FanfouConfig _config = fanfouConfig.Value;

    private string RequestTokenUrl => $"{_config.OAuthBaseUrl}/oauth/request_token";
    private string AuthorizeUrl => $"{_config.OAuthBaseUrl}/oauth/authorize";
    private string AccessTokenUrl => $"{_config.OAuthBaseUrl}/oauth/access_token";
    private string UpdateStatusUrl => $"{_config.ApiBaseUrl}/statuses/update.json";
    private string DestroyStatusUrl => $"{_config.ApiBaseUrl}/statuses/destroy.json";

    public async Task<FanfouRequestToken> GetRequestTokenAsync()
    {
        var callback = string.IsNullOrEmpty(_config.CallbackUrl) ? "oob" : _config.CallbackUrl;
        var authHeader = OAuth1Helper.Sign(
            "GET", RequestTokenUrl, _config.ConsumerKey, _config.ConsumerSecret,
            token: null, tokenSecret: null, callback: callback);

        using var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, RequestTokenUrl);
        request.Headers.TryAddWithoutValidation("Authorization", authHeader);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Fanfou request_token failed. Status: {StatusCode}, Body: {Body}", response.StatusCode, body);
            throw new Exception($"Fanfou request_token failed: {response.StatusCode}");
        }

        var parsed = ParseFormEncoded(body);
        return new FanfouRequestToken
        {
            Token = parsed.GetValueOrDefault("oauth_token", string.Empty),
            TokenSecret = parsed.GetValueOrDefault("oauth_token_secret", string.Empty),
        };
    }

    public string GetAuthorizeUrl(string requestToken)
    {
        var url = $"{AuthorizeUrl}?oauth_token={Uri.EscapeDataString(requestToken)}";
        if (!string.IsNullOrEmpty(_config.CallbackUrl))
        {
            url += $"&oauth_callback={Uri.EscapeDataString(_config.CallbackUrl)}";
        }

        return url;
    }

    public async Task<FanfouAccessToken> GetAccessTokenAsync(string requestToken, string requestTokenSecret, string verifier)
    {
        var authHeader = OAuth1Helper.Sign(
            "GET", AccessTokenUrl, _config.ConsumerKey, _config.ConsumerSecret,
            token: requestToken, tokenSecret: requestTokenSecret, verifier: verifier);

        using var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, AccessTokenUrl);
        request.Headers.TryAddWithoutValidation("Authorization", authHeader);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Fanfou access_token failed. Status: {StatusCode}, Body: {Body}", response.StatusCode, body);
            throw new Exception($"Fanfou access_token failed: {response.StatusCode}");
        }

        var parsed = ParseFormEncoded(body);
        return new FanfouAccessToken
        {
            Token = parsed.GetValueOrDefault("oauth_token", string.Empty),
            TokenSecret = parsed.GetValueOrDefault("oauth_token_secret", string.Empty),
            UserId = parsed.GetValueOrDefault("user_id", string.Empty),
            ScreenName = parsed.GetValueOrDefault("screen_name", string.Empty),
        };
    }

    public async Task<string> SendStatusAsync(string accessToken, string accessTokenSecret, string content, long noteId = 0)
    {
        var status = PrepareStatusText(content, noteId);
        var bodyParams = new Dictionary<string, string> { ["status"] = status };

        var json = await PostSignedAsync(UpdateStatusUrl, accessToken, accessTokenSecret, bodyParams);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("id", out var idElement))
        {
            return idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString() ?? string.Empty
                : idElement.GetRawText();
        }

        throw new Exception("Fanfou statuses/update response missing status id");
    }

    public async Task DeleteStatusAsync(string accessToken, string accessTokenSecret, string statusId)
    {
        var bodyParams = new Dictionary<string, string> { ["id"] = statusId };
        await PostSignedAsync(DestroyStatusUrl, accessToken, accessTokenSecret, bodyParams);
    }

    private async Task<string> PostSignedAsync(string url, string accessToken, string accessTokenSecret,
        Dictionary<string, string> bodyParams)
    {
        // Form body params participate in the OAuth signature.
        var authHeader = OAuth1Helper.Sign(
            "POST", url, _config.ConsumerKey, _config.ConsumerSecret,
            token: accessToken, tokenSecret: accessTokenSecret, requestParams: bodyParams);

        using var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(bodyParams)
        };
        request.Headers.TryAddWithoutValidation("Authorization", authHeader);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Fanfou POST {Url} failed. Status: {StatusCode}, Body: {Body}", url, response.StatusCode, body);
            throw new Exception($"Fanfou request to {url} failed: {response.StatusCode}");
        }

        return body;
    }

    /// <summary>
    /// Fanfou statuses have a hard 140-character limit. Over-length notes are
    /// truncated with a trailing permalink back to the note; the link length is
    /// reserved inside the 140-char budget so the whole status never overflows.
    /// </summary>
    internal static string PrepareStatusText(string content, long noteId)
    {
        content = content.Trim();
        if (content.Length <= Constants.FanfouStatusLength)
        {
            return content;
        }

        if (noteId <= 0)
        {
            return content[..Constants.FanfouStatusLength];
        }

        var link = $"{Constants.HappyNotesWebsite}/note/{noteId}";
        var suffix = $"… {link}";

        // If the link alone leaves no room for content, fall back to a hard truncate.
        if (suffix.Length >= Constants.FanfouStatusLength)
        {
            return content[..Constants.FanfouStatusLength];
        }

        var headLength = Constants.FanfouStatusLength - suffix.Length;
        return content[..headLength].TrimEnd() + suffix;
    }

    private static Dictionary<string, string> ParseFormEncoded(string body)
    {
        return body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(
                kv => Uri.UnescapeDataString(kv[0]),
                kv => kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty);
    }
}
