using HappyNotes.Services.SyncQueue.Configuration;
using HappyNotes.Services.SyncQueue.Models;
using HappyNotes.Services.SyncQueue.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using StackExchange.Redis;

namespace HappyNotes.Services.Tests.SyncQueue;

[TestFixture]
[Category("Integration")]
public class RedisSyncQueueServiceTests
{
    private RedisSyncQueueService _queueService = null!;
    private IConnectionMultiplexer _redis = null!;
    private IDatabase _database = null!;
    private string _redisConnectionString = null!;

    [SetUp]
    public void Setup()
    {
        // Get Redis connection string from environment variable
        _redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING")
                               ?? Environment.GetEnvironmentVariable("TEST_REDIS_CONNECTION_STRING")
                               ?? "localhost:6379";

        // Skip tests if Redis is not available or if we're in CI without proper Redis setup
        try
        {
            _redis = ConnectionMultiplexer.Connect(_redisConnectionString);
            _database = _redis.GetDatabase(15); // Use database 15 for tests

            var options = Options.Create(new SyncQueueOptions
            {
                Redis = new RedisOptions
                {
                    ConnectionString = _redisConnectionString,
                    Database = 15, // Use separate database for tests
                    KeyPrefix = "test:sync:"
                }
            });

            var logger = new LoggerFactory().CreateLogger<RedisSyncQueueService>();
            _queueService = new RedisSyncQueueService(_redis, options, logger, TimeProvider.System);
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Redis is not available for testing: {ex.Message}. Set REDIS_CONNECTION_STRING environment variable to run integration tests.");
        }
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (_redis != null && _redis.IsConnected)
            {
                // Clean up test data using dynamic server endpoint
                var endpoint = _redis.GetEndPoints().FirstOrDefault();
                if (endpoint != null)
                {
                    var server = _redis.GetServer(endpoint);
                    var keys = server.Keys(database: 15, pattern: "test:sync:*");
                    if (keys.Any())
                    {
                        _database.KeyDelete(keys.ToArray());
                    }
                }
            }

            _redis?.Dispose();
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Test]
    public async Task EnqueueAsync_ShouldAddTaskToQueue()
    {
        // Arrange
        var task = SyncTask.Create("telegram", "CREATE", 123, 456, new { content = "test" });

        // Act
        await _queueService.EnqueueAsync("telegram", task);

        // Assert
        var stats = await _queueService.GetStatsAsync("telegram");
        Assert.That(stats.PendingCount, Is.EqualTo(1));
    }

    [Test]
    public async Task DequeueAsync_ShouldRetrieveTaskFromQueue()
    {
        // Arrange
        var originalTask = SyncTask.Create("telegram", "CREATE", 123, 456, new { content = "test" });
        await _queueService.EnqueueAsync("telegram", originalTask);

        // Act
        var dequeuedTask = await _queueService.DequeueAsync<object>("telegram", CancellationToken.None);

        // Assert
        Assert.That(dequeuedTask, Is.Not.Null);
        Assert.That(dequeuedTask.Id, Is.EqualTo(originalTask.Id));
        Assert.That(dequeuedTask.Action, Is.EqualTo("CREATE"));
        Assert.That(dequeuedTask.EntityId, Is.EqualTo(123));
    }

    [Test]
    public async Task GetStatsAsync_ShouldReturnCorrectCounts()
    {
        // Arrange
        var task1 = SyncTask.Create("telegram", "CREATE", 123, 456, new { content = "test1" });
        var task2 = SyncTask.Create("telegram", "UPDATE", 124, 457, new { content = "test2" });

        // Act
        await _queueService.EnqueueAsync("telegram", task1);
        await _queueService.EnqueueAsync("telegram", task2);

        // Assert
        var stats = await _queueService.GetStatsAsync("telegram");
        Assert.That(stats.PendingCount, Is.EqualTo(2));
        Assert.That(stats.Service, Is.EqualTo("telegram"));
    }

    [Test]
    public async Task ScheduleRetryAsync_ShouldAddToDelayedQueue()
    {
        // Arrange
        var task = SyncTask.Create("telegram", "CREATE", 123, 456, new { content = "test" });
        task.AttemptCount = 1;

        // Act
        await _queueService.ScheduleRetryAsync("telegram", task, TimeSpan.FromMinutes(1));

        // Assert
        var stats = await _queueService.GetStatsAsync("telegram");
        Assert.That(stats.DelayedCount, Is.EqualTo(1));
        Assert.That(task.AttemptCount, Is.EqualTo(2));
    }

    [Test]
    public async Task MoveToFailedAsync_ShouldAddToFailedQueue()
    {
        // Arrange
        var task = SyncTask.Create("telegram", "CREATE", 123, 456, new { content = "test" });

        // Act
        await _queueService.MoveToFailedAsync("telegram", task, "Test error");

        // Assert
        var stats = await _queueService.GetStatsAsync("telegram");
        Assert.That(stats.FailedCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ClearQueueAsync_ShouldClearAllQueues()
    {
        // Arrange
        var task = SyncTask.Create("telegram", "CREATE", 123, 456, new { content = "test" });
        await _queueService.EnqueueAsync("telegram", task);
        await _queueService.MoveToFailedAsync("telegram", task, "Test error");

        // Act
        await _queueService.ClearQueueAsync("telegram");

        // Assert
        var stats = await _queueService.GetStatsAsync("telegram");
        Assert.That(stats.PendingCount, Is.EqualTo(0));
        Assert.That(stats.FailedCount, Is.EqualTo(0));
    }

    [Test]
    public async Task RetryFailedTasksAsync_ResetsAttemptCount_SoSubsequentFailureIsRescheduledNotBlacklisted()
    {
        // Arrange: task exhausted retries, sitting in failed queue
        var task = SyncTask.Create("telegram", "CREATE", 123, 456, new { content = "test" });
        task.AttemptCount = 3; // simulates MaxRetryAttempts
        await _queueService.MoveToFailedAsync("telegram", task, "transient error");

        var statsBefore = await _queueService.GetStatsAsync("telegram");
        Assert.That(statsBefore.FailedCount, Is.EqualTo(1));

        // Act
        await _queueService.RetryFailedTasksAsync("telegram");

        // Assert: task is back in the ready queue with AttemptCount reset
        var statsAfter = await _queueService.GetStatsAsync("telegram");
        Assert.That(statsAfter.FailedCount, Is.EqualTo(0));
        Assert.That(statsAfter.PendingCount, Is.EqualTo(1));

        var dequeued = await _queueService.DequeueAsync<object>("telegram", CancellationToken.None);
        Assert.That(dequeued, Is.Not.Null);
        Assert.That(dequeued!.AttemptCount, Is.EqualTo(0),
            "AttemptCount must be reset so the task receives a full retry budget");
        Assert.That(dequeued.Metadata.ContainsKey("error"), Is.False,
            "error metadata must be cleared on retry");
        Assert.That(dequeued.Metadata.ContainsKey("failedAt"), Is.False,
            "failedAt metadata must be cleared on retry");
    }
}
