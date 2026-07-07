using Api.Framework;
using Api.Framework.Extensions;
using Api.Framework.Helper;
using Api.Framework.Models;
using Api.Framework.Result;
using AutoMapper;
using HappyNotes.Common;
using HappyNotes.Common.Enums;
using HappyNotes.Dto;
using HappyNotes.Entities;
using HappyNotes.Services.interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HappyNotes.Api.Controllers;

public class TelegramSettingsController(
    IMapper mapper,
    ICurrentUser currentUser,
    IRepositoryBase<TelegramSettings> telegramSyncSettingsRepository,
    ITelegramSettingsCacheService telegramSettingsCacheService,
    ITelegramService telegramService,
    IOptions<JwtConfig> jwtConfig
) : BaseController
{
    private readonly JwtConfig _jwtConfig = jwtConfig.Value;

    [HttpGet]
    public async Task<ApiResult<List<TelegramSettingsDto>>> GetAll()
    {
        var userId = currentUser.Id;
        var settings = await telegramSyncSettingsRepository.GetListAsync(s => s.UserId.Equals(userId), o => o.CreatedAt);
        return new SuccessfulResult<List<TelegramSettingsDto>>(mapper.Map<List<TelegramSettingsDto>>(settings));
    }

    [HttpPost]
    public async Task<ApiResult<bool>> Add(TelegramSettingsDto settingsDto)
    {
        var userId = currentUser.Id;
        var existingSetting = await telegramSyncSettingsRepository.GetFirstOrDefaultAsync(
            s => s.UserId == userId && s.SyncType == settingsDto.SyncType && s.SyncValue == settingsDto.SyncValue);

        if (existingSetting != null)
        {
            throw new Exception(
                $"SyncType: {settingsDto.SyncType} has already been set to channel: {existingSetting.ChannelId}");
        }

        if (string.IsNullOrWhiteSpace(settingsDto.EncryptedToken))
            throw new ArgumentException("EncryptedToken is required");
        if (string.IsNullOrWhiteSpace(settingsDto.ChannelId))
            throw new ArgumentException("ChannelId is required");
        var now = DateTime.UtcNow.ToUnixTimeSeconds();
        var settings = new TelegramSettings
        {
            UserId = userId,
            SyncType = settingsDto.SyncType,
            SyncValue = settingsDto.SyncValue,
            // _jwtConfig.SymmetricSecurityKey is being used for two purposes, I know it is not good, but it is convenient.
            EncryptedToken =
                TextEncryptionHelper.Encrypt(settingsDto.EncryptedToken, _jwtConfig.SymmetricSecurityKey),
            TokenRemark = settingsDto.TokenRemark,
            ChannelId = settingsDto.ChannelId,
            ChannelName = settingsDto.ChannelName,
            Status = TelegramSettingStatus.Created,
            CreatedAt = now,
        };

        // check special token value
        if (settingsDto.EncryptedToken.Equals(Constants.TelegramSameTokenFlag))
        {
            var lastSetting = await telegramSyncSettingsRepository.GetFirstOrDefaultAsync(
                s => s.UserId == userId, "Id DESC");
            if (lastSetting != null)
            {
                settings.EncryptedToken = lastSetting.EncryptedToken;
            }
            else
            {
                throw new Exception("No previous token found");
            }
        }
        var result = await telegramSyncSettingsRepository.InsertAsync(settings);
        if (result) telegramSettingsCacheService.ClearCache(userId);
        return result ? new SuccessfulResult<bool>(true) : new FailedResult<bool>(false, "0 rows inserted");
    }

    [HttpPost]
    public async Task<ApiResult<bool>> Disable(TelegramSettingsDto settingsDto)
    {
        var userId = currentUser.Id;
        var existingSetting = await telegramSyncSettingsRepository.GetFirstOrDefaultAsync(
            s => s.UserId == userId && s.SyncType == settingsDto.SyncType && s.SyncValue == settingsDto.SyncValue);

        if (existingSetting == null)
        {
            throw new Exception(
                $"Setting with {settingsDto.Id} doesn't exist.");
        }

        if (existingSetting.Status.Has(TelegramSettingStatus.Inactive))
        {
            throw new Exception(
                $"Setting with {settingsDto.Id} is already Disabled");
        }

        existingSetting.Status = existingSetting.Status.Add(TelegramSettingStatus.Inactive);

        var result = await telegramSyncSettingsRepository.UpdateAsync(existingSetting);
        if (result) telegramSettingsCacheService.ClearCache(userId);
        return result ? new SuccessfulResult<bool>(true) : new FailedResult<bool>(false, "0 rows Updated");
    }

    [HttpPost]
    public async Task<ApiResult<bool>> Activate(TelegramSettingsDto settingsDto)
    {
        var userId = currentUser.Id;
        var existingSetting = await telegramSyncSettingsRepository.GetFirstOrDefaultAsync(
            s => s.UserId == userId && s.SyncType == settingsDto.SyncType && s.SyncValue == settingsDto.SyncValue);

        if (existingSetting == null)
        {
            throw new Exception(
                $"Setting with {settingsDto.Id} doesn't exist.");
        }

        if (!existingSetting.Status.Has(TelegramSettingStatus.Inactive))
        {
            throw new Exception(
                $"Setting with {settingsDto.Id} is already active");
        }

        existingSetting.Status = existingSetting.Status.Remove(TelegramSettingStatus.Inactive);

        var result = await telegramSyncSettingsRepository.UpdateAsync(existingSetting);
        if (result) telegramSettingsCacheService.ClearCache(userId);
        return result ? new SuccessfulResult<bool>(true) : new FailedResult<bool>(false, "0 rows Updated");
    }

    [HttpDelete]
    public async Task<ApiResult<bool>> Delete(TelegramSettingsDto settingsDto)
    {
        var userId = currentUser.Id;
        var existingSetting = await telegramSyncSettingsRepository.GetFirstOrDefaultAsync(
            s => s.UserId == userId && s.SyncType == settingsDto.SyncType && s.SyncValue == settingsDto.SyncValue);

        if (existingSetting == null)
        {
            throw new Exception(
                $"SyncType: {settingsDto.SyncType} has already been Deleted");
        }

        var result = await telegramSyncSettingsRepository.DeleteAsync(s => s.UserId == userId &&
                                                                           s.SyncType == existingSetting.SyncType &&
                                                                           s.SyncValue == existingSetting.SyncValue);
        if (result) telegramSettingsCacheService.ClearCache(userId);
        return result ? new SuccessfulResult<bool>(true) : new FailedResult<bool>(false, "0 rows deleted");
    }

    [HttpPost]
    public async Task<ApiResult<bool>> Test(TelegramSettingsDto settingsDto)
    {
        var userId = currentUser.Id;
        var existingSetting = await telegramSyncSettingsRepository.GetFirstOrDefaultAsync(
            s => s.UserId == userId && s.SyncType == settingsDto.SyncType && s.SyncValue == settingsDto.SyncValue);

        if (existingSetting == null)
        {
            throw new Exception("Could find this setting.");
        }
        if (string.IsNullOrWhiteSpace(existingSetting.EncryptedToken) || string.IsNullOrWhiteSpace(existingSetting.ChannelId))
        {
            throw new Exception("token or channelId is empty, cannot test.");
        }
        var token = TextEncryptionHelper.Decrypt(existingSetting.EncryptedToken, _jwtConfig.SymmetricSecurityKey);
        var messageId = await telegramService.SendMessageAsync(token, existingSetting.ChannelId, "Hello *world!*", true);
        if (messageId > 0)
        {
            existingSetting.Status = existingSetting.Status.Add(TelegramSettingStatus.Tested);
            await telegramSyncSettingsRepository.UpdateAsync(existingSetting);
            // delete test message to avoid confusing
            await telegramService.DeleteMessageAsync(token, existingSetting.ChannelId, messageId);
        }
        return messageId > 0 ? new SuccessfulResult<bool>(true) : new FailedResult<bool>(false, "test failed.");
    }
}
