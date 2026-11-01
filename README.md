# Wings Git Manager

Git repository management, automated version control, AI-session awareness, backup and recovery for applications built with **Wings Code** — an orchestration layer on top of standard Git and **Gitea**. Git stays the version-control engine; every repository remains an ordinary Git repository usable with standard tools.

```
Wings Code ──REST/SDK──▶ Wings Git Manager API ──git CLI──▶ working directories / worktrees
                               │   ▲                └──git push/fetch (verified)──▶ Gitea (central server)
                               │   └── SignalR ──▶ Angular management UI
                               ├──▶ SQL Server (metadata, queues, audit)
                               └── Worker ──▶ independent encrypted backups (file system / S3)
```

## What it does

| Area | Highlights |
|---|---|
| Repository provisioning | Idempotent per (company, Wings Code project): Gitea organization per company, private repository, `git init`, `.gitignore`, secret exclusions, initial commit, push, **remote verification**, branch protection. Import of existing local or remote repositories with full history. |
| Change tracking | `changes/begin` → policy evaluation, pre-change checkpoint (pre-existing uncommitted work is committed separately), workspace lease. `changes/{id}/complete` → stages only that operation's files, secret scan, structured conventional commit with `Wings-*` trailers, durable push queue, verification. Abort preserves work on a recovery branch. Crash/lease reconciliation. |
| Durability | Checkpoint states `WorkingTreeOnly → LocalCommit → RemotePushPending → RemoteSynchronized → IndependentlyBackedUp` (+ `Failed`, `RemoteDiverged`). A commit is "remote" only after `ls-remote` containment **and** the Gitea API confirm it. |
| High-risk policy | Directory/multi-file deletion, large refactors, mass renames, migrations, auth code, deployment config, shared libraries, dependency manifests, declared categories. High risk ⇒ the starting checkpoint must be pushed and verified first or the operation is blocked; audited administrator override. Configurable per company/project/repository. |
| Parallel AI agents | One isolated Git worktree + `ai/session-*` branch per session, ownership enforced, short-lived mutation locks shared by API and worker, optimistic concurrency via expected HEAD, never force-push. Controlled merges with `git merge-tree` conflict detection, dry run and approval. |
| Recovery | Preview (dry run) for every operation; restore file, restore deleted files, restore project (side-by-side worktree by default, or rollback commit), revert AI session / commits (selective by path, conflicts simulated in a throw-away worktree), recover a deleted local project by cloning. Uncommitted work is always saved first; history is never rewritten. |
| Conversational recovery | `recovery/candidates` interprets requests such as *"Undo the changes you made in the last session"* or *"Restore the invoice calculation code from yesterday"* and returns ranked recovery points, ambiguity flag and change summaries for Wings Code to explain. |
| Backups | Mirror of the central server (no prune) + local-only branches → `git bundle` + metadata → AES-256-GCM → file system or S3-compatible storage (object lock supported). Verification = checksum + bundle verify + test restore + `fsck` + ref comparison. Restore to a directory or re-create the central repository after server loss. |
| Operations | Monitoring (missing working directories, unpushed commits, divergence, `fsck`, storage, stale backups, abandoned worktrees, Gitea health, denied access, recovery failures), alerts, dashboard, health endpoints, audit log, Serilog, OpenAPI, SignalR. |
| Security | API tokens (scoped, hashed) + OIDC bearer, company isolation enforced independently of Gitea, roles, path-traversal and symlink-escape protection, allow-listed git commands without a shell, hooks/fsmonitor/credential helpers disabled, credentials only via environment-injected headers, secret scanning. |

## Repository layout

```
src/Wings.GitManager.Contracts       DTOs/enums shared with the SDK
src/Wings.GitManager.Core            entities, options, policy engine, secret scanner, commit messages, intent parser
src/Wings.GitManager.Infrastructure  EF Core, git execution, Gitea/file-system/generic providers, services, backups, workers
src/Wings.GitManager.Api             ASP.NET Core API, auth, middleware, SignalR, OpenAPI (serves the UI from wwwroot)
src/Wings.GitManager.Worker          independently deployable background worker
src/Wings.GitManager.Client          typed C# SDK for Wings Code
web/                                 Angular management UI (builds into the API's wwwroot)
tests/                               unit + integration tests (real git, real Gitea)
database/sqlserver/create.sql        SQL Server schema
deploy/                              Docker Compose (API, worker, UI, Gitea, SQL Server, Nginx/TLS), systemd, IIS, Gitea backup
docs/                                architecture, Wings Code integration, operations/DR, security, testing
```

## Quick start (development, macOS/Linux)

Prerequisites: .NET 10 SDK, Git ≥ 2.38, Node 22+ (UI). A Gitea binary is downloaded into `.tools/gitea` (checksum-verified).

```bash
scripts/gitea-dev.sh init     # local Gitea on :3380, admin + API token in App_Data/dev-credentials.env
scripts/gitea-dev.sh start
scripts/run-dev.sh            # API + in-process workers on http://localhost:5380
```

* API reference: <http://localhost:5380/api-docs> (OpenAPI JSON at `/openapi/v1.json`, also in `docs/openapi.json`).
* The bootstrap administrator token is written once to `App_Data/git-manager/bootstrap-admin-token.txt`.
* UI dev server: `npm --prefix web start` → <http://localhost:4380> (proxies to the API).

## Tests

```bash
dotnet test Wings.GitManager.slnx
```

106 unit tests and 32 integration tests (in-process API, SQLite, real git, file-system remotes; 4 tests run against a real Gitea and are skipped when it is not running). See [docs/testing.md](docs/testing.md) for the mapping to the 30 required scenarios.

## Documentation

* [Architecture](docs/architecture.md)
* [Wings Code integration guide (SDK, change lifecycle, conversational recovery)](docs/wings-code-integration.md)
* [Operations: deployment, HA, backup and disaster recovery](docs/operations.md)
* [Security model](docs/security.md)
* [Testing](docs/testing.md)
