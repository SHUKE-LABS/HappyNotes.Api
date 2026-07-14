# HappyNotes.Api Development Guide

## Runtime
- **Target framework**: `net10.0`
- **SDK**: 10.0.301 (pinned by `global.json`, `rollForward: latestPatch`)

## Build & Test Commands
- Build solution: `dotnet build`
- Run unit tests only: `dotnet test --filter "TestCategory!=Integration"`
- Run all tests (including integration): `dotnet test`
- Run integration tests only: `dotnet test --filter "TestCategory=Integration"`
- Run specific test project: `dotnet test tests/HappyNotes.Services.Tests`
- Run single test class: `dotnet test --filter "FullyQualifiedName~NoteServiceTests"`
- Run single test method: `dotnet test --filter "FullyQualifiedName~NoteServiceTests.Get_WithExistingPublicNote_ReturnsNote"`

### ⚠️ dotnet test Command Format Reminder
**CORRECT**: `dotnet test tests/ProjectName` or `dotnet test --filter "Pattern"`
**WRONG**: `dotnet test tests/ProjectName/SpecificFile.cs` (❌ Cannot test individual .cs files)

**Common Patterns**:
- Single test: `dotnet test --filter "FullyQualifiedName~TestMethodName"`
- Test class: `dotnet test --filter "FullyQualifiedName~TestClassName"`
- Multiple runs: Use `for` loops with project-level commands, not file-level

### Integration Tests Setup
- **Redis integration tests** require a Redis instance for sync queue functionality
- Set `REDIS_CONNECTION_STRING` environment variable (defaults to `localhost:6379`)
- Tests are automatically skipped if Redis is unavailable
- Local Docker: `docker run --rm -p 6379:6379 redis:7-alpine`
- **Queue Tests**: AtomicDequeueTests, DelayedTaskProcessingTests, RedisSyncQueueServiceTests
- **Handler Tests**: TelegramSyncHandlerTests, MastodonSyncHandlerTests (when implemented)

### GitHub Actions CI
- **Unit tests**: Run on every push/PR (fast feedback)
- **Integration tests**: Run with Redis service container
- **Parallel execution**: Unit and integration tests run simultaneously
- **PR checks**: Quick unit test feedback for all PRs
- **Label-triggered**: Add `needs-integration-tests` label to run integration tests on PRs

## Code Style Guidelines
- **Naming**: PascalCase for classes/methods, camelCase for variables/parameters
- **Types**: Use explicit types; enable nullable reference types
- **Regex**: Use GeneratedRegex attributes with partial methods for better performance
- **Error Handling**: Use CustomException with EventId for domain errors; ArgumentException for invalid inputs
- **Testing**: Use NUnit with Moq framework; follow AAA pattern (Arrange-Act-Assert)
- **Architecture**: Follow repository pattern with services for business logic
- **Dependencies**: Use constructor injection with interfaces for testability

## Project Structure
- Api.Framework: Base classes and utilities
- HappyNotes.Common: Shared constants and extensions
- HappyNotes.Services: Business logic implementation
  - **SyncQueue**: Redis-based queue system for reliable sync operations
- HappyNotes.Entities: Database model classes
- HappyNotes.Repositories: Data access layer
- HappyNotes.Dto: Data transfer objects
- HappyNotes.Models: Request/response models

## Redis Sync Queue Architecture

### Overview
The application uses a **Redis-based queue system** for reliable background sync operations to external services (Telegram, Mastodon). This ensures resilience, retry capabilities, and horizontal scalability.

### Key Components
- **SyncQueueService**: Redis queue management (enqueue, dequeue, retry)
- **SyncQueueProcessor**: Background service that processes queued tasks
- **Sync Handlers**: Service-specific handlers (TelegramSyncHandler, MastodonSyncHandler)
- **Multi-layer Retry Strategy**: 3-tier retry system with exponential backoff

### Supported Services
- ✅ **Telegram**: Full queue integration with channel management
- ✅ **Mastodon**: Complete queue integration with instance management
- ✅ **Fanfou**: Queue integration via OAuth 1.0a; per-user sync rule (all / public-only / `#fanfou` tag)
- ⏳ **ManticoreSearch**: Not yet migrated (still uses direct sync)

### Configuration
- **Redis Connection**: Set `REDIS_CONNECTION_STRING` environment variable
- **Queue Options**: Configure retry attempts, delays, and timeouts in appsettings.json
- **Handler Registration**: All handlers auto-registered via DI container

### Resilience Features
- **Exponential Backoff**: 1min → 2min → 4min → 8min retry delays
- **Task Recovery**: Automatic recovery of expired/failed tasks
- **Service Isolation**: Per-service queues prevent cross-contamination
- **Graceful Degradation**: Queue failures don't crash the main application
