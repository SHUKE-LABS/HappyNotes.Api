using HappyNotes.Common;
using HappyNotes.Common.Enums;
using HappyNotes.Entities;
using HappyNotes.Services.interfaces;
using HappyNotes.Services.SyncQueue.Interfaces;
using HappyNotes.Services.SyncQueue.Models;
using Microsoft.Extensions.Logging;
using Moq;

namespace HappyNotes.Services.Tests;

public class FanfouSyncNoteServiceTests
{
    private Mock<IFanfouUserAccountCacheService> _mockCacheService;
    private Mock<ISyncQueueService> _mockSyncQueueService;
    private Mock<ILogger<FanfouSyncNoteService>> _mockLogger;
    private FanfouSyncNoteService _service;

    private const long TestUserId = 1;
    private const long TestUserAccountId = 10;

    [SetUp]
    public void Setup()
    {
        _mockCacheService = new Mock<IFanfouUserAccountCacheService>();
        _mockSyncQueueService = new Mock<ISyncQueueService>();
        _mockLogger = new Mock<ILogger<FanfouSyncNoteService>>();

        _service = new FanfouSyncNoteService(
            _mockCacheService.Object,
            _mockSyncQueueService.Object,
            _mockLogger.Object);
    }

    [TestCase(true, FanfouSyncType.All, "", true)]
    [TestCase(false, FanfouSyncType.All, "", true)]
    [TestCase(false, FanfouSyncType.PublicOnly, "", true)]
    [TestCase(true, FanfouSyncType.PublicOnly, "", false)]
    [TestCase(true, FanfouSyncType.TagFanfouOnly, "fanfou", true)]
    [TestCase(false, FanfouSyncType.TagFanfouOnly, "fanfou", true)]
    [TestCase(false, FanfouSyncType.TagFanfouOnly, "", false)]
    [TestCase(true, FanfouSyncType.TagFanfouOnly, "", false)]
    public async Task SyncNewNote_RespectsSyncRule(bool isPrivate, FanfouSyncType syncType, string tag, bool shouldSync)
    {
        // Arrange
        var account = new FanfouUserAccount
        {
            Id = TestUserAccountId,
            UserId = TestUserId,
            Status = FanfouUserAccountStatus.Normal,
            SyncType = syncType,
        };
        _mockCacheService.Setup(s => s.GetAsync(TestUserId)).ReturnsAsync(new List<FanfouUserAccount> { account });

        var note = new Note
        {
            Id = 123,
            UserId = TestUserId,
            IsPrivate = isPrivate,
            TagList = string.IsNullOrEmpty(tag) ? new List<string>() : new List<string> { tag },
        };

        // Act
        await _service.SyncNewNote(note, "test content");

        // Assert
        _mockSyncQueueService.Verify(
            s => s.EnqueueAsync(Constants.FanfouService, It.IsAny<SyncTask<FanfouSyncPayload>>()),
            shouldSync ? Times.Once() : Times.Never());
    }

    [Test]
    public async Task SyncNewNote_NoAccounts_DoesNotEnqueue()
    {
        _mockCacheService.Setup(s => s.GetAsync(TestUserId)).ReturnsAsync(new List<FanfouUserAccount>());

        var note = new Note { Id = 123, UserId = TestUserId, IsPrivate = false };
        await _service.SyncNewNote(note, "test content");

        _mockSyncQueueService.Verify(
            s => s.EnqueueAsync(It.IsAny<string>(), It.IsAny<SyncTask<FanfouSyncPayload>>()),
            Times.Never());
    }

    [Test]
    public async Task SyncDeleteNote_WithSyncedStatus_EnqueuesDelete()
    {
        var account = new FanfouUserAccount
        {
            Id = TestUserAccountId,
            UserId = TestUserId,
            Status = FanfouUserAccountStatus.Normal,
            SyncType = FanfouSyncType.All,
        };
        _mockCacheService.Setup(s => s.GetAsync(TestUserId)).ReturnsAsync(new List<FanfouUserAccount> { account });

        var note = new Note
        {
            Id = 123,
            UserId = TestUserId,
            FanfouStatusIds = $"{TestUserAccountId}:status-100",
        };

        await _service.SyncDeleteNote(note);

        _mockSyncQueueService.Verify(
            s => s.EnqueueAsync(Constants.FanfouService,
                It.Is<SyncTask<FanfouSyncPayload>>(t => t.Action == "DELETE" && t.Payload.StatusId == "status-100")),
            Times.Once());
    }

    [Test]
    public async Task SyncDeleteNote_NoSyncData_DoesNotEnqueue()
    {
        var note = new Note { Id = 123, UserId = TestUserId, FanfouStatusIds = null };
        await _service.SyncDeleteNote(note);

        _mockSyncQueueService.Verify(
            s => s.EnqueueAsync(It.IsAny<string>(), It.IsAny<SyncTask<FanfouSyncPayload>>()),
            Times.Never());
    }
}
