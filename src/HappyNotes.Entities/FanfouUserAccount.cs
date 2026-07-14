using Api.Framework.Helper;
using HappyNotes.Common.Enums;
using SqlSugar;

namespace HappyNotes.Entities;

public class FanfouUserAccount
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    public long UserId { get; set; }

    /// <summary>
    /// The Fanfou account id (the authorizing user's Fanfou id/screen name).
    /// </summary>
    public string FanfouUserId { get; set; } = string.Empty;

    public string AccessToken { get; init; } = string.Empty;
    public string AccessTokenSecret { get; init; } = string.Empty;

    public FanfouUserAccountStatus Status { get; set; }
    public FanfouSyncType SyncType { get; set; } = FanfouSyncType.All;

    /// <summary>
    /// Gets the status text representation of the current status.
    /// </summary>
    [SugarColumn(IsIgnore = true)]
    public string StatusText => Status.ToString();

    public long CreatedAt { get; set; }

    private string? _decryptedAccessToken;
    public string DecryptedAccessToken(string key)
    {
        return _decryptedAccessToken ??= TextEncryptionHelper.Decrypt(AccessToken, key);
    }

    private string? _decryptedAccessTokenSecret;
    public string DecryptedAccessTokenSecret(string key)
    {
        return _decryptedAccessTokenSecret ??= TextEncryptionHelper.Decrypt(AccessTokenSecret, key);
    }
}
