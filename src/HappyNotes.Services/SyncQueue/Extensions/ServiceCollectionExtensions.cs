using HappyNotes.Services.SyncQueue.Configuration;
using HappyNotes.Services.SyncQueue.Handlers;
using HappyNotes.Services.SyncQueue.Interfaces;
using HappyNotes.Services.SyncQueue.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace HappyNotes.Services.SyncQueue.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSyncQueue(this IServiceCollection services, IConfiguration configuration)
    {
        // Configure options
        services.Configure<SyncQueueOptions>(configuration.GetSection(SyncQueueOptions.SectionName));

        // Get Redis configuration
        var syncQueueOptions = new SyncQueueOptions();
        configuration.GetSection(SyncQueueOptions.SectionName).Bind(syncQueueOptions);

        // Register Redis connection factory with resilient configuration
        services.AddSingleton<Lazy<IConnectionMultiplexer>>(provider =>
            new Lazy<IConnectionMultiplexer>(() =>
            {
                var connectionString = syncQueueOptions.Redis.ConnectionString;
                var options = ConfigurationOptions.Parse(connectionString);

                // Configure resilient connection options
                options.AbortOnConnectFail = false;      // Don't fail startup if Redis is unavailable
                options.ConnectRetry = 3;                // Retry connection 3 times
                options.ConnectTimeout = 15000;          // 15 second connection timeout for cross-continent
                options.SyncTimeout = 15000;             // 15 second operation timeout for cross-continent  
                options.AsyncTimeout = 15000;            // 15 second async timeout for cross-continent
                options.ReconnectRetryPolicy = new ExponentialRetry(1000); // Exponential backoff for reconnects
                options.ResolveDns = true;               // Enable DNS resolution

                var loggerFactory = provider.GetService<ILoggerFactory>();
                var logger = loggerFactory?.CreateLogger("RedisConnection");
                logger?.LogInformation("Connecting to Redis: {ConnectionString}", connectionString);

                var connection = ConnectionMultiplexer.Connect(options);

                // Log connection events for monitoring
                connection.ConnectionFailed += (sender, args) =>
                    logger?.LogWarning("Redis connection failed: {EndPoint} - {FailureType}", args.EndPoint, args.FailureType);

                connection.ConnectionRestored += (sender, args) =>
                    logger?.LogInformation("Redis connection restored: {EndPoint}", args.EndPoint);

                logger?.LogInformation("Successfully connected to Redis");
                return connection;
            }));

        // Register IConnectionMultiplexer that uses the lazy factory
        services.AddSingleton<IConnectionMultiplexer>(provider =>
            provider.GetRequiredService<Lazy<IConnectionMultiplexer>>().Value);

        // Register core services
        services.AddSingleton<ISyncQueueService, RedisSyncQueueService>();

        // Register handlers
        services.AddScoped<ISyncHandler, TelegramSyncHandler>();
        services.AddScoped<ISyncHandler, MastodonSyncHandler>();
        services.AddScoped<ISyncHandler, FanfouSyncHandler>();
        services.AddScoped<ISyncHandler, ManticoreSearchSyncHandler>();

        // Register background processor
        services.AddHostedService<SyncQueueProcessor>();

        return services;
    }
}
