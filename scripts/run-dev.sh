#!/usr/bin/env bash
# Runs the API (with in-process workers) against the local development Gitea on http://localhost:5380.
# Secrets come from App_Data/dev-credentials.env (created by scripts/gitea-dev.sh init); a backup key is added on first run.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CREDS="$ROOT/App_Data/dev-credentials.env"
[ -f "$CREDS" ] || { echo "Run scripts/gitea-dev.sh init first" >&2; exit 1; }
grep -q '^BACKUP_KEY=' "$CREDS" || echo "BACKUP_KEY=$(openssl rand -base64 32)" >> "$CREDS"
set -a; . "$CREDS"; set +a
export Gitea__Token="$GITEA_ADMIN_TOKEN" Backup__EncryptionKey="$BACKUP_KEY" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"  # (alternatively: dotnet user-secrets)
curl -sf "$GITEA_URL/api/healthz" >/dev/null || "$ROOT/scripts/gitea-dev.sh" start
cd "$ROOT/src/Wings.GitManager.Api"
exec "${DOTNET:-$HOME/.dotnet/dotnet}" run --launch-profile http
