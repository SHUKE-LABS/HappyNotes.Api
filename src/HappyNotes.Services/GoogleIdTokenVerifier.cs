using Google.Apis.Auth;
using HappyNotes.Common;
using HappyNotes.Common.Enums;
using HappyNotes.Services.interfaces;
using Microsoft.Extensions.Options;

namespace HappyNotes.Services;

public class GoogleIdTokenVerifier(IOptions<GoogleOAuthConfig> googleOAuthConfig) : IGoogleIdTokenVerifier
{
    private readonly GoogleOAuthConfig _googleOAuthConfig = googleOAuthConfig.Value;

    public async Task<GoogleJsonWebSignature.Payload> VerifyAsync(string idToken)
    {
        try
        {
            return await GoogleJsonWebSignature.ValidateAsync(idToken, new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = [_googleOAuthConfig.ClientId],
            });
        }
        catch (InvalidJwtException)
        {
            throw ExceptionHelper.New(EventId._00109_InvalidGoogleIdToken);
        }
    }
}
