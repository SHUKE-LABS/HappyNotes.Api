using HappyNotes.Entities;

namespace HappyNotes.Services.interfaces;

public interface IFanfouUserAccountCacheService
{
    Task<IList<FanfouUserAccount>> GetAsync(long userId);
    void Set(long userId, IList<FanfouUserAccount> account);
    void ClearCache(long userId);
}
