#!/usr/bin/env bash
# Verify host binaries needed for `make setup`. Presence-only check — versions are
# pinned where authoritative (csproj TargetFramework, web/package.json packageManager).
# On miss, prints the install hint from CLAUDE.md and exits non-zero.

set -eu

uname_s=$(uname -s 2>/dev/null || echo "")

hint_install() {
  case "$1" in
    ffmpeg|ffprobe)
      case "$uname_s" in
        Darwin)  echo "  brew install ffmpeg" ;;
        Linux)
          if command -v pacman >/dev/null 2>&1; then echo "  sudo pacman -S ffmpeg"
          elif command -v apt-get >/dev/null 2>&1; then echo "  sudo apt install ffmpeg"
          else echo "  install ffmpeg via your distro's package manager"
          fi ;;
        *) echo "  install ffmpeg from https://ffmpeg.org/download.html" ;;
      esac ;;
    dotnet)
      case "$uname_s" in
        Darwin) echo "  brew install --cask dotnet-sdk  # or https://dotnet.microsoft.com/download" ;;
        *)      echo "  install the .NET SDK from https://dotnet.microsoft.com/download" ;;
      esac ;;
    bun)
      echo "  curl -fsSL https://bun.sh/install | bash" ;;
    docker)
      case "$uname_s" in
        Darwin) echo "  install Docker Desktop from https://docker.com or 'brew install --cask docker'" ;;
        *)      echo "  install Docker Engine: https://docs.docker.com/engine/install/" ;;
      esac ;;
    curl)
      case "$uname_s" in
        Darwin) echo "  curl ships with macOS; reinstall Command Line Tools: xcode-select --install" ;;
        Linux)
          if command -v pacman >/dev/null 2>&1; then echo "  sudo pacman -S curl"
          elif command -v apt-get >/dev/null 2>&1; then echo "  sudo apt install curl"
          else echo "  install curl via your distro's package manager"
          fi ;;
        *) echo "  install curl from https://curl.se/download.html" ;;
      esac ;;
    *) echo "  install $1" ;;
  esac
}

missing=0
check() {
  if command -v "$1" >/dev/null 2>&1; then
    return
  fi
  echo "✗ missing: $1"
  hint_install "$1"
  missing=1
}

echo "Checking host prerequisites…"
check dotnet
check bun
check docker
check ffmpeg
check ffprobe
# Used by wait-minio's healthcheck poll. Universally present on macOS/Linux but
# checking presence here keeps the Makefile honest.
check curl

# Docker Compose: prefer the v2 plugin (`docker compose`) but accept the legacy
# `docker-compose` v1 binary that the Makefile currently invokes.
if docker compose version >/dev/null 2>&1; then
  :
elif command -v docker-compose >/dev/null 2>&1; then
  :
else
  echo "✗ missing: docker compose (plugin) or docker-compose (v1)"
  echo "  Docker Desktop ships compose v2; on Linux: install docker-compose-plugin"
  missing=1
fi

if [ "$missing" -ne 0 ]; then
  echo
  echo "Install the missing tools above, then re-run 'make setup'."
  exit 1
fi

echo "✓ all prerequisites present."
