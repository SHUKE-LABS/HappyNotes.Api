# HappyNotes.Api

[English](./README.md)

HappyNotes 项目的后端服务，基于 .NET 10 的 ASP.NET Core 构建。

## 前置要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（10.0.301 或更高补丁版本）
- MySQL / MariaDB 数据库
- Redis（Telegram 和 Mastodon 同步队列必需）

## 快速开始

```bash
# 1. 克隆仓库
git clone <repo-url>
cd HappyNotes.Api

# 2. 还原依赖
dotnet restore

# 3. 配置
cp src/HappyNotes.Api/appsettings.json src/HappyNotes.Api/appsettings.Development.json
# 编辑 appsettings.Development.json：设置 ConnectionStrings、Redis、JWT 等配置

# 4. 运行
dotnet run --project src/HappyNotes.Api
```

API 地址：`https://localhost:5001`，Swagger 文档：`/swagger/index.html`。

## 架构概览

- **ASP.NET Core Web API** — 带 JWT 认证的 RESTful 接口
- **Redis 同步队列** — 可靠的后台同步机制，支持指数退避重试（1 分钟 → 2 分钟 → 4 分钟 → 8 分钟），用于推送到 Telegram 和 Mastodon
- **Telegram 集成** — 通过队列将笔记发布到指定 Telegram 频道
- **Mastodon 集成** — 通过队列将笔记同步到 Mastodon 实例
- **ManticoreSearch** — 全文搜索（直接同步，尚未接入队列）
- **`/health` 端点** — 生产环境监控用存活检查

## 构建与测试

```bash
dotnet build
dotnet test --filter "TestCategory!=Integration"   # 仅运行单元测试
dotnet test                                         # 运行所有测试（需要 Redis）
```

运行集成测试前需设置 `REDIS_CONNECTION_STRING` 环境变量（默认：`localhost:6379`）。

## 许可证

MIT — 详见 [LICENSE](./LICENSE)。
