using System.Text.Json;
using Api.Framework.Helper;
using Api.Framework.Models;
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

public class ManticoreSearchSyncHandlerTests
{
    private Mock<ISearchService> _mockSearchService;
    private Mock<IEmbeddingService> _mockEmbeddingService;
    private Mock<INoteRepository> _mockNoteRepository;
    private Mock<IOptions<SyncQueueOptions>> _mockSyncQueueOptions;
    private Mock<ILogger<ManticoreSearchSyncHandler>> _mockLogger;
    private ManticoreSearchSyncHandler _manticoreSearchSyncHandler;

    private const string TestJwtKey = "test_key_1234567890123456";

    [SetUp]
    public void Setup()
    {
        _mockSearchService = new Mock<ISearchService>();
        _mockEmbeddingService = new Mock<IEmbeddingService>();
        _mockNoteRepository = new Mock<INoteRepository>();
        _mockSyncQueueOptions = new Mock<IOptions<SyncQueueOptions>>();
        _mockLogger = new Mock<ILogger<ManticoreSearchSyncHandler>>();

        var syncQueueOptions = new SyncQueueOptions();
        _mockSyncQueueOptions.Setup(x => x.Value).Returns(syncQueueOptions);

        // Default: embedding backend "unavailable" so existing content-sync assertions see a null vector.
        // Tests that exercise the vector path override this.
        _mockEmbeddingService.Setup(x => x.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((float[]?)null);

        _manticoreSearchSyncHandler = new ManticoreSearchSyncHandler(
            _mockSearchService.Object,
            _mockEmbeddingService.Object,
            _mockNoteRepository.Object,
            _mockSyncQueueOptions.Object,
            _mockLogger.Object);
    }

    [Test]
    public async Task ProcessAsync_CreateAction_CallsSyncNoteToIndexAsync()
    {
        // Arrange
        var note = new Note { Id = 1, UserId = 123, Content = "Test content" };
        var payload = new ManticoreSearchSyncPayload
        {
            Action = "CREATE",
            FullContent = "Test content"
        };
        var task = CreateTask("CREATE", 1, 123, payload);

        _mockNoteRepository.Setup(x => x.Get(1)).ReturnsAsync(note);
        _mockSearchService.Setup(x => x.SyncNoteToIndexAsync(note, "Test content")).Returns(Task.CompletedTask);

        // Act
        var result = await _manticoreSearchSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        _mockSearchService.Verify(x => x.SyncNoteToIndexAsync(note, "Test content"), Times.Once);
    }

    [Test]
    public async Task ProcessAsync_CreateAction_EmbedsContent_AndPassesVectorToIndex()
    {
        // Arrange
        var note = new Note { Id = 1, UserId = 123, Content = "Test content" };
        var payload = new ManticoreSearchSyncPayload { Action = "CREATE", FullContent = "Test content" };
        var task = CreateTask("CREATE", 1, 123, payload);
        var vector = new[] { 0.1f, 0.2f, 0.3f };

        _mockNoteRepository.Setup(x => x.Get(1)).ReturnsAsync(note);
        _mockEmbeddingService.Setup(x => x.EmbedAsync("Test content", It.IsAny<CancellationToken>())).ReturnsAsync(vector);

        // Act
        var result = await _manticoreSearchSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        _mockEmbeddingService.Verify(x => x.EmbedAsync("Test content", It.IsAny<CancellationToken>()), Times.Once);
        _mockSearchService.Verify(x => x.SyncNoteToIndexAsync(note, "Test content", vector), Times.Once);
    }

    private SyncTask CreateTask(string action, long entityId, long userId, ManticoreSearchSyncPayload payload)
    {
        var task = SyncTask.Create("manticoresearch", action, entityId, userId, payload);
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

    [Test]
    public async Task ProcessAsync_UpdateAction_CallsSyncNoteToIndexAsync()
    {
        // Arrange
        var note = new Note { Id = 1, UserId = 123, Content = "Updated content" };
        var payload = new ManticoreSearchSyncPayload
        {
            Action = "UPDATE",
            FullContent = "Updated content"
        };
        var task = CreateTask("UPDATE", 1, 123, payload);

        _mockNoteRepository.Setup(x => x.Get(1)).ReturnsAsync(note);
        _mockSearchService.Setup(x => x.SyncNoteToIndexAsync(note, "Updated content")).Returns(Task.CompletedTask);

        // Act
        var result = await _manticoreSearchSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        _mockSearchService.Verify(x => x.SyncNoteToIndexAsync(note, "Updated content"), Times.Once);
    }

    [Test]
    public async Task ProcessAsync_DeleteAction_CallsDeleteNoteFromIndexAsync()
    {
        // Arrange
        var note = new Note { Id = 1, UserId = 123, Content = "Test content" };
        var payload = new ManticoreSearchSyncPayload
        {
            Action = "DELETE",
            FullContent = string.Empty
        };
        var task = CreateTask("DELETE", 1, 123, payload);

        _mockNoteRepository.Setup(x => x.Get(1)).ReturnsAsync(note);
        _mockSearchService.Setup(x => x.DeleteNoteFromIndexAsync(1)).Returns(Task.CompletedTask);

        // Act
        var result = await _manticoreSearchSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        _mockSearchService.Verify(x => x.DeleteNoteFromIndexAsync(1), Times.Once);
    }

    [Test]
    public async Task ProcessAsync_UndeleteAction_CallsUndeleteNoteFromIndexAsync()
    {
        // Arrange
        var note = new Note { Id = 1, UserId = 123, Content = "Test content" };
        var payload = new ManticoreSearchSyncPayload
        {
            Action = "UNDELETE",
            FullContent = string.Empty
        };
        var task = CreateTask("UNDELETE", 1, 123, payload);

        _mockNoteRepository.Setup(x => x.Get(1)).ReturnsAsync(note);
        _mockSearchService.Setup(x => x.UndeleteNoteFromIndexAsync(1)).Returns(Task.CompletedTask);

        // Act
        var result = await _manticoreSearchSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        _mockSearchService.Verify(x => x.UndeleteNoteFromIndexAsync(1), Times.Once);
    }

    [Test]
    public async Task ProcessAsync_NoteNotFound_ReturnsFailureForNonDeleteActions()
    {
        // Arrange
        var payload = new ManticoreSearchSyncPayload
        {
            Action = "CREATE",
            FullContent = "Test content"
        };
        var task = CreateTask("CREATE", 999, 123, payload);

        _mockNoteRepository.Setup(x => x.Get(999)).ReturnsAsync((Note?)null);

        // Act
        var result = await _manticoreSearchSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Note not found"));
        Assert.That(result.ShouldRetry, Is.False);
    }

    [Test]
    public async Task ProcessAsync_NoteNotFoundForDelete_ReturnsSuccess()
    {
        // Arrange
        var payload = new ManticoreSearchSyncPayload
        {
            Action = "DELETE",
            FullContent = string.Empty
        };
        var task = CreateTask("DELETE", 999, 123, payload);

        _mockNoteRepository.Setup(x => x.Get(999)).ReturnsAsync((Note?)null);

        // Act
        var result = await _manticoreSearchSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
    }

    [Test]
    public async Task ProcessAsync_InvalidPayloadType_ReturnsFailure()
    {
        // Arrange
        var task = new SyncTask
        {
            Service = "manticoresearch",
            Action = "CREATE",
            EntityId = 1,
            UserId = 123,
            Payload = "invalid_payload"
        };

        // Act
        var result = await _manticoreSearchSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Invalid payload type"));
        Assert.That(result.ShouldRetry, Is.False);
    }

    [Test]
    public async Task ProcessAsync_SearchServiceThrowsException_ReturnsFailure()
    {
        // Arrange
        var note = new Note { Id = 1, UserId = 123, Content = "Test content" };
        var payload = new ManticoreSearchSyncPayload
        {
            Action = "CREATE",
            FullContent = "Test content"
        };
        var task = CreateTask("CREATE", 1, 123, payload);

        _mockNoteRepository.Setup(x => x.Get(1)).ReturnsAsync(note);
        _mockSearchService.Setup(x => x.SyncNoteToIndexAsync(note, "Test content"))
            .ThrowsAsync(new Exception("ManticoreSearch connection failed"));

        // Act
        var result = await _manticoreSearchSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Failed to CREATE note in ManticoreSearch"));
        Assert.That(result.ShouldRetry, Is.True);
    }

    [Test]
    public async Task ProcessAsync_InvalidAction_ThrowsException()
    {
        // Arrange
        var note = new Note { Id = 1, UserId = 123, Content = "Test content" };
        var payload = new ManticoreSearchSyncPayload
        {
            Action = "INVALID_ACTION",
            FullContent = "Test content"
        };
        var task = CreateTask("INVALID_ACTION", 1, 123, payload);

        _mockNoteRepository.Setup(x => x.Get(1)).ReturnsAsync(note);

        // Act
        var result = await _manticoreSearchSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("Unknown ManticoreSearch sync action"));
    }

    [Test]
    public void ServiceName_ReturnsCorrectValue()
    {
        // Assert
        Assert.That(_manticoreSearchSyncHandler.ServiceName, Is.EqualTo("manticoresearch"));
    }

    [Test]
    public void MaxRetryAttempts_ReturnsDefaultValue()
    {
        // Assert
        Assert.That(_manticoreSearchSyncHandler.MaxRetryAttempts, Is.EqualTo(3));
    }

    [Test]
    public void CalculateRetryDelay_UsesDefaultExponentialBackoff()
    {
        // Act
        var delay1 = _manticoreSearchSyncHandler.CalculateRetryDelay(1);
        var delay2 = _manticoreSearchSyncHandler.CalculateRetryDelay(2);
        var delay3 = _manticoreSearchSyncHandler.CalculateRetryDelay(3);

        // Assert
        Assert.That(delay1, Is.EqualTo(TimeSpan.FromMinutes(2))); // 2^1
        Assert.That(delay2, Is.EqualTo(TimeSpan.FromMinutes(4))); // 2^2
        Assert.That(delay3, Is.EqualTo(TimeSpan.FromMinutes(8))); // 2^3
    }

    [Test]
    public async Task ProcessAsync_PurgeAction_CallsPurgeUserDeletedNotesFromIndexAsync()
    {
        // Arrange
        var payload = new ManticoreSearchSyncPayload
        {
            Action = "PURGE",
            FullContent = string.Empty
        };
        var task = CreateTask("PURGE", 0, 123, payload);

        _mockSearchService.Setup(x => x.PurgeUserDeletedNotesFromIndexAsync(123)).Returns(Task.CompletedTask);

        // Act
        var result = await _manticoreSearchSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        _mockSearchService.Verify(x => x.PurgeUserDeletedNotesFromIndexAsync(123), Times.Once);
        _mockNoteRepository.Verify(x => x.Get(It.IsAny<long>()), Times.Never);
    }

    [Test]
    public async Task ProcessAsync_PurgeAction_SearchServiceThrows_ReturnsRetryableFailure()
    {
        // Arrange
        var payload = new ManticoreSearchSyncPayload
        {
            Action = "PURGE",
            FullContent = string.Empty
        };
        var task = CreateTask("PURGE", 0, 123, payload);

        _mockSearchService.Setup(x => x.PurgeUserDeletedNotesFromIndexAsync(123))
            .ThrowsAsync(new Exception("ManticoreSearch connection failed"));

        // Act
        var result = await _manticoreSearchSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ShouldRetry, Is.True);
    }

    [Test]
    public async Task ProcessAsync_JsonElementPayload_HandlesCorrectly()
    {
        // Arrange
        var note = new Note { Id = 1, UserId = 123, Content = "Test content" };
        var payload = new ManticoreSearchSyncPayload
        {
            Action = "CREATE",
            FullContent = "Test content"
        };

        // Create a task with JsonElement payload (simulating Redis deserialization)
        var jsonPayload = JsonSerializer.SerializeToElement(payload);
        var task = new SyncTask
        {
            EntityId = 1,
            UserId = 123,
            Service = "manticoresearch",
            Action = "CREATE",
            Payload = jsonPayload
        };

        _mockNoteRepository.Setup(x => x.Get(1)).ReturnsAsync(note);
        _mockSearchService.Setup(x => x.SyncNoteToIndexAsync(note, "Test content")).Returns(Task.CompletedTask);

        // Act
        var result = await _manticoreSearchSyncHandler.ProcessAsync(task, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        _mockSearchService.Verify(x => x.SyncNoteToIndexAsync(note, "Test content"), Times.Once);
    }
}
