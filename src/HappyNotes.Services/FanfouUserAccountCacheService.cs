using Api.Framework;
using HappyNotes.Common.Enums;
using HappyNotes.Entities;
using HappyNotes.Services.interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace HappyNotes.Services;

public class FanfouUserAccountCacheService(
    IMemoryCache cache,
    IRepositoryBase<FanfouUserAccount> fanfouUserAccountsRepository)
    : IFanfouUserAccountCacheService
{
    private static string CacheKey(long userId) => $"FUA_{userId}";

    // Set cache options
    private static readonly MemoryCacheEntryOptions CacheEntryOptions = new MemoryCacheEntryOptions()
        .SetSlidingExpiration(TimeSpan.FromMinutes(1440)); // Set expiration time

    public async Task<IList<FanfouUserAccount>> GetAsync(long userId)
    {
        if (cache.TryGetValue(CacheKey(userId), out List<FanfouUserAccount>? config))
        {
            return config!;
        }

        // If not in cache, load from the database
        var settings = await fanfouUserAccountsRepository.GetListAsync(
            s => s.UserId == userId && s.Status == FanfouUserAccountStatus.Normal);

        Set(userId, settings);
        return settings;
    }

    public void Set(long userId, IList<FanfouUserAccount> account)
    {
        cache.Set(CacheKey(userId), account, CacheEntryOptions);
    }

    public void ClearCache(long userId)
    {
        cache.Remove(CacheKey(userId));
    }
}
