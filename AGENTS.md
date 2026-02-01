# AGENTS.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

GankedTV is a social media platform for sharing gaming clips. It uses a monorepo structure with separate `server/` (.NET) and `web/` (Vue) directories.

## Tech Stack

- **Server:** C# / .NET 10, Entity Framework Core (PostgreSQL), AWS S3 SDK
- **Web:** Vue 3, TypeScript, Vite, Vitest, Bun
- **Infrastructure:** PostgreSQL, MinIO (S3-compatible storage)

## Development Commands

### Infrastructure
```bash
make up                       # Start PostgreSQL and MinIO
make down                     # Stop infrastructure
make clean                    # Stop and remove volumes
make logs                     # View infrastructure logs
```

### Server (from repository root)
```bash
dotnet build server           # Build
dotnet test server            # Run all tests
dotnet test server --filter "FullyQualifiedName~TestClassName"  # Run specific tests
dotnet watch --project server/src/GankedTV.Api  # Run with hot reload
```

### Web (from repository root)
```bash
cd web && bun install         # Install dependencies
cd web && bun dev             # Dev server (http://localhost:5173)
cd web && bun run build       # Production build
cd web && bun run lint        # Lint (oxlint + eslint with auto-fix)
cd web && bun run type-check  # TypeScript check
cd web && bun run test:unit   # Run tests (Vitest)
cd web && bun run test:unit -- --filter="test name"  # Run specific test
```

## Architecture

```
gankedtv/
├── server/                   # .NET 10 backend
│   ├── src/GankedTV.Api/     # Main API project
│   └── tests/GankedTV.Api.Tests/  # xUnit tests with FluentAssertions
├── web/                      # Vue 3 frontend (Bun + Vite)
│   └── src/
├── docker-compose.dev.yml    # PostgreSQL + MinIO for local dev
└── Makefile                  # Development commands
```

## Local Services

| Service    | URL                    | Credentials              |
|------------|------------------------|--------------------------|
| API        | http://localhost:5000  | -                        |
| Web        | http://localhost:5173  | -                        |
| PostgreSQL | localhost:5435         | gankedtv / gankedtv_dev  |
| MinIO API  | http://localhost:9000  | minioadmin / minioadmin  |
| MinIO UI   | http://localhost:9001  | minioadmin / minioadmin  |
