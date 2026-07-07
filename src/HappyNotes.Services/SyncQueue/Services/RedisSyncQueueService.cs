using System.Text.Json;
using HappyNotes.Common;
using HappyNotes.Services.SyncQueue.Configuration;
using HappyNotes.Services.SyncQueue.Interfaces;
using HappyNotes.Services.SyncQueue.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace HappyNotes.Services.SyncQueue.Services;

public class RedisSyncQueueService : ISyncQueueService
{
    private readonly IDatabase _database;
    private readonly SyncQueueOptions _options;
    private readonly ILogger<RedisSyncQueueService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, DateTime> _lastDelayedProcessTime = new();

    // Lua script for atomic RPOP + ZADD operation
    private static readonly string AtomicDequeueScript = @"
        local queueKey = KEYS[1]
        local processingKey = KEYS[2]
        local leaseExpiry = ARGV[1]
        
        local json = redis.call('LPOP', queueKey)
        if json then
            redis.call('ZADD', processingKey, leaseExpiry, json)
        end
        return json
    ";

    // Lua script for atomic delayed tasks processing with batch limit
    private static readonly string ProcessDelayedTasksScript = @"
        local delayedKey = KEYS[1]
        local queueKey = KEYS[2]
        local now = ARGV[1]
        local batchLimit = tonumber(ARGV[2]) or 100
        
        -- Get ready tasks with limit
        local readyTasks = redis.call('ZRANGEBYSCORE', delayedKey, 0, now, 'LIMIT', 0, batchLimit)
        
        if #readyTasks > 0 then
            -- Move tasks atomically
            for i = 1, #readyTasks do
                redis.call('LPUSH', queueKey, readyTasks[i])
                redis.call('ZREM', delayedKey, readyTasks[i])
            end
        end
        
        return #readyTasks
    ";

