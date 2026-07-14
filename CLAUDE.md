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
- ✅ **ManticoreSearch**: SyncQueue-integrated (`ManticoreSyncNoteService` enqueuer + `ManticoreSearchSyncHandler` consumer). Indexes keyword content **and**, when semantic search is enabled, the note's embedding vector — both written in one REPLACE per note.

### Configuration
- **Redis Connection**: Set `REDIS_CONNECTION_STRING` environment variable
- **Queue Options**: Configure retry attempts, delays, and timeouts in appsettings.json
- **Handler Registration**: All handlers auto-registered via DI container

### Resilience Features
- **Exponential Backoff**: 1min → 2min → 4min → 8min retry delays
- **Task Recovery**: Automatic recovery of expired/failed tasks
- **Service Isolation**: Per-service queues prevent cross-contamination
- **Graceful Degradation**: Queue failures don't crash the main application

## Search (keyword + semantic)

Note search runs over the Manticore `noteindex` table.

- **Keyword** (always on): Manticore full-text, bigram-tokenized for CJK. Built by `SearchService._BuildNoteSearchQuery` and paged directly by Manticore.
- **Semantic (vector)** (opt-in via `SemanticSearch:Enabled`): a `float_vector` KNN attribute (HNSW, cosine) on the **same** `noteindex`. Query-time flow: embed the query, run a Manticore KNN search (`GetSemanticNoteIdsAsync`), then merge into the keyword results — keyword hits first, then deduped semantic candidates by similarity — and page over the merged list. Transparent to clients: same `/notes/search` response shape.
- **Embeddings — self-hosted by default** (privacy): generated via a self-hosted endpoint (Ollama `bge-m3`, 1024-dim, cosine) by `EmbeddingService`. Note text never leaves our infrastructure and there is no per-token cost. Switching to a hosted embedding API is a config-only change (re-embed required). Vectors are generated in the SyncQueue handler (background), never in the HTTP request path.
- **Isolation**: the KNN query applies the **same** owner (`UserId`) + delete-state (`DeletedAt`) filters as the keyword path (`_BuildOwnerFilterClauses`), so vector search never leaks another user's or a soft-deleted note; a user's own private notes stay findable (parity with keyword search).
- **Graceful fallback**: if embeddings or KNN are unavailable, search degrades to keyword-only with no error.
- **Write-path note**: Manticore requires REPLACE (not UPDATE) to change a full-text field or a `float_vector`, so a single writer rewrites content + vector together to avoid clobbering the vector. Deletes/undeletes stay numeric `deletedat` updates.
- **Config**: `SemanticSearch` section in `appsettings.json` (`EmbeddingEndpoint`, `EmbeddingModel`, `Dimensions`, `Similarity`, `TopK`, `MaxDistance`, `KeywordMergeCap`, `EmbeddingTimeoutSeconds`, backfill options). The `noteindex` schema (incl. `Embedding float_vector`) lives in `docker/create_table.sql`; adding the column requires table recreation + reindex.
- **Backfill**: existing notes are embedded by the resumable, batched `NoteVectorBackfillService`, triggered off-peak via `POST /api/admin/vector-backfill/run` (Admin policy). Progress is cursor-persisted, so a stopped run resumes.
