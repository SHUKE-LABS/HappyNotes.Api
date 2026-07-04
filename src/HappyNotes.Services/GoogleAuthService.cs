using Api.Framework;
using Api.Framework.Extensions;
using Api.Framework.Helper;
using HappyNotes.Entities;
using HappyNotes.Services.interfaces;

namespace HappyNotes.Services;

public class GoogleAuthService(
    IGoogleIdTokenVerifier googleIdTokenVerifier,
    IRepositoryBase<User> userRepository,
    IRepositoryBase<UserExternalLogin> userExternalLoginRepository) : IGoogleAuthService
{
    private const string Provider = "google";

    public async Task<User> ResolveOrCreateUserAsync(string idToken)
    {
        var payload = await googleIdTokenVerifier.VerifyAsync(idToken);

        var externalLogin = await userExternalLoginRepository.GetFirstOrDefaultAsync(
            w => w.Provider == Provider && w.ProviderSubject == payload.Subject);
        if (externalLogin != null)
        {
            return await userRepository.GetFirstOrDefaultAsync(w => w.Id == externalLogin.UserId)
                   ?? throw new InvalidOperationException($"UserExternalLogin {externalLogin.Id} references missing User {externalLogin.UserId}");
        }

        var email = payload.Email.Trim().ToLower();
        var user = payload.EmailVerified
            ? await userRepository.GetFirstOrDefaultAsync(w => w.Email == email)
            : null;
        user ??= await CreateUserAsync(email, payload.EmailVerified);

        await userExternalLoginRepository.InsertAsync(new UserExternalLogin
        {
            UserId = user.Id,
            Provider = Provider,
            ProviderSubject = payload.Subject,
            CreatedAt = DateTime.Now.ToUnixTimeSeconds(),
        });

        return user;
    }

    private async Task<User> CreateUserAsync(string email, bool emailVerified)
    {
        var localPart = SanitizeUsername(email.Split('@')[0]);
        string username;
        do
        {
            username = $"{localPart}{SaltGenerator.GenerateSaltString(8)}";
        } while (await userRepository.GetFirstOrDefaultAsync(w => w.Username == username) != null);

        var newUser = new User
        {
            Username = username,
            Email = email,
            EmailVerified = emailVerified ? 1 : 0,
            Gravatar = GravatarHelper.GetGravatarUrl(email),
            Password = string.Empty,
            Salt = string.Empty,
            CreatedAt = DateTime.Now.ToUnixTimeSeconds(),
        };

        newUser.Id = await userRepository.InsertReturnIdentityAsync(newUser);
        return newUser;
    }

    private static string SanitizeUsername(string localPart)
    {
        var sanitized = new string(localPart.Where(char.IsLetterOrDigit).ToArray()).ToLower();
        return string.IsNullOrEmpty(sanitized) ? "user" : sanitized;
    }
}
