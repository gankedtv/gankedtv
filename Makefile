.PHONY: up down clean logs server server-build server-test migrate migrate-add seed web web-install web-build web-test web-lint dev-all hooks ci ci-server ci-web

# Infrastructure
up:
	docker-compose -f docker-compose.dev.yml up -d

down:
	docker-compose -f docker-compose.dev.yml down

clean:
	docker-compose -f docker-compose.dev.yml down -v

logs:
	docker-compose -f docker-compose.dev.yml logs -f

# Server
server:
	dotnet watch --project server/src/GankedTV.Api

server-build:
	dotnet build server

server-test:
	dotnet test server

# Apply EF migrations to the local DB. Restores the dotnet-ef local tool on
# first run so you don't need to remember `dotnet tool restore`. --startup-project
# is explicit so this keeps working when GankedTV.Workers extracts and the EF
# entities live in a class library that isn't itself a host.
migrate:
	cd server && dotnet tool restore
	cd server && dotnet ef database update --project src/GankedTV.Api --startup-project src/GankedTV.Api

# Create a new migration: `make migrate-add NAME=AddSomething`.
migrate-add:
	@if [ -z "$(NAME)" ]; then echo "usage: make migrate-add NAME=<MigrationName>"; exit 1; fi
	cd server && dotnet tool restore
	cd server && dotnet ef migrations add $(NAME) --project src/GankedTV.Api --startup-project src/GankedTV.Api

# Idempotent dev seed: inserts a known test user (`seeduser`) and ten sample clips so
# the feed isn't empty on a fresh DB. Refuses to run unless ASPNETCORE_ENVIRONMENT
# is Development, which is the default for the dev compose stack.
seed:
	ASPNETCORE_ENVIRONMENT=Development dotnet run --project server/src/GankedTV.Api -- --seed

# Web
web-install:
	cd web && bun install

web:
	cd web && bun dev

web-build:
	cd web && bun run build

web-test:
	cd web && bun run test:unit

web-lint:
	cd web && bun run lint

# Combined (Ctrl+C stops both server and web)
dev-all: up
	@trap 'kill 0' EXIT; \
	dotnet watch --project server/src/GankedTV.Api & \
	cd web && bun dev

# CI mirror: runs the same checks the GitHub workflows run, in verify-only mode.
# Mirrors `.github/workflows/server.yml` and `.github/workflows/web.yml` step-for-step
# so contributors can reproduce CI locally with one command.
#
# Pre-push hook is intentionally NOT wired through here — it scopes to changed dirs
# (server/ vs web/), `make ci` runs the full matrix. Use sub-targets for partial runs.
ci: ci-server ci-web

ci-server:
	cd server && dotnet format --verify-no-changes
	cd server && dotnet build --configuration Release
	cd server && dotnet test --configuration Release /p:CollectCoverage=true /p:Threshold=85%2C85

ci-web:
	cd web && bun install --frozen-lockfile
	cd web && bun run format:check
	cd web && bun run lint:check
	cd web && bun run build
	cd web && bun run test:coverage

# Git hooks — point git at the tracked .githooks/ directory.
hooks:
	git config --local core.hooksPath .githooks
	@echo "pre-push hook active. Bypass with PREPUSH_SKIP=1 or git push --no-verify."
