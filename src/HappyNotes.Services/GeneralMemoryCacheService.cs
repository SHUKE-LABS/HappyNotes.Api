using HappyNotes.Services.interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace HappyNotes.Services;

public class GeneralMemoryCacheService(
    IMemoryCache cache
)
    : IGeneralMemoryCacheService
{
    private static string CacheKey(string cacheKey) => $"GENERAL_{cacheKey}";
    private int defaultCacheExpirationInMins = 30;

    public T? Get<T>(string cacheKey)
    {
        if (cache.TryGetValue(CacheKey(cacheKey), out T? value))
        {
            return value;
        }

        return default;
    }

    public void Set<T>(string cacheKey, T value, TimeSpan? expiration = null)
    {
        expiration ??= TimeSpan.FromMinutes(defaultCacheExpirationInMins);
        var cacheOptions = new MemoryCacheEntryOptions().SetSlidingExpiration((TimeSpan)expiration);
        cache.Set(CacheKey(cacheKey), value, cacheOptions);
    }

    public void ClearCache(string cacheKey)
    {
        cache.Remove(CacheKey(cacheKey));
    }
}
