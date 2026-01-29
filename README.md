# GankedTV

Social media platform to share your gaming clips.

## Tech Stack

- **Backend:** C# (.NET 10)
- **Frontend:** Vue 3 + TypeScript (Bun)
- **Database:** PostgreSQL
- **Storage:** S3 (MinIO)

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- [Bun](https://bun.sh)
- [Docker](https://www.docker.com/get-started)

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

API available at `http://localhost:5000`

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

### Web Commands

```bash
cd web
bun install               # Install dependencies
bun dev                   # Start dev server
bun run build             # Build for production
bun run lint              # Run linter
bun run test:unit         # Run tests
```

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
