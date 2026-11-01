# Operations: deployment, high availability, backup and disaster recovery

## Deployment options

| Target | Files |
|---|---|
| Docker Compose (API, worker, Gitea, SQL Server, Nginx/TLS) | `deploy/docker/docker-compose.yml`, `Dockerfile.api`, `Dockerfile.worker`, `.env.example`, `deploy/nginx/nginx.conf` |
| Linux + Nginx | `deploy/linux/*.service`, `deploy/linux/nginx-site.conf` |
| Windows Server + IIS | `deploy/iis/web.config`, `deploy/iis/README.md` |
| Gitea configuration backups | `deploy/gitea/backup-gitea.sh` (`gitea dump`) |

The API and worker are independently deployable. Run the API with `Worker:RunInApi=false` when a separate worker is deployed. Both need the same configuration, database and workspace volume.

### First start

1. **Database** — SQL Server: create an empty database and run `database/sqlserver/create.sql`; set `Database:Provider=SqlServer`, `Database:AutoCreate=false`. (SQLite with auto-create is for development only.)
2. **Gitea** — install (same host or, preferably, a separate server with its own storage), disable registration, require sign-in, private-by-default repositories, HTTPS. Create a dedicated service user (e.g. `wings-git-manager`) with permission to create organizations, and an access token with `write:organization`, `write:repository`, `read:user` scopes (or an admin token). Configure `Gitea:BaseUrl`, `Gitea:ServiceUser`, `Gitea:Token`, optional `Gitea:OrganizationPrefix`.
3. **Webhooks** (optional) — set `Gitea:WebhookUrl` (reachable from Gitea) and `Gitea:WebhookSecret`; allow the host in Gitea's `[webhook] ALLOWED_HOST_LIST`.
4. **Backups** — set `Backup:EncryptionKey` (`openssl rand -base64 32`) and store a copy **outside** the platform (password manager/KMS). Point `Backup:Provider` at S3-compatible storage in a different failure domain (`Backup:S3:*`, enable bucket versioning; set `ObjectLockDays` when the bucket has Object Lock) or at a file-system path on separate storage.
5. **Workspaces** — set `GitManager:WorkspaceRoots` to the directory Wings Code uses for projects (`<root>/<company-slug>/<project>/main`). Keep `EnforceCompanyDirectories=true`.
6. **Bootstrap** — on first start an administrator and an API token are created; the token is written to `<DataDirectory>/bootstrap-admin-token.txt`. Store it securely and delete the file. Create companies, the Wings Code service account and its scoped token (see `docs/wings-code-integration.md`).
7. **Authentication for people** — set `Authentication:Oidc:Authority`/`Audience` (e.g. the Wings Authentication server). Users must be provisioned (`POST /api/v1/users`) before they can sign in; the OIDC subject is bound on first login.

### Configuration reference (most important keys)

| Key | Default | Purpose |
|---|---|---|
| `GitManager:WorkspaceRoots` | — | Allowed working-directory roots |
| `GitManager:DataDirectory` | `App_Data/git-manager` | Mirrors, private git config, data-protection keys, scratch |
| `GitManager:DefaultProvider` | `Gitea` | `Gitea`, `FileSystem`, `GenericRemote` |
| `GitManager:AllowHttpRemotes` | `false` | Allow `http://` remotes (internal networks only) |
| `GitManager:SynchronousPushTimeoutSeconds` | `60` | Time `complete` waits for push + verification before returning `RemotePushPending` |
| `Sync:MaxRetries` / `BaseRetrySeconds` / `MaxRetrySeconds` | 12 / 10 / 3600 | Exponential retry policy |
| `Backup:VerifyEveryHours` / `MinimumRetainedVerified` | 24 / 3 | Re-verification (with test restore) and retention floor |
| `Monitoring:*` | | Thresholds for storage, unpushed commits, stale backups, fsck interval |
| `Worker:RunInApi` | `true` | Host workers inside the API (single node) |

