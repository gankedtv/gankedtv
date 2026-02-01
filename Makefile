.PHONY: up down clean logs server server-build server-test web web-install web-build web-test web-lint dev-all

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
