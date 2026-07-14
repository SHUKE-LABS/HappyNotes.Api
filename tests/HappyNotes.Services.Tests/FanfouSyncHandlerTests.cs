using System.Text.Json;
using Api.Framework.Helper;
using Api.Framework.Models;
using HappyNotes.Common;
using HappyNotes.Common.Enums;
using HappyNotes.Entities;
using HappyNotes.Repositories.interfaces;
using HappyNotes.Services.interfaces;
using HappyNotes.Services.SyncQueue.Configuration;
using HappyNotes.Services.SyncQueue.Handlers;
using HappyNotes.Services.SyncQueue.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace HappyNotes.Services.Tests;

public class FanfouSyncHandlerTests
{
    private Mock<IFanfouService> _mockFanfouService;
    private Mock<IFanfouUserAccountCacheService> _mockFanfouUserAccountCacheService;
    private Mock<INoteRepository> _mockNoteRepository;
    private Mock<IOptions<SyncQueueOptions>> _mockSyncQueueOptions;
    private Mock<IOptions<JwtConfig>> _mockJwtConfig;
    private Mock<ILogger<FanfouSyncHandler>> _mockLogger;
    private FanfouSyncHandler _handler;

    private const string TestJwtKey = "test_key_1234567890123456";
    private const string PlainAccessToken = "plain-access-token";
    private const string PlainAccessTokenSecret = "plain-access-secret";
    private const long TestUserId = 1;
    private const long TestUserAccountId = 10;
    private const long TestNoteId = 123;

    [SetUp]
    public void Setup()
    {
        _mockFanfouService = new Mock<IFanfouService>();
        _mockFanfouUserAccountCacheService = new Mock<IFanfouUserAccountCacheService>();
        _mockNoteRepository = new Mock<INoteRepository>();
        _mockSyncQueueOptions = new Mock<IOptions<SyncQueueOptions>>();
        _mockJwtConfig = new Mock<IOptions<JwtConfig>>();
        _mockLogger = new Mock<ILogger<FanfouSyncHandler>>();

        _mockSyncQueueOptions.Setup(x => x.Value).Returns(new SyncQueueOptions());
        _mockJwtConfig.Setup(x => x.Value).Returns(new JwtConfig { SymmetricSecurityKey = TestJwtKey });

        _handler = new FanfouSyncHandler(
            _mockFanfouService.Object,
            _mockFanfouUserAccountCacheService.Object,
            _mockNoteRepository.Object,
            _mockSyncQueueOptions.Object,
            _mockJwtConfig.Object,
            _mockLogger.Object);
    }

    private static SyncTask CreateTask(string action, FanfouSyncPayload payload, long entityId = TestNoteId, long userId = TestUserId)
    {
        var task = SyncTask.Create(Constants.FanfouService, action, entityId, userId, payload);
        return new SyncTask
        {
            Id = task.Id,
            Service = task.Service,
            Action = task.Action,
            EntityId = task.EntityId,
            UserId = task.UserId,
            Payload = task.Payload,
            AttemptCount = task.AttemptCount,
            CreatedAt = task.CreatedAt,
            ScheduledFor = task.ScheduledFor,
            Metadata = task.Metadata
        };
    }

    private void SetupUserAccount()
    {
        var accounts = new List<FanfouUserAccount>
        {
            new()
            {
                Id = TestUserAccountId,
                UserId = TestUserId,
                Status = FanfouUserAccountStatus.Normal,
                AccessToken = TextEncryptionHelper.Encrypt(PlainAccessToken, TestJwtKey),
                AccessTokenSecret = TextEncryptionHelper.Encrypt(PlainAccessTokenSecret, TestJwtKey),
            }
        };

        _mockFanfouUserAccountCacheService.Setup(s => s.GetAsync(TestUserId)).ReturnsAsync(accounts);
    }