## High availability

* **API**: stateless apart from the database and the workspace volume; run several instances behind the load balancer. Mutation locks and idempotency records live in the database, so instances coordinate. SignalR without a backplane needs sticky sessions (or add the Redis/Azure SignalR backplane).
* **Worker**: run one or more instances; queue items and jobs are claimed with leases (`LockedUntil`), so a crashed worker's work is picked up after the lease expires.
* **Gitea**: separate host and storage volume from Wings Code working directories. For HA use Gitea with an external PostgreSQL/MySQL, shared repository storage and multiple instances behind a load balancer, or a warm standby restored from `gitea dump` + Wings backups.
* **SQL Server**: Always On availability group or managed SQL with point-in-time restore. Connection resiliency (`EnableRetryOnFailure`) is enabled.
* **Backups**: a different storage system/account/region than Gitea and SQL Server.

## Monitoring

`GET /health/live` and `/health/ready` (anonymous, for load balancers), `GET /api/v1/system/health` (database, git, Gitea, backup store, sync queue, jobs, volumes, encryption key), `GET /api/v1/alerts`, Serilog JSON logs with correlation ids. Alerts: missing working directories, uncommitted external changes, unpushed commits, failed/conflicted pushes, remote divergence, `fsck` failures, storage thresholds, unverified/stale backups, abandoned worktrees, Gitea health, denied-access bursts, repeated recovery failures, blocked high-risk operations.

## Disaster recovery procedures

### A. A local project directory was deleted
Code is safe on Gitea. `POST /api/v1/repositories/{id}/recovery/clone` (or the UI recovery wizard / Wings Code "recover the project I deleted"). The clone is checked out at the verified head, validated (`HEAD == expected`, `fsck`, clean status) and re-registered as the main working directory.

### B. One repository was deleted or corrupted on Gitea
1. In the UI → repository → Backups, or `GET /api/v1/repositories/{id}/backups`, pick the latest **Verified** backup.
2. `POST /api/v1/repositories/{id}/backups/{backupId}/restore {"target":"remote","approved":true}` — the repository is re-created in the company's organization, every branch and tag (plus branches that existed only locally) is pushed without force, the default branch head is verified and branch protection is re-applied.
3. `POST /api/v1/repositories/{id}/sync/fetch`, then resume work. Commits made after the backup that still exist in working directories are pushed automatically by the queue.

### C. Complete Gitea server loss
1. Provision a new Gitea (same or new URL). Restore its configuration from the latest `gitea dump` if available (users, organizations, settings); otherwise create the service user and token again.
2. Update `Gitea:BaseUrl`/`Gitea:Token` if they changed and restart API + worker.
3. For every repository (script over `GET /api/v1/repositories?pageSize=200`): restore its latest verified backup with `target=remote` (step B.2). Organizations are created automatically.
4. Run `POST /sync/fetch` per repository; the push queue then uploads any newer local commits from working directories. Check `GET /api/v1/dashboard/summary` (pending/failed pushes, divergence) and `/alerts`.
5. Verify a sample of repositories with standard Git (`git clone`, `git log`) — repositories are plain Git.

### D. Wings Git Manager database loss
Restore SQL Server from its own backups. If unavailable, the system-wide metadata export (`system/metadata-*.json.enc` in the backup store, decrypt with the backup key) contains companies, projects, repository mappings, remotes, settings, sessions, checkpoints and recovery records for reconstruction; Git history itself is in Gitea and the repository backups.

### E. Backup key loss
Encrypted backups cannot be decrypted without `Backup:EncryptionKey`. Keep at least two offline copies; rotating the key only affects new backups (keep old keys until their backups expire).

### Periodic tests
The scheduler re-verifies the newest backup of every repository daily with a full test restore (`Backup:VerifyEveryHours`). Additionally rehearse procedure C on a staging Gitea at least quarterly.
