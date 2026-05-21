# Per-worktree env file (see scripts/new-worktree.sh and README "Parallel
# worktrees"). The leading `-` makes -include a no-op when the file is missing,
# so the main checkout with no .env.worktree.local behaves identically to today.
-include .env.worktree.local

# Defaults match the historical hardcoded values. Override via .env.worktree.local.
POSTGRES_HOST_PORT ?= 5435
MINIO_API_HOST_PORT ?= 9000
MINIO_CONSOLE_HOST_PORT ?= 9001
ASPNETCORE_URLS ?= http://localhost:5050
VITE_PORT ?= 5173

# Bare `export` exports every variable DEFINED in this Makefile (or via the
# include above) into recipe subshells — that includes the ?= defaults and
# anything pulled in from .env.worktree.local. Crucially, vars that are NOT
# defined here (DATABASE_URL, S3_ACCESS_KEY, etc. in the main checkout) are
# left alone, so dotnet/bun still see "unset" and fall back to
# appsettings.Development.json defaults instead of inheriting an empty string.
export

# docker compose only auto-reads `.env`; pass our worktree env file explicitly
# when it exists so port vars + COMPOSE_PROJECT_NAME resolve identically across
# up/down/clean. Wildcard returns empty when the file is absent → flag drops out.
COMPOSE_ENV_FILE := $(if $(wildcard .env.worktree.local),--env-file .env.worktree.local,)
COMPOSE := docker-compose -f docker-compose.dev.yml $(COMPOSE_ENV_FILE)

.PHONY: setup server-install wait-postgres wait-minio up down clean logs server server-build server-test migrate migrate-add seed web web-install web-build web-test web-lint dev-all hooks ci ci-server ci-web ports

# One-command dev bootstrap. DESTRUCTIVE: wipes the local Postgres + MinIO volumes
# so every run lands you on a known-good state from migrations + seed. Steps:
# clean → prereqs → image pull → infra up + healthy → server + web deps →
# migrations → seed → git hooks.
setup:
	@echo "⚠ make setup will wipe local postgres + minio volumes (dev data lost)."
	$(MAKE) clean
	@./scripts/check-prereqs.sh
	$(COMPOSE) pull
	$(MAKE) up
	$(MAKE) wait-minio
	$(MAKE) server-install
	$(MAKE) web-install
	$(MAKE) migrate
	$(MAKE) seed
	$(MAKE) hooks
	@echo
	@echo "✓ setup complete. Next: 'make dev-all' to start the API + web."
	@echo "  (For real game cover art, set IGDB_CLIENT_ID/SECRET and run 'make import-games'.)"

# Restore server-side packages: the dotnet-ef local tool plus all NuGet refs.
# Separated from `migrate` so `make setup` has an explicit install step
# symmetric with `web-install`. Safe to re-run — both restores are idempotent
# and cached, so this only does network work when something changed.
server-install:
	cd server && dotnet tool restore
	cd server && dotnet restore

# Internal: block until the postgres service reports ready. `migrate` depends on this
# so the EF tool can't silently no-op against a not-yet-healthy DB (a real footgun we
# hit when chaining `make up && make migrate`). Uses compose-resolved service names so
# the wait works regardless of the user's directory name / compose project name.
wait-postgres:
	@printf "Waiting for postgres "
	@for i in $$(seq 1 60); do \
	  if $(COMPOSE) exec -T postgres pg_isready -U gankedtv -d gankedtv >/dev/null 2>&1; then echo " ready."; exit 0; fi; \
	  printf "."; sleep 1; \
	done; echo " timed out after 60s"; exit 1

# Internal: block until MinIO answers its liveness probe. Seed uploads synthetic
# clip media here, so the bucket bootstrap + PutObject calls must not race startup.
wait-minio:
	@printf "Waiting for minio "
	@for i in $$(seq 1 60); do \
	  if curl -fsS http://localhost:$(MINIO_API_HOST_PORT)/minio/health/live >/dev/null 2>&1; then echo " ready."; exit 0; fi; \
	  printf "."; sleep 1; \
	done; echo " timed out after 60s"; exit 1

# Print the current stack's URLs. In the main checkout this shows the defaults;
# inside a worktree (where .env.worktree.local is loaded) it shows the offsets.
# Useful when the bootstrap output has scrolled past and you've forgotten which
# stack lives where.
ports:
	@printf "postgres   localhost:%s\n" "$(POSTGRES_HOST_PORT)"
	@printf "minio      localhost:%s  (console: localhost:%s)\n" "$(MINIO_API_HOST_PORT)" "$(MINIO_CONSOLE_HOST_PORT)"
	@printf "api        %s\n" "$(ASPNETCORE_URLS)"
	@printf "web        http://localhost:%s\n" "$(VITE_PORT)"
	@test -z "$(COMPOSE_PROJECT_NAME)" || printf "project    %s\n" "$(COMPOSE_PROJECT_NAME)"

# Infrastructure
up:
	$(COMPOSE) up -d

down:
	$(COMPOSE) down

clean:
	$(COMPOSE) down -v

logs:
	$(COMPOSE) logs -f

# Server
# ASPNETCORE_URLS is passed inline rather than relying on env-inheritance through
# dotnet watch's launch-profile machinery — env precedence between launchSettings
# and parent env is inconsistent across .NET versions, and inline assignment is
# unambiguous.
server:
	ASPNETCORE_URLS=$(ASPNETCORE_URLS) dotnet watch --project server/src/GankedTV.Api

server-build:
	dotnet build server

server-test:
	dotnet test server

# Apply EF migrations to the local DB. Restores the dotnet-ef local tool on
# first run so you don't need to remember `dotnet tool restore`. --startup-project
# is explicit so this keeps working when GankedTV.Workers extracts and the EF
# entities live in a class library that isn't itself a host.
migrate: wait-postgres
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

# Backfill the games catalog + cover art from IGDB. Idempotent / resumable. Requires
# IGDB_CLIENT_ID and IGDB_CLIENT_SECRET (Twitch app client-credentials) in the environment.
import-games:
	dotnet run --project server/src/GankedTV.Api -- --import-games

# Web
web-install:
	cd web && bun install

web:
	cd web && bun dev --port $(VITE_PORT)

web-build:
	cd web && bun run build

web-test:
	cd web && bun run test:unit

web-lint:
	cd web && bun run lint

# Combined (Ctrl+C stops both server and web)
dev-all: up
	@trap 'kill 0' EXIT; \
	ASPNETCORE_URLS=$(ASPNETCORE_URLS) dotnet watch --project server/src/GankedTV.Api & \
	cd web && bun dev --port $(VITE_PORT)

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