    public RedisSyncQueueService(
        IConnectionMultiplexer redis,
        IOptions<SyncQueueOptions> options,
        ILogger<RedisSyncQueueService> logger,
        TimeProvider timeProvider)
    {
        _database = redis.GetDatabase(options.Value.Redis.Database);
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task EnqueueAsync<T>(string service, SyncTask<T> task)
    {
        var key = GetQueueKey(service, "queue");
        var json = JsonSerializer.Serialize(task, JsonSerializerConfig.Default);

        await _database.ListRightPushAsync(key, json);

        _logger.LogDebug("Enqueued task {TaskId} for service {Service}", task.Id, service);
    }

    public async Task<SyncTask<T>?> DequeueAsync<T>(string service, CancellationToken cancellationToken)
    {
        var queueKey = GetQueueKey(service, "queue");
        var processingKey = GetQueueKey(service, "processing");

        // First check for delayed tasks that are ready (with rate limiting)
        await ProcessDelayedTasksWithRateLimit(service);

        // Calculate lease expiry timestamp
        var leaseExpiry = _timeProvider.GetUtcNow().Add(_options.Processing.VisibilityTimeout).ToUnixTimeSeconds();

        // Atomically RPOP from queue and ZADD to processing using Lua script
        var result = await _database.ScriptEvaluateAsync(
            AtomicDequeueScript,
            new RedisKey[] { queueKey, processingKey },
            new RedisValue[] { leaseExpiry }
        );

        if (result.IsNull)
            return null;

        var json = (string)result!;

        try
        {
            var task = JsonSerializer.Deserialize<SyncTask<T>>(json, JsonSerializerConfig.Default);
            if (task != null)
            {
                // Store original JSON for recovery purposes
                task.Metadata["_originalJson"] = json;
            }
            return task;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize task from queue {Service}, JSON: {Json}", service, json);

            // Remove corrupted task from processing queue
            await _database.SortedSetRemoveAsync(processingKey, json);
            return null;
        }
    }

    public async Task ScheduleRetryAsync<T>(string service, SyncTask<T> task, TimeSpan delay)
    {
        task.AttemptCount++;
        task.ScheduledFor = _timeProvider.GetUtcNow().DateTime.Add(delay);

        var key = GetQueueKey(service, "delayed");
        var json = JsonSerializer.Serialize(task, JsonSerializerConfig.Default);
        var score = ((DateTimeOffset)task.ScheduledFor.Value).ToUnixTimeSeconds();

        await _database.SortedSetAddAsync(key, json, score);

        _logger.LogDebug("Scheduled retry for task {TaskId} in {Delay}", task.Id, delay);
    }

    public async Task MoveToFailedAsync<T>(string service, SyncTask<T> task, string error)
    {
        task.Metadata["error"] = error;
        task.Metadata["failedAt"] = _timeProvider.GetUtcNow().DateTime;

        var failedKey = GetQueueKey(service, "failed");
        var json = JsonSerializer.Serialize(task, JsonSerializerConfig.Default);

        await _database.ListLeftPushAsync(failedKey, json);

        // Remove from processing without updating success stats
        await RemoveFromProcessingAsync(service, task, updateSuccessStats: false);

        // Update failure stats
        var statsKey = GetQueueKey(service, "stats");
        try
        {
            await _database.HashIncrementAsync(statsKey, "totalFailed");
            await _database.HashSetAsync(statsKey, "lastFailedAt", _timeProvider.GetUtcNow().DateTime.ToString("O"));
        }
        catch (StackExchange.Redis.RedisServerException ex) when (ex.Message.Contains("WRONGTYPE"))
        {
            _logger.LogWarning("Stats key {StatsKey} has wrong type, recreating as hash", statsKey);

            // Delete corrupted key and recreate as hash
            await _database.KeyDeleteAsync(statsKey);
            await _database.HashIncrementAsync(statsKey, "totalFailed");
            await _database.HashSetAsync(statsKey, "lastFailedAt", _timeProvider.GetUtcNow().DateTime.ToString("O"));

            _logger.LogInformation("Successfully recreated stats key {StatsKey} as hash", statsKey);
        }

        _logger.LogWarning("Moved task {TaskId} to failed queue: {Error}", task.Id, error);
    }

    public async Task<QueueStats> GetStatsAsync(string service)
    {
        var queueKey = GetQueueKey(service, "queue");
        var delayedKey = GetQueueKey(service, "delayed");
        var failedKey = GetQueueKey(service, "failed");
        var processingKey = GetQueueKey(service, "processing");
        var statsKey = GetQueueKey(service, "stats");

        var queueLengthTask = _database.ListLengthAsync(queueKey);
        var delayedLengthTask = _database.SortedSetLengthAsync(delayedKey);
        var failedLengthTask = _database.ListLengthAsync(failedKey);
        var processingLengthTask = _database.SortedSetLengthAsync(processingKey);
        var statsTask = _database.HashGetAllAsync(statsKey);

        await Task.WhenAll(queueLengthTask, delayedLengthTask, failedLengthTask, processingLengthTask, statsTask);

        var stats = statsTask.Result.ToDictionary();

        return new QueueStats
        {
            Service = service,
            PendingCount = queueLengthTask.Result,
            DelayedCount = delayedLengthTask.Result,
            FailedCount = failedLengthTask.Result,
            ProcessingCount = processingLengthTask.Result,
            LastProcessedAt = stats.TryGetValue("lastProcessedAt", out var lastProcessed)
                ? DateTime.Parse(lastProcessed!)
                : DateTime.MinValue,
            LastFailedAt = stats.TryGetValue("lastFailedAt", out var lastFailed)
                ? DateTime.Parse(lastFailed!)
                : DateTime.MinValue,
            TotalProcessed = stats.TryGetValue("totalProcessed", out var totalProcessed)
                ? long.Parse(totalProcessed!)
                : 0,
            TotalFailed = stats.TryGetValue("totalFailed", out var totalFailed)
                ? long.Parse(totalFailed!)
                : 0
        };
    }

    public async Task RetryFailedTasksAsync(string service)
    {
        var failedKey = GetQueueKey(service, "failed");
        var queueKey = GetQueueKey(service, "queue");

        var rawTasks = await _database.ListRangeAsync(failedKey);
        if (rawTasks.Length == 0)
        {
            _logger.LogInformation("No failed tasks to retry for service {Service}", service);
            return;
        }

        await _database.KeyDeleteAsync(failedKey);

        var count = 0;
        foreach (var raw in rawTasks)
        {
            try
            {
                var task = JsonSerializer.Deserialize<SyncTask<object>>((string)raw!, JsonSerializerConfig.Default);
                if (task == null) continue;

                task.AttemptCount = 0;
                task.Metadata.Remove("error");
                task.Metadata.Remove("failedAt");

                var json = JsonSerializer.Serialize(task, JsonSerializerConfig.Default);
                await _database.ListLeftPushAsync(queueKey, json);
                count++;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Skipping undeserializable failed task for service {Service}", service);
            }
        }

        _logger.LogInformation("Retried {Count} failed tasks for service {Service}", count, service);
    }

    public async Task ClearQueueAsync(string service)
    {
        var keys = new[]
        {
            GetQueueKey(service, "queue"),
            GetQueueKey(service, "delayed"),
            GetQueueKey(service, "failed"),
            GetQueueKey(service, "processing")
        };

        await _database.KeyDeleteAsync(keys.Select(k => (RedisKey)k).ToArray());

        _logger.LogInformation("Cleared all queues for service {Service}", service);
    }

    public async Task MarkAsProcessingAsync<T>(string service, SyncTask<T> task)
    {
        // This method is now only used for manual task processing
        // Normal dequeue operations use AddToProcessingWithLeaseAsync
        var json = JsonSerializer.Serialize(task, JsonSerializerConfig.Default);
        await AddToProcessingWithLeaseAsync(service, task, json);
    }

    private async Task AddToProcessingWithLeaseAsync<T>(string service, SyncTask<T> task, string json)
    {
        var key = GetQueueKey(service, "processing");
        var leaseExpiry = _timeProvider.GetUtcNow().Add(_options.Processing.VisibilityTimeout).ToUnixTimeSeconds();

        // Add to processing sorted set with lease expiry as score
        await _database.SortedSetAddAsync(key, json, leaseExpiry);

        _logger.LogDebug("Added task {TaskId} to processing with lease expiry {LeaseExpiry}",
            task.Id, DateTimeOffset.FromUnixTimeSeconds(leaseExpiry));
    }

    public async Task RemoveFromProcessingAsync<T>(string service, SyncTask<T> task)
    {
        await RemoveFromProcessingAsync(service, task, updateSuccessStats: false);
    }

    private async Task RemoveFromProcessingAsync<T>(string service, SyncTask<T> task, bool updateSuccessStats)
    {
        var key = GetQueueKey(service, "processing");

        // Use original JSON if available to avoid serialization inconsistencies
        var json = task.Metadata.TryGetValue("_originalJson", out var originalJson)
            ? originalJson.ToString()
            : JsonSerializer.Serialize(task, JsonSerializerConfig.Default);

        // Remove from processing sorted set by value
        await _database.SortedSetRemoveAsync(key, json!);

        // Only update success stats if explicitly requested (successful completion)
        if (updateSuccessStats)
        {
            var statsKey = GetQueueKey(service, "stats");
            try
            {
                await _database.HashIncrementAsync(statsKey, "totalProcessed");
                await _database.HashSetAsync(statsKey, "lastProcessedAt", _timeProvider.GetUtcNow().DateTime.ToString("O"));
            }
            catch (StackExchange.Redis.RedisServerException ex) when (ex.Message.Contains("WRONGTYPE"))
            {
                _logger.LogWarning("Stats key {StatsKey} has wrong type, recreating as hash", statsKey);

                // Delete corrupted key and recreate as hash
                await _database.KeyDeleteAsync(statsKey);
                await _database.HashIncrementAsync(statsKey, "totalProcessed");
                await _database.HashSetAsync(statsKey, "lastProcessedAt", _timeProvider.GetUtcNow().DateTime.ToString("O"));

                _logger.LogInformation("Successfully recreated stats key {StatsKey} as hash", statsKey);
            }
        }
    }

    public async Task RemoveFromProcessingAsyncOnSuccess<T>(string service, SyncTask<T> task)
    {
        await RemoveFromProcessingAsync(service, task, updateSuccessStats: true);
    }

    private async Task ProcessDelayedTasksWithRateLimit(string service)
    {
        var now = _timeProvider.GetUtcNow().DateTime;
        var minInterval = TimeSpan.FromSeconds(30); // Rate limit: max once per 30 seconds per service

        lock (_lastDelayedProcessTime)
        {
            if (_lastDelayedProcessTime.TryGetValue(service, out var lastTime))
            {
                if (now - lastTime < minInterval)
                {
                    return; // Skip processing due to rate limit
                }
            }
            _lastDelayedProcessTime[service] = now;
        }

        await ProcessDelayedTasks(service);
    }

    private async Task ProcessDelayedTasks(string service)
    {
        var delayedKey = GetQueueKey(service, "delayed");
        var queueKey = GetQueueKey(service, "queue");
        var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var batchLimit = 100; // Limit batch size to prevent overwhelming Redis

        int movedCount;
        try
        {
            // Use Lua script for atomic batch processing with limit
            movedCount = (int)await _database.ScriptEvaluateAsync(
                ProcessDelayedTasksScript,
                new RedisKey[] { delayedKey, queueKey },
                new RedisValue[] { now, batchLimit }
            );
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex, "Redis timeout during delayed tasks processing for service {Service} - skipping this cycle", service);
            return; // Skip this cycle, will retry on next dequeue attempt
        }
        catch (StackExchange.Redis.RedisServerException ex) when (ex.Message.Contains("WRONGTYPE"))
        {
            _logger.LogWarning("Delayed queue {DelayedKey} has wrong type, recreating as sorted set", delayedKey);

            // Delete corrupted delayed queue and recreate as sorted set
            await _database.KeyDeleteAsync(delayedKey);

            _logger.LogInformation("Successfully recreated delayed queue {DelayedKey} as sorted set", delayedKey);
            return; // No tasks to process after recreation
        }

        if (movedCount > 0)
        {
            _logger.LogDebug("Moved {Count} delayed tasks to queue for service {Service}", movedCount, service);

            // If we moved the maximum batch, there might be more ready tasks
            // Let rate limiter handle the next batch in subsequent calls
            if (movedCount == batchLimit)
            {
                _logger.LogDebug("Batch limit reached, more delayed tasks may be ready for service {Service}", service);
            }
        }
    }

