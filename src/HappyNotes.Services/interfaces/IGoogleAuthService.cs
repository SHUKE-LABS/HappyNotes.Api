using HappyNotes.Entities;

namespace HappyNotes.Services.interfaces;

public interface IGoogleAuthService
{
    Task<User> ResolveOrCreateUserAsync(string idToken);
}
