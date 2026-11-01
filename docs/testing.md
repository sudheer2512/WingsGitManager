# Testing

```bash
dotnet test Wings.GitManager.slnx
```

* **Unit tests** (`tests/Wings.GitManager.UnitTests`, 106): path/ref validation, glob semantics, secret scanner, high-risk policy engine, commit message builder and trailers, conversational intent parser, AES-GCM backup encryption (tamper/truncation/wrong key), git executor guards (force push, disallowed commands/options, hooks never run, redaction), git output parsers.
* **Integration tests** (`tests/Wings.GitManager.IntegrationTests`, 32): the real API in-process (`WebApplicationFactory`), SQLite, real `git`, file-system bare remotes, encrypted file-system backups, real background workers, the typed C# SDK — each run in a disposable temporary directory.
* **Gitea tests** (4 of the 32) run against a real Gitea server (`scripts/gitea-dev.sh init && start`, Gitea 28.2.0) and are skipped automatically when it is not running. They create a uniquely prefixed organization per run and delete it afterwards.

## Required scenarios → tests

| # | Scenario | Test |
|---|---|---|
| 1 | Creating a new repository | `Create_repository_initializes_commits_pushes_and_verifies`, `Provisions_private_repository_in_company_organization_and_verifies_commits` (Gitea) |
| 2 | Importing an existing repository | `Import_existing_local_repository_preserves_history_and_branches`, `Imports_remote_repository_with_history` (Gitea), `Import_rejects_repository_with_executable_configuration` |
| 3 | Creating and pushing the initial commit | `Create_repository_initializes_commits_pushes_and_verifies` |
| 4 | Modifying a source file | `Change_operation_commits_modifications_additions_and_deletions` |
| 5 | Adding and deleting files | same |
| 6 | Pre-change checkpoints | `Pre_change_checkpoint_commits_unrelated_uncommitted_work_separately`, `High_risk_change_is_blocked_until_…` |
| 7 | Post-change checkpoints | `Change_operation_commits_…` (checkpoint list, durability, agent) |
| 8 | High-risk modification blocking | `High_risk_change_is_blocked_until_the_checkpoint_is_verified_on_the_remote` (+ audited override, service account cannot override) |
| 9 | Remote checkpoint verification | same + Gitea commit verification in the Gitea tests |
| 10 | Failure of the remote Git server | `Commits_are_queued_when_the_remote_fails_and_pushed_when_it_returns` |
| 11 | Persistent push retries | same (RetryScheduled → Succeeded, sync attempts recorded) |
| 12 | Concurrent AI sessions | `Concurrent_ai_sessions_work_in_isolated_worktrees` |
| 13 | Isolated worktree behaviour | same (ownership 403, workspace busy 409, no cross-commits) |
| 14 | Branch conflicts | `Merges_detect_conflicts_and_respect_approval_policy` |
| 15 | AI session rollback | `Reverting_a_session_preserves_unrelated_changes` |
| 16 | Selective source-file recovery | `Restore_file_preserves_unrelated_and_uncommitted_changes`, `Deleted_files_can_be_listed_and_restored` |
| 17 | Recovery after local project deletion | `Deleted_local_project_is_recovered_from_the_central_repository` |
| 18 | Recovery after central server failure | `Backups_are_verified_and_restore_the_central_repository_after_server_loss`, `Repository_deleted_from_gitea_is_restored_from_backup` (Gitea) |
| 19 | Unauthorized repository access | `Other_companies_and_insufficient_roles_are_denied`, `Revoked_tokens_are_rejected` |
| 20 | Path traversal | `Path_traversal_and_foreign_directories_are_rejected` (incl. symlink escape), unit tests |
| 21 | Secret scanning | `Secret_scanning_blocks_commits_and_operation_can_be_completed_after_fixing`, unit tests |
| 22 | Unsafe force pushes | `Diverged_remote_is_reported_as_conflict_without_force_pushing`, `Session_branches_sync_to_gitea_and_force_push_is_rejected_by_the_server` (Gitea), executor unit tests |
| 23 | Backup verification | `Backups_are_verified_and_restore_…` (verified, encrypted at rest, tamper detected) |
| 24 | Interrupted operations | `Interrupted_operations_can_be_reconciled` (lease expiry + crash during commit), `Abort_preserves_work_and_resets_workspace` |
| 25 | Idempotent API requests | `Idempotency_keys_replay_responses_and_reject_reuse`, repeated create/complete calls |
| 26 | Large repositories | `Handles_large_repositories` (3,000 files, 300-file change) |
| 27 | Correct Git status after recovery | asserted clean `git status` in every recovery test |
| 28 | Conversational recovery API | `Conversational_recovery_finds_sessions_files_and_summaries`, `Deleted_local_project_…` (candidates), parser unit tests |
| 29 | Unrelated changes preserved during AI rollback | `Reverting_a_session_preserves_unrelated_changes` |
| 30 | Uncommitted changes during restoration | `Restore_file_preserves_unrelated_and_uncommitted_changes` (safety checkpoint) |
| — | Monitoring alerts, file-system watching | `Monitoring_raises_and_resolves_alerts`, `File_watcher_detects_and_checkpoints_external_changes` |

## Not covered by automated tests (yet)

* SQL Server execution of `database/sqlserver/create.sql` (generated from the EF model; tests use SQLite), Docker images/Compose, IIS/Windows Service, systemd units.
* S3 backup store against a real S3-compatible service.
* OIDC sign-in with a real identity provider; Gitea → Wings webhooks.
* The Angular UI has no automated tests; it was clicked through manually in a browser (destructive recoveries, merge approval and token creation were exercised only up to the preview/form step).
