using Api.Framework;
using Api.Framework.Helper;
using Api.Framework.Models;
using Api.Framework.Result;
using HappyNotes.Common.Enums;
using HappyNotes.Entities;
using HappyNotes.Models;
using HappyNotes.Services.interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HappyNotes.Api.Controllers;

[Authorize]
public class FanfouAuthController(
    IFanfouService fanfouService,
    IRepositoryBase<FanfouUserAccount> fanfouUserAccountRepository,
    IFanfouUserAccountCacheService fanfouUserAccountCacheService,
    ICurrentUser currentUser,
    ILogger<FanfouAuthController> logger,
    IOptions<JwtConfig> jwtConfig,
    IGeneralMemoryCacheService generalMemoryCacheService) : BaseController
{
    private readonly JwtConfig _jwtConfig = jwtConfig.Value;

    // The request-token → user binding is single-use and short-lived so a leaked
    // oauth_token cannot be replayed to rebind a different account.
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);
    private static string StateCacheKey(string requestToken) => $"fanfou_oauth_{requestToken}";

    /// <summary>
    /// First leg: fetch a request token and return the URL the user should be sent to
    /// in order to authorize the app.
    /// </summary>
    [HttpPost]
    public async Task<ApiResult<string>> RequestToken()
    {
        var requestToken = await fanfouService.GetRequestTokenAsync();
        if (string.IsNullOrEmpty(requestToken.Token))
        {
            return new FailedResult<string>(string.Empty, "Failed to obtain Fanfou request token");
        }

        generalMemoryCacheService.Set(StateCacheKey(requestToken.Token),
            new FanfouOAuthState { UserId = currentUser.Id, RequestTokenSecret = requestToken.TokenSecret },
            StateTtl);

        var authorizeUrl = fanfouService.GetAuthorizeUrl(requestToken.Token);
        logger.LogInformation("Issued Fanfou request token for user {UserId}", currentUser.Id);
        return new SuccessfulResult<string>(authorizeUrl);
    }

    /// <summary>
    /// Final leg: Fanfou redirects here after the user authorizes. Exchange the
    /// verified request token for access credentials and persist the account.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<ApiResult> Callback(string oauth_token, string oauth_verifier)
    {
        try
        {
            if (string.IsNullOrEmpty(oauth_token))
            {
                return Fail("Missing oauth_token");
            }

            var state = generalMemoryCacheService.Get<FanfouOAuthState>(StateCacheKey(oauth_token));
            if (state == null || state.UserId == 0)
            {
                logger.LogError("Invalid or expired Fanfou oauth_token in callback");
                return Fail("Invalid or expired authorization state");
            }

            // Single-use: evict immediately so the token cannot be replayed.
            generalMemoryCacheService.ClearCache(StateCacheKey(oauth_token));

            var accessToken = await fanfouService.GetAccessTokenAsync(oauth_token, state.RequestTokenSecret, oauth_verifier);
            if (string.IsNullOrEmpty(accessToken.Token) || string.IsNullOrEmpty(accessToken.TokenSecret))
            {
                logger.LogError("Fanfou access token exchange returned empty credentials for user {UserId}", state.UserId);
                return Fail("Failed to obtain Fanfou access token");
            }

            // A user has a single Fanfou account; replace any previous linkage.
            await fanfouUserAccountRepository.DeleteAsync(a => a.UserId == state.UserId);

            var account = new FanfouUserAccount
            {
                UserId = state.UserId,
                FanfouUserId = accessToken.UserId,
                AccessToken = TextEncryptionHelper.Encrypt(accessToken.Token, _jwtConfig.SymmetricSecurityKey),
                AccessTokenSecret = TextEncryptionHelper.Encrypt(accessToken.TokenSecret, _jwtConfig.SymmetricSecurityKey),
                Status = FanfouUserAccountStatus.Normal,
                SyncType = FanfouSyncType.All,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            await fanfouUserAccountRepository.InsertAsync(account);
            fanfouUserAccountCacheService.ClearCache(state.UserId);

            logger.LogInformation("Successfully linked Fanfou account for user {UserId}", state.UserId);
            return Success("Successfully authenticated with Fanfou. You can close this page.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fanfou OAuth callback failed: {ErrorMessage}", ex.Message);
            return Fail($"Authentication failed: {ex.Message}");
        }
    }
}
