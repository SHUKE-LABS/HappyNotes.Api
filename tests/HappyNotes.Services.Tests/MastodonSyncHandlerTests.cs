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
using Mastonet.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace HappyNotes.Services.Tests;

public class MastodonSyncHandlerTests
{
    private Mock<IMastodonTootService> _mockMastodonTootService;
    private Mock<IMastodonUserAccountCacheService> _mockMastodonUserAccountCacheService;
    private Mock<INoteRepository> _mockNoteRepository;
    private Mock<IOptions<SyncQueueOptions>> _mockSyncQueueOptions;
    private Mock<IOptions<JwtConfig>> _mockJwtConfig;
    private Mock<ILogger<MastodonSyncHandler>> _mockLogger;
    private MastodonSyncHandler _mastodonSyncHandler;

    private const string TestJwtKey = "test_key_1234567890123456";
    private const string TestInstanceUrl = "https://mastodon.example.com";
    private const long TestUserId = 1;
    private const long TestUserAccountId = 10;
    private const long TestNoteId = 123;

    [SetUp]
    public void Setup()
    {
        _mockMastodonTootService = new Mock<IMastodonTootService>();
        _mockMastodonUserAccountCacheService = new Mock<IMastodonUserAccountCacheService>();
        _mockNoteRepository = new Mock<INoteRepository>();
        _mockSyncQueueOptions = new Mock<IOptions<SyncQueueOptions>>();
        _mockJwtConfig = new Mock<IOptions<JwtConfig>>();
        _mockLogger = new Mock<ILogger<MastodonSyncHandler>>();

        var syncQueueOptions = new SyncQueueOptions();
        _mockSyncQueueOptions.Setup(x => x.Value).Returns(syncQueueOptions);

        var jwtConfig = new JwtConfig { SymmetricSecurityKey = TestJwtKey };
        _mockJwtConfig.Setup(x => x.Value).Returns(jwtConfig);

        _mastodonSyncHandler = new MastodonSyncHandler(
            _mockMastodonTootService.Object,
            _mockMastodonUserAccountCacheService.Object,
            _mockNoteRepository.Object,
            _mockSyncQueueOptions.Object,
            _mockJwtConfig.Object,
            _mockLogger.Object);
    }

    private SyncTask CreateTask(string action, MastodonSyncPayload payload, long entityId = TestNoteId, long userId = TestUserId)
    {
        var task = SyncTask.Create("mastodon", action, entityId, userId, payload);
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

    private void SetupUserAccount(long userId = TestUserId, long userAccountId = TestUserAccountId, string plainAccessToken = "plain-access-token")
    {
        var userAccounts = new List<MastodonUserAccount>
        {
            new()
            {
                Id = userAccountId,
                UserId = userId,
                InstanceUrl = TestInstanceUrl,
                Status = MastodonUserAccountStatus.Normal,
                AccessToken = TextEncryptionHelper.Encrypt(plainAccessToken, TestJwtKey)
            }
        };

        _mockMastodonUserAccountCacheService
            .Setup(s => s.GetAsync(userId))
            .ReturnsAsync(userAccounts);
    }

    #region Guard Clause Tests

    [Test]
    public async Task ProcessAsync_InvalidPayloadType_ReturnsFailure()
    {
        // Arrange
        var task = new SyncTask
        {
            Service = "mastodon",
            Action = "CREATE",
            EntityId = TestNoteId,
            UserId = TestUserId,
            Payload = "invalid_payload"
        };

        // Act
        var result = await _mastodonSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Invalid payload type"));
        Assert.That(result.ShouldRetry, Is.False);
    }

    [Test]
    public async Task ProcessAsync_NullPayloadAfterDeserialization_ReturnsFailure()
    {
        // Arrange
        var jsonElement = JsonSerializer.SerializeToElement<MastodonSyncPayload?>(null);
        var task = new SyncTask
        {
            Service = "mastodon",
            Action = "CREATE",
            EntityId = TestNoteId,
            UserId = TestUserId,
            Payload = jsonElement
        };

        // Act
        var result = await _mastodonSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Failed to deserialize payload"));
        Assert.That(result.ShouldRetry, Is.False);
    }

    [Test]
    public async Task ProcessAsync_NoMatchingUserAccount_ReturnsFailure()
    {
        // Arrange
        var payload = new MastodonSyncPayload
        {
            InstanceUrl = TestInstanceUrl,
            UserAccountId = 999, // does not match any cached account
            FullContent = "Test content"
        };
        var task = CreateTask("CREATE", payload);

        SetupUserAccount(TestUserId, TestUserAccountId);

        // Act
        var result = await _mastodonSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo($"No Mastodon user account found for user {TestUserId} and account 999"));
        Assert.That(result.ShouldRetry, Is.False);
    }

