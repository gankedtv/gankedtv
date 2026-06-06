#!/bin/sh
# Generate the SPA's runtime config from container env, then hand off to Caddy. This is what keeps
# the published image generic: the same bundle reads window.__APP_CONFIG__ (see src/config.ts), and
# each deployment supplies its own values as env vars instead of baking them at build time.
#
# Values are JSON-encoded (jq) so quotes/backslashes in a value can't break out of the JS string.
# Any var left unset becomes "" → src/config.ts treats blanks as "not provided".
set -eu

json() { printf '%s' "${1:-}" | jq -Rs .; }

cat > /srv/config.js <<EOF
window.__APP_CONFIG__ = {
  VITE_API_BASE_URL: $(json "${VITE_API_BASE_URL:-}"),
  VITE_GA_MEASUREMENT_ID: $(json "${VITE_GA_MEASUREMENT_ID:-}"),
  VITE_USE_SECURE_COOKIES: $(json "${VITE_USE_SECURE_COOKIES:-}"),
  VITE_MAX_UPLOAD_SIZE_MB: $(json "${VITE_MAX_UPLOAD_SIZE_MB:-}"),
  VITE_SENTRY_DSN: $(json "${VITE_SENTRY_DSN:-}"),
  VITE_SENTRY_ENVIRONMENT: $(json "${VITE_SENTRY_ENVIRONMENT:-}"),
  VITE_SENTRY_RELEASE: $(json "${VITE_SENTRY_RELEASE:-}"),
  VITE_SENTRY_TRACES_SAMPLE_RATE: $(json "${VITE_SENTRY_TRACES_SAMPLE_RATE:-}")
}
EOF

exec caddy run --config /etc/caddy/Caddyfile --adapter caddyfile
