# Security model

## Authentication
* **API tokens** (`Authorization: Bearer wgm_…`) for service-to-service calls (Wings Code AI agent service, automation) and administrators. 256-bit random secrets, stored only as SHA-256 hashes, shown once, optional expiry, revocable, last-use tracking. Scopes: `repos:read`, `repos:write`, `changes:write`, `recovery:execute`, `backups:manage`, `admin`.
* **OIDC / OAuth 2.0 bearer tokens** when `Authentication:Oidc:Authority` is configured. The external identity must map to a provisioned Wings user (email on first sign-in, then the `iss|sub` subject).
* Everything except `/health/*`, the OpenAPI document, the UI's static files and the HMAC-signed Gitea webhook requires authentication.

## Authorization and tenant isolation
Enforced in Wings Git Manager independently of Gitea permissions (`AccessControl`):

| Role | Read | Change / recover | Merge approval, settings, overrides, backups | Delete repository |
|---|---|---|---|---|
| Read-only user | ✓ | | | |
| Developer / AI Agent Service | ✓ | ✓ | | |
| Repository administrator | ✓ | ✓ | ✓ | |
| Company administrator | ✓ | ✓ | ✓ | ✓ |
| System administrator | all companies | ✓ | ✓ | ✓ (+ purge remote with a verified backup) |

Effective role = max(company membership, repository permission), additionally capped by token scopes. Repositories of other companies return **404** (existence is not revealed) and are filtered from every list, search, dashboard, alert and audit query. Denials are written to the audit log and raise an alert when they spike. Service accounts can never apply policy overrides.

Each company maps to its own Gitea organization (private visibility); repositories are private. The Gitea token is held only by the server; the UI never talks to Gitea administration APIs.

## Git execution safety (`GitExecutor`)
* Allow-listed git sub-commands, arguments passed without a shell, no caller-controlled options: branch names, revisions, SHAs and paths are validated (`RepoPath`), pathspecs use `:(literal)`, and options such as `--upload-pack`, `--exec`, `--output`, `--ext-diff`, `--config`, `--git-dir` are rejected.
* Force pushes, `+refspecs`, `--mirror` and `--delete` pushes are refused at the executor level; Gitea branch protection disables force pushes server-side as a second layer.
* Repository content, configuration and hooks are untrusted: `core.hooksPath` points to an empty directory, `core.fsmonitor`, pagers, `credential.helper`, `core.sshCommand` are disabled, `--no-ext-diff --no-textconv` everywhere, system/global config replaced by a private one (`GIT_CONFIG_NOSYSTEM`, private `HOME`), `transfer.fsckObjects=true`, protocol allow-list (`https`, optional `http`/`file`). Imports of repositories whose `.git/config` defines executable settings (filters, fsmonitor, textconv, includeIf, …) are rejected.
* Credentials are never placed in remote URLs, git config, command lines or logs: they are injected per command as an `http.extraHeader` through `GIT_CONFIG_*` environment variables; redirects are disabled for authenticated calls; output is redacted. Generic-remote secrets are encrypted with ASP.NET Core Data Protection.
* Timeouts, output limits and process-tree kill on cancellation. Run the services as a dedicated unprivileged OS account (see systemd hardening and IIS app-pool identity in `deploy/`).

## Working-directory restrictions
Every directory is canonicalised (all symbolic links resolved) and must lie inside a configured workspace root **and** inside the owning company's folder. Links that escape the root are rejected. Stored paths are re-validated before each use. Repository-relative paths reject `..`, absolute paths, drive letters and `.git`.

## Secret scanning
Staged diffs are scanned before every commit (initial commit, change operations, manual commits, auto-commits of external changes): private keys, AWS/GitHub/GitLab/Slack/Stripe/Google/Anthropic/OpenAI keys, Azure storage keys, connection-string passwords, JWTs, generic secret assignments, and sensitive file types (`.env*`, `*.pem`, `*.pfx`, `id_*`, `.netrc`, …). Default mode **Block** (nothing committed, operation recoverable); `Warn`/`Off` per repository. Generated `.gitignore` files exclude secrets; existing `.gitignore` files are never overwritten.

## Other controls
* Structured audit log for every mutation, override, denial, recovery and backup action (actor, actor type, company, repository, correlation id, IP).
* Idempotency keys for mutating requests; problem+json errors without internal details; correlation ids on every response.
* Security headers (`nosniff`, `DENY` framing, no referrer), HSTS/HTTPS redirection in production, forwarded headers trusted only from configured proxies.
* Backups encrypted with AES-256-GCM (authenticated, truncation-detecting) before leaving the host; S3 server-side encryption and optional Object Lock.
* Repository deletion is soft (remote archived); purging the central repository requires a system administrator and a verified backup.