    [Test]
    public async Task ProcessAsync_UnknownAction_ReturnsFailure()
    {
        // Arrange
        var payload = new MastodonSyncPayload
        {
            InstanceUrl = TestInstanceUrl,
            UserAccountId = TestUserAccountId,
            FullContent = "Test content"
        };
        var task = CreateTask("UNKNOWN_ACTION", payload);

        SetupUserAccount();

        // Act
        var result = await _mastodonSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Unknown action: UNKNOWN_ACTION"));
        Assert.That(result.ShouldRetry, Is.False);
    }

    #endregion

    #region CREATE Action Tests

    [Test]
    public async Task ProcessCreateAction_HappyPath_SendsTootAndUpdatesNote()
    {
        // Arrange
        var payload = new MastodonSyncPayload
        {
            InstanceUrl = TestInstanceUrl,
            UserAccountId = TestUserAccountId,
            FullContent = "Test content",
            IsPrivate = false,
            IsMarkdown = false
        };
        var task = CreateTask("CREATE", payload);

        SetupUserAccount();
        _mockMastodonTootService
            .Setup(s => s.SendTootAsync(TestInstanceUrl, "plain-access-token", payload.FullContent, payload.IsPrivate, payload.IsMarkdown))
            .ReturnsAsync(new Status { Id = "toot-100" });

        var testNote = new Note { Id = TestNoteId, MastodonTootIds = null };
        _mockNoteRepository
            .Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Note, bool>>>(), null))
            .ReturnsAsync(testNote);

        // Act
        var result = await _mastodonSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        _mockMastodonTootService.Verify(s => s.SendTootAsync(TestInstanceUrl, "plain-access-token", payload.FullContent, payload.IsPrivate, payload.IsMarkdown), Times.Once);
        _mockNoteRepository.Verify(r => r.UpdateAsync(
            It.Is<System.Linq.Expressions.Expression<Func<Note, Note>>>(expr =>
                expr.Compile()(new Note()).MastodonTootIds == $"{TestUserAccountId}:toot-100"),
            It.IsAny<System.Linq.Expressions.Expression<Func<Note, bool>>>()
        ), Times.Once);
    }

    [Test]
    public async Task ProcessCreateAction_SendTootThrows_ReturnsFailureWithRetry()
    {
        // Arrange
        var payload = new MastodonSyncPayload
        {
            InstanceUrl = TestInstanceUrl,
            UserAccountId = TestUserAccountId,
            FullContent = "Test content"
        };
        var task = CreateTask("CREATE", payload);

        SetupUserAccount();
        _mockMastodonTootService
            .Setup(s => s.SendTootAsync(TestInstanceUrl, "plain-access-token", payload.FullContent, payload.IsPrivate, payload.IsMarkdown))
            .ThrowsAsync(new Exception("Mastodon instance unreachable"));

        // Act
        var result = await _mastodonSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Mastodon instance unreachable"));
        Assert.That(result.ShouldRetry, Is.True);
    }

    #endregion

    #region UPDATE Action Tests

    [Test]
    public async Task ProcessUpdateAction_EmptyTootId_ReturnsFailure()
    {
        // Arrange
        var payload = new MastodonSyncPayload
        {
            InstanceUrl = TestInstanceUrl,
            UserAccountId = TestUserAccountId,
            FullContent = "Updated content",
            TootId = null
        };
        var task = CreateTask("UPDATE", payload);

        SetupUserAccount();

        // Act
        var result = await _mastodonSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("TootId is required for UPDATE action"));
        Assert.That(result.ShouldRetry, Is.False);
    }

    [Test]
    public async Task ProcessUpdateAction_HappyPath_EditsToot()
    {
        // Arrange
        var payload = new MastodonSyncPayload
        {
            InstanceUrl = TestInstanceUrl,
            UserAccountId = TestUserAccountId,
            FullContent = "Updated content",
            TootId = "toot-100",
            IsPrivate = true,
            IsMarkdown = true
        };
        var task = CreateTask("UPDATE", payload);

        SetupUserAccount();
        _mockMastodonTootService
            .Setup(s => s.EditTootAsync(TestInstanceUrl, "plain-access-token", "toot-100", payload.FullContent, payload.IsPrivate, payload.IsMarkdown))
            .ReturnsAsync(new Status { Id = "toot-100" });

        // Act
        var result = await _mastodonSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        _mockMastodonTootService.Verify(s => s.EditTootAsync(TestInstanceUrl, "plain-access-token", "toot-100", payload.FullContent, payload.IsPrivate, payload.IsMarkdown), Times.Once);
    }

    #endregion

    #region DELETE Action Tests

    [Test]
    public async Task ProcessDeleteAction_EmptyTootId_ReturnsFailure()
    {
        // Arrange
        var payload = new MastodonSyncPayload
        {
            InstanceUrl = TestInstanceUrl,
            UserAccountId = TestUserAccountId,
            TootId = null
        };
        var task = CreateTask("DELETE", payload);

        SetupUserAccount();

        // Act
        var result = await _mastodonSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("TootId is required for DELETE action"));
        Assert.That(result.ShouldRetry, Is.False);
    }

    [Test]
    public async Task ProcessDeleteAction_HappyPath_DeletesTootAndUpdatesNote()
    {
        // Arrange
        var payload = new MastodonSyncPayload
        {
            InstanceUrl = TestInstanceUrl,
            UserAccountId = TestUserAccountId,
            TootId = "toot-100"
        };
        var task = CreateTask("DELETE", payload);

        SetupUserAccount();
        _mockMastodonTootService
            .Setup(s => s.DeleteTootAsync(TestInstanceUrl, "plain-access-token", "toot-100"))
            .Returns(Task.CompletedTask);

        var testNote = new Note { Id = TestNoteId, MastodonTootIds = $"{TestUserAccountId}:toot-100,99:toot-999" };
        _mockNoteRepository
            .Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Note, bool>>>(), null))
            .ReturnsAsync(testNote);

        // Act
        var result = await _mastodonSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        _mockMastodonTootService.Verify(s => s.DeleteTootAsync(TestInstanceUrl, "plain-access-token", "toot-100"), Times.Once);
        _mockNoteRepository.Verify(r => r.UpdateAsync(
            It.Is<System.Linq.Expressions.Expression<Func<Note, Note>>>(expr =>
                expr.Compile()(new Note()).MastodonTootIds == "99:toot-999"),
            It.IsAny<System.Linq.Expressions.Expression<Func<Note, bool>>>()
        ), Times.Once);
    }

    #endregion

    #region Payload Handling Tests

    [Test]
    public async Task ProcessAsync_JsonElementPayload_HandlesCorrectly()
    {
        // Arrange
        var payload = new MastodonSyncPayload
        {
            InstanceUrl = TestInstanceUrl,
            UserAccountId = TestUserAccountId,
            FullContent = "Test content"
        };
        var jsonPayload = JsonSerializer.SerializeToElement(payload);
        var task = new SyncTask
        {
            EntityId = TestNoteId,
            UserId = TestUserId,
            Service = "mastodon",
            Action = "CREATE",
            Payload = jsonPayload
        };

        SetupUserAccount();
        _mockMastodonTootService
            .Setup(s => s.SendTootAsync(TestInstanceUrl, "plain-access-token", payload.FullContent, payload.IsPrivate, payload.IsMarkdown))
            .ReturnsAsync(new Status { Id = "toot-200" });

        var testNote = new Note { Id = TestNoteId, MastodonTootIds = null };
        _mockNoteRepository
            .Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Note, bool>>>(), null))
            .ReturnsAsync(testNote);

        // Act
        var result = await _mastodonSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        _mockMastodonTootService.Verify(s => s.SendTootAsync(TestInstanceUrl, "plain-access-token", payload.FullContent, payload.IsPrivate, payload.IsMarkdown), Times.Once);
    }

    #endregion

    #region Metadata Tests

    [Test]
    public void ServiceName_ReturnsCorrectValue()
    {
        // Assert
        Assert.That(_mastodonSyncHandler.ServiceName, Is.EqualTo(Constants.MastodonService));
    }

    [Test]
    public void CalculateRetryDelay_UsesDefaultExponentialBackoff()
    {
        // Act
        var delay1 = _mastodonSyncHandler.CalculateRetryDelay(1);
        var delay2 = _mastodonSyncHandler.CalculateRetryDelay(2);
        var delay3 = _mastodonSyncHandler.CalculateRetryDelay(3);

        // Assert
        Assert.That(delay1, Is.EqualTo(TimeSpan.FromMinutes(1))); // 2^0
        Assert.That(delay2, Is.EqualTo(TimeSpan.FromMinutes(2))); // 2^1
        Assert.That(delay3, Is.EqualTo(TimeSpan.FromMinutes(4))); // 2^2
    }

    #endregion
}
