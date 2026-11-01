# Wings Code integration guide

Wings Code talks **only** to Wings Git Manager (never directly to Gitea). Use the typed SDK in `src/Wings.GitManager.Client` (package `Wings.GitManager.Client`), or call the REST API under `/api/v1` (OpenAPI: `/openapi/v1.json`).

## Identity

Wings Code authenticates as a **service account** with a scoped API token (`wgm_…`):

```http
POST /api/v1/users   { "email": "wings-code@acme.example", "displayName": "Wings Code", "isServiceAccount": true, "companyId": "<company>", "role": "Developer" }
POST /api/v1/tokens  { "name": "wings-code-prod", "userId": "<user>", "scopes": ["repos:read","repos:write","changes:write","recovery:execute"] }
```

The token's secret is returned once. Every request must also say **who the real user is** (`initiatingUser`) — commits are authored by `Wings AI Agent (<agentId>)`, committed by the service identity, and carry `Wings-Initiated-By` trailers, so automation is never attributed to a human.

```csharp
var git = new WingsGitManagerClient(new WingsGitManagerClientOptions
{
    BaseAddress = new Uri("https://git-manager.example.com/"),
    ApiToken = config["WingsGitManager:Token"]!,
});
```

All mutating SDK calls send an `Idempotency-Key`; transient failures (network, 502/503/504) are retried with the same key, so a retry can never create a second repository, commit or recovery.

## 1. When Wings Code creates a project

```csharp
var repo = await git.Repositories.CreateAsync(new CreateRepositoryRequest
{
    CompanyId = companyId,
    ProjectExternalId = project.Id,          // idempotency: one repository per (company, project)
    ProjectName = project.Name,
    GitIgnoreTemplates = ["dotnet", "angular"],
});
// repo.RepositoryId, repo.WorkingDirectory, repo.RemoteUrl, repo.CommitSha,
// repo.Durability == RemoteSynchronized once the initial commit is verified on Gitea
```

Write the project's files into `repo.WorkingDirectory` (or create the repository first and let Wings Code generate into it). Existing repositories are adopted with `ImportAsync` (local path or remote URL; history preserved).

## 2. Every code modification

```csharp
var op = await git.Changes.BeginAsync(repo.RepositoryId, new BeginChangeRequest
{
    AgentId = "coder-1",
    SessionId = session?.Id,                  // optional: work in the session's isolated worktree
    Description = "Add customer ledger screen",
    InitiatingUser = user.Email,
    UserRequestRef = conversationMessageId,
    Intent = new ChangeIntent { FilesToAdd = ["src/app/ledger/ledger.component.ts"], FilesToModify = ["src/app/app.routes.ts"] },
});
// op.WorkingDirectory — the ONLY directory the agent may modify
// op.Risk — Low/Medium/High with reasons; op.PreChangeCheckpoint — safe starting point

/* ... the agent edits files inside op.WorkingDirectory ... */

var result = await git.Changes.CompleteAsync(repo.RepositoryId, op.OperationId, new CompleteChangeRequest
{
    Summary = "Add customer ledger screen",
    BuildStatus = "succeeded",
    TestStatus = "passed",
    TestsExecuted = ["LedgerComponent should render"],
});
// result.CommitSha, result.Files, result.Durability, result.RecoveryOptions
```

Or use `git.Changes.RunAsync(repoId, begin, async op => { …edit…; return new CompleteChangeRequest { … }; })`, which aborts (preserving the work on a recovery branch) if the edit throws.

Error codes Wings Code should handle:

| Code | Meaning | What to do |
|---|---|---|
| `high_risk_blocked` / `remote_checkpoint_required` (422) | The starting point could not be verified on Gitea. | Tell the user; retry later. A repository administrator can supply `Override.Reason` (audited). |
| `workspace_busy` (409) | Another operation holds the working directory. | Start an AI session (own worktree) or wait. |
| `secrets_detected` (422) | Credentials in the change; nothing committed. | Remove them, call complete again (or abort). |
| `expected_head_mismatch` (409) | The branch moved. | Re-read state, begin again. |
| `working_directory_missing` (409) | Local project deleted. | Run the clone recovery (below). |

