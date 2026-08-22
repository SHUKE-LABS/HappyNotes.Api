using System.Collections.Concurrent;
using System.Text.Json;
using Api.Framework.Helper;
using HappyNotes.Common;
using HappyNotes.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HappyNotes.Services;

public static class DuplicateRequestChecker
{
    // Default threshold of 10 minutes (600,000 ms) if not specified in environment
    private static readonly int TimeThresholdInMilliseconds = GetTimeThresholdFromEnvironment();
    private static readonly int ExpirationDurationInSeconds = GetExpirationDurationFromEnvironment();
    private const int CleanupIntervalInSeconds = 2;

    public static readonly ConcurrentDictionary<long, (DateTimeOffset Timestamp, string Hash)> RecentRequests = new();
    private static TimeSpan _expirationDuration = TimeSpan.FromSeconds(ExpirationDurationInSeconds);
    private static TimeSpan _cleanupInterval = TimeSpan.FromSeconds(CleanupIntervalInSeconds);
    private static readonly object CleanupLock = new();
    private static ILogger _logger = NullLogger.Instance;

    private static readonly CancellationTokenSource CancellationTokenSource = new();
    public static int Length(long userId) => RecentRequests.Count(r => r.Key == userId);

    static DuplicateRequestChecker()
    {
        // Start the background cleanup task
        Task.Run(async () => await PeriodicCleanup(CancellationTokenSource.Token), CancellationTokenSource.Token);
    }

    public static bool IsDuplicate(long userId, PostNoteRequest request)
    {
        var now = DateTimeOffset.UtcNow;

        if (RecentRequests.TryGetValue(userId, out var lastRequest))
        {
            var contentHash = CommonHelper.CalculateMd5Hash(JsonSerializer.Serialize(request, JsonSerializerConfig.Default));
            if ((now - lastRequest.Timestamp).TotalMilliseconds < TimeThresholdInMilliseconds && lastRequest.Hash == contentHash)
            {
                return true;
            }
        }

        var newContentHash = CommonHelper.CalculateMd5Hash(JsonSerializer.Serialize(request, JsonSerializerConfig.Default));
        RecentRequests[userId] = (now, newContentHash);
        return false;
    }

    private static async Task PeriodicCleanup(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_cleanupInterval, cancellationToken);

                lock (CleanupLock)
                {
                    var now = DateTime.UtcNow;
                    var keysToRemove = RecentRequests
                        .Where(kv => (now - kv.Value.Timestamp).TotalMilliseconds > _expirationDuration.TotalMilliseconds)
                        .Select(kv => kv.Key)
                        .ToList();

                    foreach (var key in keysToRemove)
                    {
                        RecentRequests.TryRemove(key, out _);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // Task was cancelled, exit gracefully
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled duplicate request cleanup failed");
            }
        }
    }

    public static void ConfigureLogger(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger("HappyNotes.Services.DuplicateRequestChecker");
    }

    // Method to set the cleanup interval dynamically
    public static void SetCleanupInterval(TimeSpan interval)
    {
        lock (CleanupLock)
        {
            _cleanupInterval = interval;
        }
    }

    public static void SetExpirationDuration(TimeSpan interval)
    {
        lock (CleanupLock)
        {
            _expirationDuration = interval;
        }
    }

    // Method to stop the background task gracefully
    public static void StopBackgroundTask()
    {
        CancellationTokenSource.Cancel();
    }

    private static int GetTimeThresholdFromEnvironment()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("HAPPYNOTES_DUPLICATE_THRESHOLD_MS"), out int threshold))
        {
            return threshold;
        }
        return 600000; // Default to 10 minutes (600,000 ms)
    }

    private static int GetExpirationDurationFromEnvironment()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("HAPPYNOTES_EXPIRATION_DURATION_SEC"), out int duration))
        {
            return duration;
        }
        return 660; // Default to 11 minutes (slightly longer than threshold)
    }
}