    [Test]
    public async Task ProcessAsync_InvalidPayloadType_ReturnsFailure()
    {
        var task = new SyncTask { Service = Constants.FanfouService, Action = "CREATE", EntityId = TestNoteId, UserId = TestUserId, Payload = "invalid" };
        var result = await _handler.ProcessAsync(task, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Invalid payload type"));
        Assert.That(result.ShouldRetry, Is.False);
    }

    [Test]
    public async Task ProcessAsync_NoMatchingUserAccount_ReturnsFailure()
    {
        var payload = new FanfouSyncPayload { UserAccountId = 999, FullContent = "hi" };
        var task = CreateTask("CREATE", payload);
        SetupUserAccount();

        var result = await _handler.ProcessAsync(task, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo($"No Fanfou user account found for user {TestUserId} and account 999"));
        Assert.That(result.ShouldRetry, Is.False);
    }

    [Test]
    public async Task ProcessAsync_UnknownAction_ReturnsFailure()
    {
        var payload = new FanfouSyncPayload { UserAccountId = TestUserAccountId, FullContent = "hi" };
        var task = CreateTask("WHAT", payload);
        SetupUserAccount();

        var result = await _handler.ProcessAsync(task, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Unknown action: WHAT"));
        Assert.That(result.ShouldRetry, Is.False);
    }

    [Test]
    public async Task ProcessCreateAction_HappyPath_SendsStatusAndUpdatesNote()
    {
        var payload = new FanfouSyncPayload { UserAccountId = TestUserAccountId, FullContent = "Test content" };
        var task = CreateTask("CREATE", payload);

        SetupUserAccount();
        _mockFanfouService
            .Setup(s => s.SendStatusAsync(PlainAccessToken, PlainAccessTokenSecret, payload.FullContent, TestNoteId))
            .ReturnsAsync("status-100");
        _mockNoteRepository
            .Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Note, bool>>>(), null))
            .ReturnsAsync(new Note { Id = TestNoteId, FanfouStatusIds = null });

        var result = await _handler.ProcessAsync(task, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        _mockFanfouService.Verify(s => s.SendStatusAsync(PlainAccessToken, PlainAccessTokenSecret, payload.FullContent, TestNoteId), Times.Once);
        _mockNoteRepository.Verify(r => r.UpdateAsync(
            It.Is<System.Linq.Expressions.Expression<Func<Note, Note>>>(expr =>
                expr.Compile()(new Note()).FanfouStatusIds == $"{TestUserAccountId}:status-100"),
            It.IsAny<System.Linq.Expressions.Expression<Func<Note, bool>>>()
        ), Times.Once);
    }

    [Test]
    public async Task ProcessCreateAction_SendThrows_ReturnsFailureWithRetry()
    {
        var payload = new FanfouSyncPayload { UserAccountId = TestUserAccountId, FullContent = "Test content" };
        var task = CreateTask("CREATE", payload);

        SetupUserAccount();
        _mockFanfouService
            .Setup(s => s.SendStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>()))
            .ThrowsAsync(new Exception("Fanfou unreachable"));

        var result = await _handler.ProcessAsync(task, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Fanfou unreachable"));
        Assert.That(result.ShouldRetry, Is.True);
    }

    [Test]
    public async Task ProcessUpdateAction_EmptyStatusId_ReturnsFailure()
    {
        var payload = new FanfouSyncPayload { UserAccountId = TestUserAccountId, FullContent = "Updated", StatusId = null };
        var task = CreateTask("UPDATE", payload);
        SetupUserAccount();

        var result = await _handler.ProcessAsync(task, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("StatusId is required for UPDATE action"));
        Assert.That(result.ShouldRetry, Is.False);
    }

    [Test]
    public async Task ProcessUpdateAction_HappyPath_DeletesOldPostsNewAndUpdatesNote()
    {
        var payload = new FanfouSyncPayload { UserAccountId = TestUserAccountId, FullContent = "Updated content", StatusId = "status-old" };
        var task = CreateTask("UPDATE", payload);

        SetupUserAccount();
        _mockFanfouService.Setup(s => s.DeleteStatusAsync(PlainAccessToken, PlainAccessTokenSecret, "status-old")).Returns(Task.CompletedTask);
        _mockFanfouService.Setup(s => s.SendStatusAsync(PlainAccessToken, PlainAccessTokenSecret, payload.FullContent, TestNoteId)).ReturnsAsync("status-new");
        _mockNoteRepository
            .Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Note, bool>>>(), null))
            .ReturnsAsync(new Note { Id = TestNoteId, FanfouStatusIds = $"{TestUserAccountId}:status-old" });