`Durability` tells you how safe the result is: report "saved to the central server" only for `RemoteSynchronized`/`IndependentlyBackedUp`. `RemotePushPending` means it is committed locally and queued; the worker retries with exponential backoff and the UI shows it.

## 3. Several AI agents on one project

```csharp
var s = await git.Sessions.StartAsync(new StartSessionRequest { RepositoryId = repoId, AgentId = "coder-2", InitiatingUser = user.Email, Title = "Reports" });
// s.WorkingDirectory = …/<project>/worktrees/session-<id>, s.Branch = ai/session-<id>
// pass SessionId in every BeginChangeRequest; another agent cannot use this session
await git.Sessions.EndAsync(s.Id, new EndSessionRequest { MergeIntoBase = true, ReleaseWorktree = true });
```

Merging into a protected branch on behalf of an agent creates a merge request that a repository administrator approves (`POST /repositories/{id}/merge-requests/{mrId}/approve`). Conflicts are reported (`state = Conflict`, `conflicts = [...]`) without changing anything.

## 4. Conversational recovery

Map the user's words to one API call, show the result, ask for approval, then execute:

```csharp
var c = await git.Recovery.CandidatesAsync(repoId, new RecoveryCandidatesRequest { Query = userMessage });
// c.Interpretation  — what was understood (type, time window, keywords) → say it back to the user
// c.Candidates      — ranked recovery points with descriptions, durability and suggested paths/commits
// c.Ambiguous       — true ⇒ list the candidates and let the user choose
// c.ChangeSummary   — for "show me what changed…" requests (narrative + commits + files)

var pick = c.Candidates[0];
var preview = await git.Recovery.PreviewAsync(repoId, new RecoveryRequest
{
    Type = pick.SuggestedType,
    RecoveryPoint = pick.RecoveryPoint,
    Paths = pick.SuggestedPaths,
    CommitShas = pick.SuggestedCommits,
    SessionId = pick.SessionId,
});
// Show: preview.Explanation, preview.ExpectedResult, preview.AffectedFiles (+/-), preview.ExpectedConflicts,
//       preview.UncommittedChanges, preview.Destructive. Ask the user to confirm when preview.ApprovalRequired.
var done = await git.Recovery.ExecuteAsync(repoId, preview, approved: userConfirmed);
// done.Status, done.ResultCommitSha, done.ResultBranch, done.SafetyCheckpointId
```

| User says | Interpretation → API |
|---|---|
| "Undo the changes you made in the last session." | `RevertSession` of the latest session (`Sessions.RecoveryPreviewAsync`). |
| "Restore the invoice calculation code from yesterday." | `RestoreFile` with paths matching *invoice*, *calculation* as of the end of yesterday. |
| "The customer portal stopped working after your changes. Go back to the previous version." | `RevertCommits` of the latest operation(s); recovery point = verified pre-change checkpoint. |
| "Recover the project I accidentally deleted." / "Restore my project from Gitea." | `CloneProject` — asynchronous; poll `Recovery.GetAsync` or `WaitForJobAsync(done.JobId)`. |
| "Show me what changed in the last three hours." | `ChangeSummary` (no recovery). |
| "Bring back the old customer ledger screen without removing my new API code." | `RestoreFile` limited to ledger paths; other files untouched. |
| "Restore the project to the version before the last refactoring." | `RestoreProject` at the parent of the last refactoring commit; default strategy is a side-by-side recovery branch/worktree. |
| "Recover the deleted customer ledger page." | `RestoreDeleted` (`Recovery.DeletedFilesAsync` lists candidates). |

Rules the service enforces: nothing destructive runs without `approved = true`; dirty working trees are saved as a safety checkpoint first; branches are never reset and history is never rewritten (restores and reverts are new commits); a preview expires (default 60 min) and is rejected if the branch moved since.

## 5. Events

Subscribe to `/hubs/notifications` (SignalR, `access_token` query parameter) to receive `change.completed`, `sync.failed`, `sync.conflict`, `change.blocked`, `recovery.succeeded`, `backup.verified`, `alert.raised`, … for the companies the token can see.
