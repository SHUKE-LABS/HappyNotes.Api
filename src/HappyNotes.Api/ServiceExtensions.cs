using HappyNotes.Repositories;
using HappyNotes.Repositories.interfaces;
using HappyNotes.Services;
using HappyNotes.Services.interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace HappyNotes.Api;

public static class ServiceExtensions
{
    public static void RegisterServices(this IServiceCollection services)
    {
        services.AddHttpClient();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IGoogleIdTokenVerifier, GoogleIdTokenVerifier>();
        services.AddScoped<IGoogleAuthService, GoogleAuthService>();
        services.AddScoped<INoteService, NoteService>();
        services.AddScoped<INoteTagService, NoteTagService>();
        services.AddScoped<INoteRepository, NoteRepository>();
        services.AddSingleton<ITelegramService, TelegramService>();
        services.AddSingleton<IMastodonTootService, MastodonTootService>();
        services.AddSingleton<IMemoryCache, MemoryCache>();
        services.AddScoped<ITelegramSettingsCacheService, TelegramSettingsCacheService>();
        services.AddScoped<IMastodonUserAccountCacheService, MastodonUserAccountCacheService>();
        services.AddSingleton<IGeneralMemoryCacheService, GeneralMemoryCacheService>();
        services.AddScoped<ISyncNoteService, MastodonSyncNoteService>();
        services.AddScoped<ISyncNoteService, TelegramSyncNoteService>();
        services.AddScoped<ISyncNoteService, ManticoreSyncNoteService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<IDatabaseClient, DatabaseClient>();
    }
}
