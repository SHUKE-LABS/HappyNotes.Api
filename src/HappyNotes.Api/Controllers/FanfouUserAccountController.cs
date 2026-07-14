using Api.Framework;
using Api.Framework.Extensions;
using Api.Framework.Result;
using HappyNotes.Common.Enums;
using HappyNotes.Entities;
using HappyNotes.Services.interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HappyNotes.Api.Controllers;

[Authorize]
public class FanfouUserAccountController(
    ICurrentUser currentUser,
    IRepositoryBase<FanfouUserAccount> fanfouUserAccountsRepository,
    IFanfouUserAccountCacheService fanfouUserAccountsCacheService
) : BaseController
{
    // A user has a single Fanfou account; GetAll returns a list for parity with
    // the Mastodon settings shape the frontend consumes.
    [HttpGet]
    public async Task<ApiResult<List<FanfouUserAccount>>> GetAll()
    {
        var userId = currentUser.Id;
        var settings = await fanfouUserAccountsRepository.GetListAsync(s => s.UserId.Equals(userId), o => o.CreatedAt);
        return new SuccessfulResult<List<FanfouUserAccount>>(settings.ToList());
    }

    [HttpPost]
    public async Task<ApiResult> Disable()
    {
        var userId = currentUser.Id;
        var existingSetting = await fanfouUserAccountsRepository.GetFirstOrDefaultAsync(s => s.UserId == userId);
        if (existingSetting == null)
        {
            throw new Exception("No Fanfou account exists for the current user.");
        }

        if (existingSetting.Status.Has(FanfouUserAccountStatus.Inactive))
        {
            throw new Exception("Fanfou account is already Disabled");
        }

        existingSetting.Status = existingSetting.Status.Add(FanfouUserAccountStatus.Inactive);

        var result = await fanfouUserAccountsRepository.UpdateAsync(existingSetting);
        if (result) fanfouUserAccountsCacheService.ClearCache(userId);
        return result ? Success() : new FailedResult<bool>(false, "0 rows Updated");
    }

    [HttpPost]
    public async Task<ApiResult<bool>> Activate()
    {
        var userId = currentUser.Id;
        var existingSetting = await fanfouUserAccountsRepository.GetFirstOrDefaultAsync(s => s.UserId == userId);
        if (existingSetting == null)
        {
            throw new Exception("No Fanfou account exists for the current user.");
        }

        if (!existingSetting.Status.Has(FanfouUserAccountStatus.Inactive))
        {
            throw new Exception("Fanfou account is already active");
        }

        existingSetting.Status = existingSetting.Status.Remove(FanfouUserAccountStatus.Inactive);

        var result = await fanfouUserAccountsRepository.UpdateAsync(existingSetting);
        if (result) fanfouUserAccountsCacheService.ClearCache(userId);
        return result ? new SuccessfulResult<bool>(true) : new FailedResult<bool>(false, "0 rows Updated");
    }

    [HttpPost]
    public async Task<ApiResult<bool>> NextSyncType()
    {
        var userId = currentUser.Id;
        var existingSetting = await fanfouUserAccountsRepository.GetFirstOrDefaultAsync(s => s.UserId == userId);
        if (existingSetting == null)
        {
            throw new Exception("No Fanfou account exists for the current user.");
        }

        existingSetting.SyncType = existingSetting.SyncType.Next();
        var result = await fanfouUserAccountsRepository.UpdateAsync(existingSetting);
        if (result) fanfouUserAccountsCacheService.ClearCache(userId);
        return result ? new SuccessfulResult<bool>(true) : new FailedResult<bool>(false, "0 rows Updated");
    }

    [HttpDelete]
    public async Task<ApiResult<bool>> Delete()
    {
        var userId = currentUser.Id;
        var existingSetting = await fanfouUserAccountsRepository.GetFirstOrDefaultAsync(s => s.UserId == userId);
        if (existingSetting == null)
        {
            throw new Exception("Fanfou account has already been Deleted");
        }

        var result = await fanfouUserAccountsRepository.DeleteAsync(s => s.UserId == userId);
        if (result) fanfouUserAccountsCacheService.ClearCache(userId);
        return result ? new SuccessfulResult<bool>(true) : new FailedResult<bool>(false, "0 rows deleted");
    }
}
