using Google.Apis.Auth;

namespace HappyNotes.Services.interfaces;

public interface IGoogleIdTokenVerifier
{
    Task<GoogleJsonWebSignature.Payload> VerifyAsync(string idToken);
}
