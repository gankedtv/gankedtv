# CLAUDE.md

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

### Host requirements

The dev workflow runs `dotnet watch` on the host, which means the API process — including the thumbnail-generation worker (issue #57) — runs on your host directly. Install these locally:

- **`dotnet`** (SDK matching `<TargetFramework>` in [server/src/GankedTV.Api/GankedTV.Api.csproj](server/src/GankedTV.Api/GankedTV.Api.csproj)).
- **`ffmpeg`** (provides `ffmpeg` and `ffprobe`) — required for the thumbnail worker. Without it, `POST /clips/{id}/complete` enqueues the clip but the worker fails on every attempt and the clip eventually lands in `status='failed'`.
  - Arch/CachyOS: `sudo pacman -S ffmpeg`
  - Debian/Ubuntu: `sudo apt install ffmpeg`
  - macOS: `brew install ffmpeg`
  - Override the binary location with `FFMPEG_PATH` / `FFPROBE_PATH` env vars when not on `$PATH`.
- **`bun`** for the web app.

[server/Dockerfile.api](server/Dockerfile.api) ships an API image with ffmpeg pre-installed — it's there for production / CI parity builds, not used by the dev compose stack.

### Parallel worktrees

For working on multiple issues in parallel, `./scripts/new-worktree.sh <issue>` (also exposed as `/worktree <n>`) creates `../gankedtv-issue-<n>/` with its own dev stack on offset ports — deterministic per-issue, derived from a SHA1 of the issue number. The script writes a gitignored `.env.worktree.local` (auto-loaded by the Makefile and passed through to `docker compose` via `--env-file`) and then runs the equivalent of `make setup` minus the destructive clean. Tear down with `./scripts/remove-worktree.sh <issue>` (or `/worktree-remove <n>`). The main checkout with no `.env.worktree.local` is unchanged — defaults still resolve to 5435 / 9000 / 9001 / 5050 / 5173.

### Git hooks
```bash
make hooks                    # One-time: install pre-push hook via core.hooksPath
```
Hook lives at `.githooks/pre-push` and mirrors CI, scoped to changed top-level dirs (`server/` or `web/`). Bypass with `PREPUSH_SKIP=1 git push` or `git push --no-verify`.

### CI mirror (run all CI checks locally)
```bash
make ci                       # Runs server + web CI checks (format, build, test+coverage)
make ci-server                # Server-only subset
make ci-web                   # Web-only subset
```
Mirrors `.github/workflows/server.yml` and `.github/workflows/web.yml` step-for-step. Pre-push hook stays separate — it scopes to changed dirs; `make ci` is the full matrix.

### Server (from repository root)
```bash
dotnet build server           # Build
dotnet test server            # Run all tests
dotnet test server --filter "FullyQualifiedName~TestClassName"  # Run specific tests
make server  # Run with hot reload
dotnet test server /p:CollectCoverage=true /p:Threshold=85%2C85  # Run with coverage + threshold gate (%2C is an escaped comma)
```

Server coverage gate: **85% line / 85% branch** (total), enforced by CI and the pre-push hook. Excluded from the denominator: EF migrations (`server/src/GankedTV.Api/Data/Migrations/**`), `*.generated.cs` (OpenAPI source-generator artifacts), and `Program.cs` (DI/bootstrap wiring, covered indirectly via `WebApplicationFactory` integration tests — see [server/src/GankedTV.Api/Program.Coverage.cs](server/src/GankedTV.Api/Program.Coverage.cs)). **Keep `Program.cs` to pure DI wiring + config binding;** any real logic (validators, feature checks, computation) belongs in a service so it stays inside the coverage denominator. Coverlet config lives in [server/tests/GankedTV.Api.Tests/GankedTV.Api.Tests.csproj](server/tests/GankedTV.Api.Tests/GankedTV.Api.Tests.csproj).

### Database Migrations (run from `server/`)
```bash
dotnet tool restore                                          # first time: restore dotnet-ef local tool
dotnet ef migrations add <Name> --project src/GankedTV.Api   # create a new migration
dotnet ef database update --project src/GankedTV.Api         # apply migrations to the local DB
```
Connection string comes from `DATABASE_URL` env var, falling back to `ConnectionStrings:DefaultConnection` in `appsettings.Development.json`.

### Web (from repository root)
```bash
cd web && bun install         # Install dependencies
cd web && bun dev             # Dev server (http://localhost:5173)
cd web && bun run build       # Production build
cd web && bun run lint        # Lint (oxlint + eslint with auto-fix)
cd web && bun run type-check  # TypeScript check
cd web && bun run test:unit   # Run tests (Vitest)
cd web && bun run test:unit -- --filter="test name"  # Run specific test
cd web && bun run test:coverage  # Run with coverage + threshold gate (scoped)
```

Web coverage gate: **85% line / 85% branch**, scoped to `src/api/**`, `src/router/**`, `src/stores/**` (HTTP client, auth, routing). Components, views, `App.vue`, `main.ts`, and `assets/` are deliberately excluded — the goal is protecting auth/network/routing logic, not display code. Coverage config lives in [web/vitest.config.ts](web/vitest.config.ts).

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
| API        | http://localhost:5050  | -                        |
| Web        | http://localhost:5173  | -                        |
| PostgreSQL | localhost:5435         | gankedtv / gankedtv_dev  |
| MinIO API  | http://localhost:9000  | minioadmin / minioadmin  |
| MinIO UI   | http://localhost:9001  | minioadmin / minioadmin  |

## Git workflow

- Do not add yourself (Claude / any AI) as a co-author. Never append `Co-Authored-By: Claude ...` trailers to commit messages.

## Frontend Design

**Before writing any frontend (Vue components, CSS, Tailwind classes):** Read [web/DESIGN.md](web/DESIGN.md).

It defines the "Underground Arena" design system: typography (Rajdhani, Barlow Condensed, DM Sans, DM Mono), color tokens (`--color-surface-base`, `--color-brand`, `--color-neon`, etc.), layout rules, motion principles, and anti-patterns to avoid. All tokens are CSS custom properties defined in `web/src/assets/base.css`.

## graphify

This project has a graphify knowledge graph at graphify-out/.

Rules:
- Before answering architecture or codebase questions, read graphify-out/GRAPH_REPORT.md for god nodes and community structure
- If graphify-out/wiki/index.md exists, navigate it instead of reading raw files
- After modifying code files in this session, run `graphify update .` to keep the graph current (AST-only, no API cost)
