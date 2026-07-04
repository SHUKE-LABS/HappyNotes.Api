# HappyNotes.Api

[中文](./README.cn.md)

Backend service for the HappyNotes project, built with ASP.NET Core on .NET 10.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.301 or later patch)
- MySQL / MariaDB database
- Redis (required for Telegram and Mastodon sync queues)

## Quick Start

```bash
# 1. Clone
git clone <repo-url>
cd HappyNotes.Api

# 2. Restore dependencies
dotnet restore

# 3. Configure
cp src/HappyNotes.Api/appsettings.json src/HappyNotes.Api/appsettings.Development.json
# Edit appsettings.Development.json: set ConnectionStrings, Redis, JWT settings

# 4. Run
dotnet run --project src/HappyNotes.Api
```

API is available at `https://localhost:5001`. Swagger UI at `/swagger/index.html`.

## Architecture

- **ASP.NET Core Web API** — RESTful endpoints with JWT authentication
- **Redis sync queue** — reliable background sync to external services (Telegram, Mastodon) with exponential-backoff retry (1 min → 2 min → 4 min → 8 min)
- **Telegram integration** — posts notes to configured Telegram channels via queue
- **Mastodon integration** — syncs notes to Mastodon instances via queue
- **ManticoreSearch** — full-text search (direct sync, not yet queued)
- **`/health` endpoint** — liveness check used by production monitoring

## Build & Test

```bash
dotnet build
dotnet test --filter "TestCategory!=Integration"   # unit tests only
dotnet test                                         # all tests (requires Redis)
```

Set `REDIS_CONNECTION_STRING` (default: `localhost:6379`) before running integration tests.

## License

MIT — see [LICENSE](./LICENSE).
