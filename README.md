# GankedTV

Social media platform to share your gaming clips.

## Tech Stack

- **Backend:** C# (.NET 10)
- **Frontend:** Vue 3 + TypeScript (Bun)
- **Database:** PostgreSQL
- **Storage:** S3 (MinIO)

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Bun](https://bun.sh)
- [Docker](https://www.docker.com/get-started)
- **ffmpeg** (provides `ffmpeg` and `ffprobe`) — required for the thumbnail-generation worker.
  Without it, clip uploads land in `status='failed'` after the worker exhausts its retries.
  - Arch/CachyOS: `sudo pacman -S ffmpeg`
  - Debian/Ubuntu: `sudo apt install ffmpeg`
  - macOS: `brew install ffmpeg`
  - Set `FFMPEG_PATH` / `FFPROBE_PATH` env vars if the binaries aren't on `$PATH`.

## Getting Started

```bash
make setup
make dev-all
```

That's it. `make setup` wipes the local Postgres + MinIO volumes, verifies host prerequisites, pulls/starts both services, waits for them to be healthy, restores server (NuGet + dotnet-ef) and web (bun) packages, applies migrations, seeds a test user and ten playable clips with real video + thumbnails uploaded to MinIO, and installs the pre-push git hook. Re-running gives you the same known-good state every time.

> **Destructive.** Any users you registered, clips you uploaded, or other dev data will be lost on every `make setup`. Use the manual setup below if you want to keep local state.

After it finishes:
- API on `http://localhost:5050`
- Web on `http://localhost:5173`
- Sign in with `seeduser@dev.local` / `testpass123!`

### Manual setup (advanced)

If you want to start pieces individually:

```bash
make up                                   # postgres + minio
cd server && dotnet run --project src/GankedTV.Api
cd web && bun install && bun dev
```

Migrations and seed live behind `make migrate` / `make seed` — `make migrate` now blocks until Postgres is healthy.

## Development

### Server Commands

```bash
cd server
dotnet build              # Build the solution
dotnet test               # Run tests
dotnet watch --project src/GankedTV.Api  # Run with hot reload
```

### Database Migrations

`dotnet-ef` is pinned as a local tool in [server/.config/dotnet-tools.json](server/.config/dotnet-tools.json). First-time setup:

```bash
cd server
dotnet tool restore
```

Then, from `server/`:

```bash
dotnet ef migrations add <Name> --project src/GankedTV.Api   # create a new migration
dotnet ef database update --project src/GankedTV.Api         # apply migrations to the local DB
```

The connection string is read from the `DATABASE_URL` env var, falling back to `ConnectionStrings:DefaultConnection` in `appsettings.Development.json` (preconfigured to hit the compose Postgres on port 5435).

### Web Commands

```bash
cd web
bun install               # Install dependencies
bun dev                   # Start dev server
bun run build             # Build for production
bun run lint              # Run linter
bun run test:unit         # Run tests
```

### Local quality gate

Run once to install the pre-push git hook:

```bash
make hooks
```

This points git at `.githooks/` (via `core.hooksPath`). On `git push`, the hook runs the same checks CI runs, scoped to whichever top-level area changed (`server/` or `web/`). Bypass with `PREPUSH_SKIP=1 git push` or `git push --no-verify` when truly needed.

To run the full CI matrix manually (format + build + test + coverage on both halves):

```bash
make ci          # server + web
make ci-server   # server only
make ci-web      # web only
make ci-discord  # discord bot only
```

### Parallel worktrees

Run multiple issues in parallel, each with its own isolated dev stack:

```bash
./scripts/new-worktree.sh 92      # create + bootstrap
./scripts/remove-worktree.sh 92   # tear down
```

Creates `.worktrees/issue-92/` (gitignored) with a `.env.worktree.local` containing deterministic per-issue port offsets (`offset = (sha1(<issue>) mod 50) * 10`, applied to all five host ports), then runs compose up + migrate + seed. After it finishes: `cd .worktrees/issue-92 && make dev-all`. If the `code` CLI is on `$PATH`, a new VS Code window opens on the worktree automatically — set `WORKTREE_EDITOR=cursor` (or any VS Code-family CLI) to use a different editor, or `WORKTREE_NO_OPEN=1` to skip. Forgotten which port a worktree is on? `make ports` from inside the worktree prints the summary. Teardown also deletes the local branch (safely, via `git branch -d`; pass `--force` to escalate to `-D`). The main checkout's defaults are unchanged.

### Seeded test account

`make seed` creates a known test user so contributors can sign in immediately on a fresh DB without configuring OAuth credentials:

| Field    | Value                  |
|----------|------------------------|
| Email    | `seeduser@dev.local`   |
| Password | `testpass123!`         |

Use these credentials at `http://localhost:5173/login` (or `POST /auth/login` with `{ email, password }`).

## Docker

### Start Infrastructure

```bash
make up    # docker compose -f docker-compose.dev.yml up -d
```

### Access MinIO Console

Open `http://localhost:9001` and log in with:
- Username: `minioadmin`
- Password: `minioadmin`

## Project Structure

```
gankedtv/
├── server/                 # .NET Backend
│   ├── src/
│   │   └── GankedTV.Api/   # Main API project
│   └── tests/
│       └── GankedTV.Api.Tests/
├── web/                    # Vue Frontend
│   ├── src/
│   └── public/
├── discord/                # Discord bot (Bun + discord.js, off by default)
│   ├── src/
│   └── tests/
├── shared/                 # Code shared across web + discord
├── .github/workflows/      # CI/CD
│   ├── server.yml
│   ├── web.yml
│   ├── discord.yml
│   └── release.yml
├── docker-compose.dev.yml  # PostgreSQL + MinIO (local dev)
├── docker-compose.prod.yml # Full production stack
└── .env.example
```

## License

See [LICENSE](LICENSE) for details.
