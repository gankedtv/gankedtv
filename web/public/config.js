// Committed default — intentionally empty so dev/local builds fall back to import.meta.env (the
// VITE_* values from .env / Vaultwarden). The production web image's entrypoint
// (web/docker-entrypoint.sh) overwrites /srv/config.js with values from container env at startup.
window.__APP_CONFIG__ = {}
