#!/usr/bin/env bash
# Local development Gitea instance for Wings Git Manager.
#   scripts/gitea-dev.sh init   - write app.ini, migrate DB, create admin + API token (App_Data/dev-credentials.env)
#   scripts/gitea-dev.sh start  - run Gitea in the background on http://localhost:3380
#   scripts/gitea-dev.sh stop
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GITEA_BIN="${GITEA_BIN:-$ROOT/.tools/gitea/gitea-28.2.0-darwin-arm64}"
WORK="$ROOT/App_Data/gitea"
CONF="$WORK/custom/conf/app.ini"
PORT="${GITEA_PORT:-3380}"
CREDS="$ROOT/App_Data/dev-credentials.env"

gitea() { "$GITEA_BIN" --work-path "$WORK" --config "$CONF" "$@"; }

case "${1:-}" in
  init)
    mkdir -p "$WORK/custom/conf" "$WORK/data" "$WORK/log"
    if [ ! -f "$CONF" ]; then
      cat > "$CONF" <<INI
APP_NAME = Wings Gitea (dev)
RUN_MODE = prod
WORK_PATH = $WORK

[server]
HTTP_ADDR = 127.0.0.1
HTTP_PORT = $PORT
ROOT_URL = http://localhost:$PORT/
DISABLE_SSH = true
LFS_START_SERVER = false
OFFLINE_MODE = true

[database]
DB_TYPE = sqlite3
PATH = $WORK/data/gitea.db

[repository]
ROOT = $WORK/data/repositories
DEFAULT_PRIVATE = private
DEFAULT_BRANCH = main
DISABLE_DOWNLOAD_SOURCE_ARCHIVES = true

[security]
INSTALL_LOCK = true
SECRET_KEY = $(openssl rand -hex 32)
INTERNAL_TOKEN = $(openssl rand -hex 48)
PASSWORD_HASH_ALGO = pbkdf2

[service]
DISABLE_REGISTRATION = true
REQUIRE_SIGNIN_VIEW = true

[oauth2]
JWT_SECRET = $(openssl rand -base64 32 | tr -d '=' | tr '+/' '-_')

[log]
MODE = file
LEVEL = Warn
ROOT_PATH = $WORK/log

[webhook]
ALLOWED_HOST_LIST = loopback
INI
    fi
    gitea migrate >/dev/null
    if ! grep -q GITEA_ADMIN_TOKEN "$CREDS" 2>/dev/null; then
      PASS="$(openssl rand -base64 24 | tr -d '/+=')"
      gitea admin user create --admin --username wings-admin --password "$PASS" --email wings-admin@localhost --must-change-password=false >/dev/null
      TOKEN="$(gitea admin user generate-access-token --username wings-admin --token-name wings-git-manager --scopes all --raw)"
      {
        echo "GITEA_URL=http://localhost:$PORT"
        echo "GITEA_ADMIN_USER=wings-admin"
        echo "GITEA_ADMIN_PASSWORD=$PASS"
        echo "GITEA_ADMIN_TOKEN=$TOKEN"
      } >> "$CREDS"
      chmod 600 "$CREDS"
    fi
    echo "Gitea initialised in $WORK (credentials in App_Data/dev-credentials.env)"
    ;;
  start)
    nohup "$GITEA_BIN" --work-path "$WORK" --config "$CONF" web > "$WORK/log/stdout.log" 2>&1 &
    echo $! > "$WORK/gitea.pid"
    for _ in $(seq 1 30); do curl -sf "http://localhost:$PORT/api/healthz" >/dev/null && { echo "Gitea up on http://localhost:$PORT"; exit 0; }; sleep 1; done
    echo "Gitea did not become healthy; see $WORK/log" >&2; exit 1
    ;;
  stop)
    [ -f "$WORK/gitea.pid" ] && kill "$(cat "$WORK/gitea.pid")" 2>/dev/null || true
    rm -f "$WORK/gitea.pid"
    ;;
  *) echo "usage: $0 init|start|stop" >&2; exit 2 ;;
esac
