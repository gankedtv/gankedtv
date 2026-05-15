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

### 1. Start Infrastructure

```bash
docker-compose up -d
```

This starts:
- PostgreSQL on port `5432`
- MinIO on port `9000` (API) and `9001` (Console)

### 2. Run the Server

```bash
cd server
dotnet run --project src/GankedTV.Api
```

API available at `http://localhost:5050`

### 3. Run the Web App

```bash
cd web
bun install
bun dev
```

App available at `http://localhost:5173`

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
make ci          # both halves
make ci-server   # server only
make ci-web      # web only
```

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
docker-compose up -d
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
├── .github/workflows/      # CI/CD
│   ├── server.yml
│   └── web.yml
├── docker-compose.yml      # PostgreSQL + MinIO
└── .env.example
```

## License

See [LICENSE](LICENSE) for details.