    public async Task RecoverExpiredTasksAsync(string service)
    {
        try
        {
            var processingKey = GetQueueKey(service, "processing");
            var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();

            // Get expired tasks (lease expiry < now) with timeout protection
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var expiredTasks = await _database.SortedSetRangeByScoreAsync(processingKey, 0, now);

            if (expiredTasks.Length == 0)
                return;

            _logger.LogWarning("Found {Count} expired tasks in processing queue for service {Service}",
                expiredTasks.Length, service);

            foreach (var taskJson in expiredTasks)
            {
                try
                {
                    var task = JsonSerializer.Deserialize<SyncTask<object>>((string)taskJson!, JsonSerializerConfig.Default);
                    if (task == null) continue;

                    // Remove from processing queue first
                    await _database.SortedSetRemoveAsync(processingKey, taskJson);

                    // Check if we should retry or move to failed
                    var handlerOptions = GetHandlerOptions(service);

                    if (task.AttemptCount >= handlerOptions.MaxRetries)
                    {
                        await MoveToFailedAsync(service, task, "Task expired in processing queue");
                        _logger.LogWarning("Moved expired task {TaskId} to failed queue (max retries exceeded)", task.Id);
                    }
                    else
                    {
                        // Calculate retry delay
                        var retryDelay = CalculateRetryDelay(handlerOptions, task.AttemptCount);
                        await ScheduleRetryAsync(service, task, retryDelay);
                        _logger.LogInformation("Rescheduled expired task {TaskId} for retry in {Delay}", task.Id, retryDelay);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Failed to deserialize expired task from processing queue: {TaskJson}", taskJson);
                    // Remove invalid JSON from processing queue
                    await _database.SortedSetRemoveAsync(processingKey, taskJson);
                }
            }

            _logger.LogInformation("Recovered {Count} expired tasks for service {Service}", expiredTasks.Length, service);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex, "Redis timeout during recovery for service {Service} - skipping this recovery cycle", service);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during recovery for service {Service}", service);
        }
    }

    private HandlerOptions GetHandlerOptions(string service)
    {
        return _options.Handlers.TryGetValue(service, out var options)
            ? options
            : new HandlerOptions(); // Default values
    }

    private TimeSpan CalculateRetryDelay(HandlerOptions options, int attemptCount)
    {
        var delay = TimeSpan.FromSeconds(options.BaseDelaySeconds * Math.Pow(options.BackoffMultiplier, attemptCount));
        var maxDelay = TimeSpan.FromMinutes(options.MaxDelayMinutes);
        return delay > maxDelay ? maxDelay : delay;
    }

    private string GetQueueKey(string service, string type)
    {
        return $"{_options.Redis.KeyPrefix}{service}:{type}";
    }
}
