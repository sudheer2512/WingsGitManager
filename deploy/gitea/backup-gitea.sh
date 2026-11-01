#!/usr/bin/env bash
# Gitea configuration + metadata backup (complements Wings Git Manager's repository bundles).
# Run on the Gitea host as the gitea user, e.g. nightly from cron. Copies the dump to independent storage.
set -euo pipefail
GITEA_BIN="${GITEA_BIN:-/usr/local/bin/gitea}"
GITEA_CONFIG="${GITEA_CONFIG:-/etc/gitea/app.ini}"
OUT_DIR="${OUT_DIR:-/var/backups/gitea}"
mkdir -p "$OUT_DIR"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
"$GITEA_BIN" dump --config "$GITEA_CONFIG" --type tar.gz --file "$OUT_DIR/gitea-dump-$STAMP.tar.gz" --skip-log
sha256sum "$OUT_DIR/gitea-dump-$STAMP.tar.gz" > "$OUT_DIR/gitea-dump-$STAMP.tar.gz.sha256"
# Ship off-host (example: S3-compatible storage with versioning/object lock):
#   aws s3 cp "$OUT_DIR/gitea-dump-$STAMP.tar.gz" "s3://$BUCKET/gitea/" --sse AES256
find "$OUT_DIR" -name 'gitea-dump-*' -mtime +30 -delete