        var result = await _handler.ProcessAsync(task, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        _mockFanfouService.Verify(s => s.DeleteStatusAsync(PlainAccessToken, PlainAccessTokenSecret, "status-old"), Times.Once);
        _mockFanfouService.Verify(s => s.SendStatusAsync(PlainAccessToken, PlainAccessTokenSecret, payload.FullContent, TestNoteId), Times.Once);
        _mockNoteRepository.Verify(r => r.UpdateAsync(
            It.Is<System.Linq.Expressions.Expression<Func<Note, Note>>>(expr =>
                expr.Compile()(new Note()).FanfouStatusIds == $"{TestUserAccountId}:status-new"),
            It.IsAny<System.Linq.Expressions.Expression<Func<Note, bool>>>()
        ), Times.Once);
    }

    [Test]
    public async Task ProcessDeleteAction_EmptyStatusId_ReturnsFailure()
    {
        var payload = new FanfouSyncPayload { UserAccountId = TestUserAccountId, StatusId = null };
        var task = CreateTask("DELETE", payload);
        SetupUserAccount();

        var result = await _handler.ProcessAsync(task, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("StatusId is required for DELETE action"));
        Assert.That(result.ShouldRetry, Is.False);
    }

    [Test]
    public async Task ProcessDeleteAction_HappyPath_DeletesStatusAndUpdatesNote()
    {
        var payload = new FanfouSyncPayload { UserAccountId = TestUserAccountId, StatusId = "status-100" };
        var task = CreateTask("DELETE", payload);

        SetupUserAccount();
        _mockFanfouService.Setup(s => s.DeleteStatusAsync(PlainAccessToken, PlainAccessTokenSecret, "status-100")).Returns(Task.CompletedTask);
        _mockNoteRepository
            .Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Note, bool>>>(), null))
            .ReturnsAsync(new Note { Id = TestNoteId, FanfouStatusIds = $"{TestUserAccountId}:status-100,99:status-999" });

        var result = await _handler.ProcessAsync(task, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        _mockFanfouService.Verify(s => s.DeleteStatusAsync(PlainAccessToken, PlainAccessTokenSecret, "status-100"), Times.Once);
        _mockNoteRepository.Verify(r => r.UpdateAsync(
            It.Is<System.Linq.Expressions.Expression<Func<Note, Note>>>(expr =>
                expr.Compile()(new Note()).FanfouStatusIds == "99:status-999"),
            It.IsAny<System.Linq.Expressions.Expression<Func<Note, bool>>>()
        ), Times.Once);
    }

    [Test]
    public async Task ProcessAsync_JsonElementPayload_HandlesCorrectly()
    {
        var payload = new FanfouSyncPayload { UserAccountId = TestUserAccountId, FullContent = "Test content" };
        var jsonPayload = JsonSerializer.SerializeToElement(payload);
        var task = new SyncTask { EntityId = TestNoteId, UserId = TestUserId, Service = Constants.FanfouService, Action = "CREATE", Payload = jsonPayload };

        SetupUserAccount();
        _mockFanfouService
            .Setup(s => s.SendStatusAsync(PlainAccessToken, PlainAccessTokenSecret, payload.FullContent, TestNoteId))
            .ReturnsAsync("status-200");
        _mockNoteRepository
            .Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Note, bool>>>(), null))
            .ReturnsAsync(new Note { Id = TestNoteId, FanfouStatusIds = null });

        var result = await _handler.ProcessAsync(task, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        _mockFanfouService.Verify(s => s.SendStatusAsync(PlainAccessToken, PlainAccessTokenSecret, payload.FullContent, TestNoteId), Times.Once);
    }

    [Test]
    public void ServiceName_ReturnsCorrectValue()
    {
        Assert.That(_handler.ServiceName, Is.EqualTo(Constants.FanfouService));
    }

    [Test]
    public void CalculateRetryDelay_UsesDefaultExponentialBackoff()
    {
        Assert.That(_handler.CalculateRetryDelay(1), Is.EqualTo(TimeSpan.FromMinutes(1)));
        Assert.That(_handler.CalculateRetryDelay(2), Is.EqualTo(TimeSpan.FromMinutes(2)));
        Assert.That(_handler.CalculateRetryDelay(3), Is.EqualTo(TimeSpan.FromMinutes(4)));
    }
}
