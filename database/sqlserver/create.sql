CREATE TABLE [AISessionCommits] (
    [Id] uniqueidentifier NOT NULL,
    [SessionId] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NOT NULL,
    [Sha] nvarchar(450) NOT NULL,
    [CommitMetadataId] uniqueidentifier NULL,
    [Sequence] int NOT NULL,
    [IsCheckpointOnly] bit NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_AISessionCommits] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [AISessions] (
    [Id] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NOT NULL,
    [AgentId] nvarchar(450) NOT NULL,
    [InitiatingUser] nvarchar(450) NULL,
    [Title] nvarchar(450) NULL,
    [UserRequestRef] nvarchar(450) NULL,
    [State] int NOT NULL,
    [Isolation] int NOT NULL,
    [Branch] nvarchar(450) NOT NULL,
    [BaseBranch] nvarchar(450) NOT NULL,
    [WorkingDirectory] nvarchar(1024) NOT NULL,
    [WorktreeId] uniqueidentifier NULL,
    [StartingCommitSha] nvarchar(450) NOT NULL,
    [EndingCommitSha] nvarchar(450) NULL,
    [TestsExecuted] nvarchar(max) NOT NULL,
    [LastTestStatus] nvarchar(450) NULL,
    [CreatedAt] datetime2 NOT NULL,
    [LastActivityAt] datetime2 NOT NULL,
    [EndedAt] datetime2 NULL,
    CONSTRAINT [PK_AISessions] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [AuditLogs] (
    [Id] uniqueidentifier NOT NULL,
    [Timestamp] datetime2 NOT NULL,
    [UserId] nvarchar(450) NULL,
    [Actor] nvarchar(450) NOT NULL,
    [ActorType] nvarchar(450) NOT NULL,
    [CompanyId] uniqueidentifier NULL,
    [RepositoryId] uniqueidentifier NULL,
    [Action] nvarchar(450) NOT NULL,
    [Target] nvarchar(450) NULL,
    [Outcome] nvarchar(450) NOT NULL,
    [Details] nvarchar(max) NULL,
    [CorrelationId] nvarchar(450) NULL,
    [IpAddress] nvarchar(450) NULL,
    CONSTRAINT [PK_AuditLogs] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [BackgroundJobs] (
    [Id] uniqueidentifier NOT NULL,
    [Type] nvarchar(450) NOT NULL,
    [State] int NOT NULL,
    [RepositoryId] uniqueidentifier NULL,
    [PayloadJson] nvarchar(max) NOT NULL,
    [ResultJson] nvarchar(max) NULL,
    [Error] nvarchar(4000) NULL,
    [Progress] nvarchar(450) NULL,
    [Attempts] int NOT NULL,
    [MaxAttempts] int NOT NULL,
    [CreatedBy] nvarchar(450) NULL,
    [LockedBy] nvarchar(450) NULL,
    [LockedUntil] datetime2 NULL,
    [NotBefore] datetime2 NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [StartedAt] datetime2 NULL,
    [CompletedAt] datetime2 NULL,
    CONSTRAINT [PK_BackgroundJobs] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [BackupVerificationResults] (
    [Id] uniqueidentifier NOT NULL,
    [BackupId] uniqueidentifier NOT NULL,
    [StartedAt] datetime2 NOT NULL,
    [CompletedAt] datetime2 NULL,
    [Succeeded] bit NOT NULL,
    [ChecksumOk] bit NOT NULL,
    [BundleOk] bit NOT NULL,
    [FsckOk] bit NOT NULL,
    [RefsMatch] bit NOT NULL,
    [TestRestoreOk] bit NOT NULL,
    [Details] nvarchar(max) NULL,
    CONSTRAINT [PK_BackupVerificationResults] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [Branches] (
    [Id] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NOT NULL,
    [Name] nvarchar(450) NOT NULL,
    [HeadSha] nvarchar(450) NULL,
    [RemoteHeadSha] nvarchar(450) NULL,
    [IsSessionBranch] bit NOT NULL,
    [SessionId] uniqueidentifier NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_Branches] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [ChangeOperations] (
    [Id] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NOT NULL,
    [SessionId] uniqueidentifier NULL,
    [AgentId] nvarchar(450) NOT NULL,
    [InitiatingUser] nvarchar(450) NULL,
    [CallerUserId] uniqueidentifier NULL,
    [State] int NOT NULL,
    [RiskLevel] int NOT NULL,
    [RiskReasons] nvarchar(max) NOT NULL,
    [Description] nvarchar(4000) NOT NULL,
    [UserRequestRef] nvarchar(450) NULL,
    [WorkingDirectory] nvarchar(800) NOT NULL,
    [Branch] nvarchar(450) NOT NULL,
    [StartCommitSha] nvarchar(450) NOT NULL,
    [ResultCommitSha] nvarchar(450) NULL,
    [PreChangeCheckpointId] uniqueidentifier NULL,
    [PostChangeCheckpointId] uniqueidentifier NULL,
    [ScopePaths] nvarchar(max) NOT NULL,
    [ExcludedPaths] nvarchar(max) NOT NULL,
    [Authorized] bit NOT NULL,
    [IntentJson] nvarchar(max) NULL,
    [PolicyOverrideId] uniqueidentifier NULL,
    [PreservedBranch] nvarchar(450) NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CompletedAt] datetime2 NULL,
    [LeaseExpiresAt] datetime2 NOT NULL,
    [FailureReason] nvarchar(4000) NULL,
    [Version] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_ChangeOperations] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [Checkpoints] (
    [Id] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NOT NULL,
    [ProjectId] uniqueidentifier NOT NULL,
    [UserId] nvarchar(450) NULL,
    [SessionId] uniqueidentifier NULL,
    [AgentId] nvarchar(450) NULL,
    [ChangeOperationId] uniqueidentifier NULL,
    [Branch] nvarchar(450) NOT NULL,
    [CommitSha] nvarchar(450) NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [Description] nvarchar(4000) NULL,
    [Type] int NOT NULL,
    [SyncState] int NOT NULL,
    [BackupState] int NULL,
    [RemoteVerifiedAt] datetime2 NULL,
    [BackedUpAt] datetime2 NULL,
    [BuildStatus] nvarchar(450) NULL,
    [TestStatus] nvarchar(450) NULL,
    CONSTRAINT [PK_Checkpoints] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [CommitMetadata] (
    [Id] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NOT NULL,
    [Sha] nvarchar(64) NOT NULL,
    [Branch] nvarchar(450) NOT NULL,
    [Subject] nvarchar(450) NOT NULL,
    [Message] nvarchar(max) NOT NULL,
    [AuthorName] nvarchar(450) NOT NULL,
    [AuthorEmail] nvarchar(450) NOT NULL,
    [CommittedAt] datetime2 NOT NULL,
    [InitiatingUser] nvarchar(450) NULL,
    [SessionId] uniqueidentifier NULL,
    [AgentId] nvarchar(450) NULL,
    [ChangeOperationId] uniqueidentifier NULL,
    [UserRequestRef] nvarchar(450) NULL,
    [CommitType] nvarchar(450) NULL,
    [BuildStatus] nvarchar(450) NULL,
    [TestStatus] nvarchar(450) NULL,
    [IsAutomated] bit NOT NULL,
    [FilesChanged] int NOT NULL,
    [Additions] int NOT NULL,
    [Deletions] int NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_CommitMetadata] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [Companies] (
    [Id] uniqueidentifier NOT NULL,
    [Name] nvarchar(450) NOT NULL,
    [Slug] nvarchar(450) NOT NULL,
    [ExternalId] nvarchar(450) NULL,
    [GitNamespace] nvarchar(450) NULL,
    [IsActive] bit NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_Companies] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [IdempotencyRecords] (
    [Id] uniqueidentifier NOT NULL,
    [Key] nvarchar(200) NOT NULL,
    [Principal] nvarchar(100) NOT NULL,
    [Method] nvarchar(450) NOT NULL,
    [Path] nvarchar(450) NOT NULL,
    [RequestHash] nvarchar(450) NOT NULL,
    [StatusCode] int NOT NULL,
    [ResponseBody] nvarchar(max) NULL,
    [Completed] bit NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_IdempotencyRecords] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [MergeRequests] (
    [Id] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NOT NULL,
    [SourceBranch] nvarchar(450) NOT NULL,
    [TargetBranch] nvarchar(450) NOT NULL,
    [SourceSha] nvarchar(450) NULL,
    [TargetSha] nvarchar(450) NULL,
    [SessionId] uniqueidentifier NULL,
    [State] int NOT NULL,
    [Message] nvarchar(4000) NULL,
    [Conflicts] nvarchar(max) NOT NULL,
    [RequestedBy] nvarchar(450) NULL,
    [ApprovedBy] nvarchar(450) NULL,
    [ResultSha] nvarchar(450) NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CompletedAt] datetime2 NULL,
    CONSTRAINT [PK_MergeRequests] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [MonitoringAlerts] (
    [Id] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NULL,
    [CompanyId] uniqueidentifier NULL,
    [Key] nvarchar(450) NOT NULL,
    [Category] nvarchar(450) NOT NULL,
    [Severity] int NOT NULL,
    [Message] nvarchar(4000) NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [LastSeenAt] datetime2 NOT NULL,
    [ResolvedAt] datetime2 NULL,
    CONSTRAINT [PK_MonitoringAlerts] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [MutationLocks] (
    [Id] uniqueidentifier NOT NULL,
    [LockKey] nvarchar(200) NOT NULL,
    [Owner] nvarchar(450) NOT NULL,
    [AcquiredAt] datetime2 NOT NULL,
    [ExpiresAt] datetime2 NOT NULL,
    CONSTRAINT [PK_MutationLocks] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [NotificationEvents] (
    [Id] bigint NOT NULL IDENTITY,
    [Type] nvarchar(450) NOT NULL,
    [CompanyId] uniqueidentifier NULL,
    [RepositoryId] uniqueidentifier NULL,
    [PayloadJson] nvarchar(max) NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_NotificationEvents] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [PolicyDefinitions] (
    [Id] uniqueidentifier NOT NULL,
    [Scope] nvarchar(20) NOT NULL,
    [ScopeId] uniqueidentifier NULL,
    [Key] nvarchar(100) NOT NULL,
    [Enabled] bit NOT NULL,
    [SettingsJson] nvarchar(max) NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    [UpdatedBy] nvarchar(450) NULL,
    CONSTRAINT [PK_PolicyDefinitions] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [PolicyOverrides] (
    [Id] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NOT NULL,
    [ChangeOperationId] uniqueidentifier NULL,
    [PolicyKey] nvarchar(450) NOT NULL,
    [GrantedByUserId] uniqueidentifier NOT NULL,
    [GrantedBy] nvarchar(450) NOT NULL,
    [Reason] nvarchar(4000) NOT NULL,
    [Details] nvarchar(max) NULL,
    [CreatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_PolicyOverrides] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [PushQueue] (
    [Id] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NOT NULL,
    [Branch] nvarchar(450) NOT NULL,
    [CommitSha] nvarchar(450) NOT NULL,
    [ChangeOperationId] uniqueidentifier NULL,
    [WorkingDirectory] nvarchar(1024) NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [RetryCount] int NOT NULL,
    [LastError] nvarchar(4000) NULL,
    [State] int NOT NULL,
    [ExpectedRemoteHead] nvarchar(450) NULL,
    [NextAttemptAt] datetime2 NOT NULL,
    [LockedBy] nvarchar(450) NULL,
    [LockedUntil] datetime2 NULL,
    [CompletedAt] datetime2 NULL,
    CONSTRAINT [PK_PushQueue] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [RecoveryOperations] (
    [Id] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NOT NULL,
    [Type] int NOT NULL,
    [Status] int NOT NULL,
    [Strategy] int NOT NULL,
    [RequestedBy] nvarchar(450) NULL,
    [CallerUserId] uniqueidentifier NULL,
    [SessionId] uniqueidentifier NULL,
    [Branch] nvarchar(450) NULL,
    [CurrentCommitSha] nvarchar(450) NULL,
    [RecoveryCommitSha] nvarchar(450) NULL,
    [SelectedPaths] nvarchar(max) NOT NULL,
    [SelectedCommits] nvarchar(max) NOT NULL,
    [TargetDirectory] nvarchar(1024) NULL,
    [PlanJson] nvarchar(max) NOT NULL,
    [ApprovalRequired] bit NOT NULL,
    [ApprovedBy] nvarchar(450) NULL,
    [ApprovedAt] datetime2 NULL,
    [ResultCommitSha] nvarchar(450) NULL,
    [ResultBranch] nvarchar(450) NULL,
    [SafetyCheckpointId] uniqueidentifier NULL,
    [JobId] uniqueidentifier NULL,
    [Reason] nvarchar(4000) NULL,
    [Error] nvarchar(4000) NULL,
    [CreatedAt] datetime2 NOT NULL,
    [ExpiresAt] datetime2 NOT NULL,
    [CompletedAt] datetime2 NULL,
    CONSTRAINT [PK_RecoveryOperations] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [RepositoryBackups] (
    [Id] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NULL,
    [Kind] nvarchar(450) NOT NULL,
    [State] int NOT NULL,
    [StorageProvider] nvarchar(450) NOT NULL,
    [StorageKey] nvarchar(1024) NULL,
    [SizeBytes] bigint NOT NULL,
    [Sha256] nvarchar(450) NULL,
    [Encrypted] bit NOT NULL,
    [HeadSha] nvarchar(450) NULL,
    [RefsJson] nvarchar(max) NULL,
    [Trigger] nvarchar(450) NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CompletedAt] datetime2 NULL,
    [VerifiedAt] datetime2 NULL,
    [Error] nvarchar(4000) NULL,
    [RetentionLocked] bit NOT NULL,
    CONSTRAINT [PK_RepositoryBackups] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [SyncAttempts] (
    [Id] uniqueidentifier NOT NULL,
    [PushQueueItemId] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NOT NULL,
    [StartedAt] datetime2 NOT NULL,
    [FinishedAt] datetime2 NULL,
    [Outcome] int NOT NULL,
    [Error] nvarchar(4000) NULL,
    [RemoteHeadBefore] nvarchar(450) NULL,
    [RemoteHeadAfter] nvarchar(450) NULL,
    [Worker] nvarchar(450) NULL,
    CONSTRAINT [PK_SyncAttempts] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [SystemSettings] (
    [Id] uniqueidentifier NOT NULL,
    [Key] nvarchar(450) NOT NULL,
    [Value] nvarchar(max) NULL,
    [IsSecret] bit NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_SystemSettings] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [Users] (
    [Id] uniqueidentifier NOT NULL,
    [Email] nvarchar(450) NOT NULL,
    [DisplayName] nvarchar(450) NOT NULL,
    [ExternalSubject] nvarchar(450) NULL,
    [IsSystemAdministrator] bit NOT NULL,
    [IsServiceAccount] bit NOT NULL,
    [IsActive] bit NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [WorkingDirectories] (
    [Id] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NOT NULL,
    [Path] nvarchar(800) NOT NULL,
    [Kind] int NOT NULL,
    [Branch] nvarchar(450) NULL,
    [IsActive] bit NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [LastSeenAt] datetime2 NULL,
    CONSTRAINT [PK_WorkingDirectories] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [Worktrees] (
    [Id] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NOT NULL,
    [WorkingDirectoryId] uniqueidentifier NULL,
    [Path] nvarchar(1024) NOT NULL,
    [Branch] nvarchar(450) NOT NULL,
    [SessionId] uniqueidentifier NULL,
    [OwnerAgentId] nvarchar(450) NULL,
    [State] int NOT NULL,
    [BaseCommitSha] nvarchar(450) NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [LastActivityAt] datetime2 NOT NULL,
    [ReleasedAt] datetime2 NULL,
    CONSTRAINT [PK_Worktrees] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [CommitFileChanges] (
    [Id] uniqueidentifier NOT NULL,
    [CommitMetadataId] uniqueidentifier NOT NULL,
    [Path] nvarchar(1024) NOT NULL,
    [OldPath] nvarchar(1024) NULL,
    [Kind] int NOT NULL,
    [Additions] int NOT NULL,
    [Deletions] int NOT NULL,
    CONSTRAINT [PK_CommitFileChanges] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_CommitFileChanges_CommitMetadata_CommitMetadataId] FOREIGN KEY ([CommitMetadataId]) REFERENCES [CommitMetadata] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [Projects] (
    [Id] uniqueidentifier NOT NULL,
    [CompanyId] uniqueidentifier NOT NULL,
    [Name] nvarchar(450) NOT NULL,
    [Slug] nvarchar(450) NOT NULL,
    [ExternalId] nvarchar(450) NULL,
    [CreatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_Projects] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Projects_Companies_CompanyId] FOREIGN KEY ([CompanyId]) REFERENCES [Companies] ([Id]) ON DELETE NO ACTION
);
GO


CREATE TABLE [ApiTokens] (
    [Id] uniqueidentifier NOT NULL,
    [Name] nvarchar(450) NOT NULL,
    [TokenHash] nvarchar(450) NOT NULL,
    [Prefix] nvarchar(450) NOT NULL,
    [UserId] uniqueidentifier NOT NULL,
    [Scopes] nvarchar(max) NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [ExpiresAt] datetime2 NULL,
    [RevokedAt] datetime2 NULL,
    [LastUsedAt] datetime2 NULL,
    CONSTRAINT [PK_ApiTokens] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ApiTokens_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [CompanyMemberships] (
    [Id] uniqueidentifier NOT NULL,
    [CompanyId] uniqueidentifier NOT NULL,
    [UserId] uniqueidentifier NOT NULL,
    [Role] int NOT NULL,
    CONSTRAINT [PK_CompanyMemberships] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_CompanyMemberships_Companies_CompanyId] FOREIGN KEY ([CompanyId]) REFERENCES [Companies] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_CompanyMemberships_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [RepositoryPermissions] (
    [Id] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NOT NULL,
    [UserId] uniqueidentifier NOT NULL,
    [Role] int NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_RepositoryPermissions] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_RepositoryPermissions_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [Repositories] (
    [Id] uniqueidentifier NOT NULL,
    [CompanyId] uniqueidentifier NOT NULL,
    [ProjectId] uniqueidentifier NOT NULL,
    [Name] nvarchar(450) NOT NULL,
    [Slug] nvarchar(450) NOT NULL,
    [Description] nvarchar(4000) NULL,
    [DefaultBranch] nvarchar(450) NOT NULL,
    [State] int NOT NULL,
    [LastCommitSha] nvarchar(450) NULL,
    [LastCommitAt] datetime2 NULL,
    [LastPushAt] datetime2 NULL,
    [LastVerifiedSha] nvarchar(450) NULL,
    [RemoteStatus] int NOT NULL,
    [LatestCheckpointId] uniqueidentifier NULL,
    [StorageBytes] bigint NOT NULL,
    [HasUncommittedChanges] bit NOT NULL,
    [ExternalChangeDetectedAt] datetime2 NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    [ArchivedAt] datetime2 NULL,
    [DeletedAt] datetime2 NULL,
    [LastIndexedSha] nvarchar(450) NULL,
    CONSTRAINT [PK_Repositories] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Repositories_Companies_CompanyId] FOREIGN KEY ([CompanyId]) REFERENCES [Companies] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Repositories_Projects_ProjectId] FOREIGN KEY ([ProjectId]) REFERENCES [Projects] ([Id]) ON DELETE NO ACTION
);
GO


CREATE TABLE [RepositoryRemotes] (
    [Id] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NOT NULL,
    [Name] nvarchar(450) NOT NULL,
    [Provider] int NOT NULL,
    [Url] nvarchar(450) NOT NULL,
    [Owner] nvarchar(450) NULL,
    [RemoteRepositoryName] nvarchar(450) NULL,
    [ProviderRepositoryId] nvarchar(450) NULL,
    [CredentialUsername] nvarchar(450) NULL,
    [EncryptedCredential] nvarchar(max) NULL,
    [IsPrimary] bit NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_RepositoryRemotes] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_RepositoryRemotes_Repositories_RepositoryId] FOREIGN KEY ([RepositoryId]) REFERENCES [Repositories] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [RepositorySettings] (
    [Id] uniqueidentifier NOT NULL,
    [RepositoryId] uniqueidentifier NOT NULL,
    [AutoPush] bit NOT NULL,
    [RequireRemoteCheckpointBeforeChanges] bit NOT NULL,
    [CommitUncommittedChangesOnBegin] bit NOT NULL,
    [SecretScanMode] int NOT NULL,
    [ExcludedPathPatterns] nvarchar(max) NOT NULL,
    [ProtectedBranches] nvarchar(max) NOT NULL,
    [RequireMergeApproval] bit NOT NULL,
    [WorktreeRetentionDays] int NOT NULL,
    [BackupIntervalHours] int NOT NULL,
    [BackupRetentionCount] int NOT NULL,
    [WatchFileSystem] bit NOT NULL,
    [AutoCommitExternalChanges] bit NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_RepositorySettings] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_RepositorySettings_Repositories_RepositoryId] FOREIGN KEY ([RepositoryId]) REFERENCES [Repositories] ([Id]) ON DELETE CASCADE
);
GO


CREATE INDEX [IX_AISessionCommits_SessionId_Sequence] ON [AISessionCommits] ([SessionId], [Sequence]);
GO


CREATE INDEX [IX_AISessions_RepositoryId_State] ON [AISessions] ([RepositoryId], [State]);
GO


CREATE UNIQUE INDEX [IX_ApiTokens_TokenHash] ON [ApiTokens] ([TokenHash]);
GO


CREATE INDEX [IX_ApiTokens_UserId] ON [ApiTokens] ([UserId]);
GO


CREATE INDEX [IX_AuditLogs_CompanyId_Timestamp] ON [AuditLogs] ([CompanyId], [Timestamp]);
GO


CREATE INDEX [IX_AuditLogs_RepositoryId_Timestamp] ON [AuditLogs] ([RepositoryId], [Timestamp]);
GO


CREATE INDEX [IX_AuditLogs_Timestamp] ON [AuditLogs] ([Timestamp]);
GO


CREATE INDEX [IX_BackgroundJobs_State_NotBefore] ON [BackgroundJobs] ([State], [NotBefore]);
GO


CREATE INDEX [IX_BackupVerificationResults_BackupId] ON [BackupVerificationResults] ([BackupId]);
GO


CREATE UNIQUE INDEX [IX_Branches_RepositoryId_Name] ON [Branches] ([RepositoryId], [Name]);
GO


CREATE INDEX [IX_ChangeOperations_RepositoryId_State] ON [ChangeOperations] ([RepositoryId], [State]);
GO


CREATE INDEX [IX_ChangeOperations_WorkingDirectory] ON [ChangeOperations] ([WorkingDirectory]);
GO


CREATE INDEX [IX_Checkpoints_RepositoryId_CommitSha] ON [Checkpoints] ([RepositoryId], [CommitSha]);
GO


CREATE INDEX [IX_Checkpoints_RepositoryId_CreatedAt] ON [Checkpoints] ([RepositoryId], [CreatedAt]);
GO


CREATE INDEX [IX_CommitFileChanges_CommitMetadataId] ON [CommitFileChanges] ([CommitMetadataId]);
GO


CREATE INDEX [IX_CommitMetadata_RepositoryId_CommittedAt] ON [CommitMetadata] ([RepositoryId], [CommittedAt]);
GO


CREATE UNIQUE INDEX [IX_CommitMetadata_RepositoryId_Sha] ON [CommitMetadata] ([RepositoryId], [Sha]);
GO


CREATE INDEX [IX_CommitMetadata_SessionId] ON [CommitMetadata] ([SessionId]);
GO


CREATE INDEX [IX_Companies_ExternalId] ON [Companies] ([ExternalId]);
GO


CREATE UNIQUE INDEX [IX_Companies_Slug] ON [Companies] ([Slug]);
GO


CREATE UNIQUE INDEX [IX_CompanyMemberships_CompanyId_UserId] ON [CompanyMemberships] ([CompanyId], [UserId]);
GO


CREATE INDEX [IX_CompanyMemberships_UserId] ON [CompanyMemberships] ([UserId]);
GO


CREATE INDEX [IX_IdempotencyRecords_CreatedAt] ON [IdempotencyRecords] ([CreatedAt]);
GO


CREATE UNIQUE INDEX [IX_IdempotencyRecords_Principal_Key] ON [IdempotencyRecords] ([Principal], [Key]);
GO


CREATE INDEX [IX_MergeRequests_RepositoryId_State] ON [MergeRequests] ([RepositoryId], [State]);
GO


CREATE INDEX [IX_MonitoringAlerts_Key_ResolvedAt] ON [MonitoringAlerts] ([Key], [ResolvedAt]);
GO


CREATE UNIQUE INDEX [IX_MutationLocks_LockKey] ON [MutationLocks] ([LockKey]);
GO


CREATE INDEX [IX_NotificationEvents_CreatedAt] ON [NotificationEvents] ([CreatedAt]);
GO


CREATE UNIQUE INDEX [IX_PolicyDefinitions_Key_Scope_ScopeId] ON [PolicyDefinitions] ([Key], [Scope], [ScopeId]) WHERE [ScopeId] IS NOT NULL;
GO


CREATE INDEX [IX_PolicyOverrides_RepositoryId] ON [PolicyOverrides] ([RepositoryId]);
GO


CREATE UNIQUE INDEX [IX_Projects_CompanyId_ExternalId] ON [Projects] ([CompanyId], [ExternalId]) WHERE [ExternalId] IS NOT NULL;
GO


CREATE INDEX [IX_PushQueue_RepositoryId_Branch_State] ON [PushQueue] ([RepositoryId], [Branch], [State]);
GO


CREATE INDEX [IX_PushQueue_State_NextAttemptAt] ON [PushQueue] ([State], [NextAttemptAt]);
GO


CREATE INDEX [IX_RecoveryOperations_RepositoryId_CreatedAt] ON [RecoveryOperations] ([RepositoryId], [CreatedAt]);
GO


CREATE UNIQUE INDEX [IX_Repositories_CompanyId_Slug] ON [Repositories] ([CompanyId], [Slug]);
GO


CREATE INDEX [IX_Repositories_ProjectId] ON [Repositories] ([ProjectId]);
GO


CREATE INDEX [IX_RepositoryBackups_RepositoryId_CreatedAt] ON [RepositoryBackups] ([RepositoryId], [CreatedAt]);
GO


CREATE UNIQUE INDEX [IX_RepositoryPermissions_RepositoryId_UserId] ON [RepositoryPermissions] ([RepositoryId], [UserId]);
GO


CREATE INDEX [IX_RepositoryPermissions_UserId] ON [RepositoryPermissions] ([UserId]);
GO


CREATE INDEX [IX_RepositoryRemotes_RepositoryId] ON [RepositoryRemotes] ([RepositoryId]);
GO


CREATE UNIQUE INDEX [IX_RepositorySettings_RepositoryId] ON [RepositorySettings] ([RepositoryId]);
GO


CREATE INDEX [IX_SyncAttempts_PushQueueItemId] ON [SyncAttempts] ([PushQueueItemId]);
GO


CREATE UNIQUE INDEX [IX_SystemSettings_Key] ON [SystemSettings] ([Key]);
GO


CREATE UNIQUE INDEX [IX_Users_Email] ON [Users] ([Email]);
GO


CREATE INDEX [IX_Users_ExternalSubject] ON [Users] ([ExternalSubject]);
GO


CREATE INDEX [IX_WorkingDirectories_Path] ON [WorkingDirectories] ([Path]);
GO


CREATE INDEX [IX_WorkingDirectories_RepositoryId_Kind] ON [WorkingDirectories] ([RepositoryId], [Kind]);
GO


CREATE INDEX [IX_Worktrees_RepositoryId_State] ON [Worktrees] ([RepositoryId], [State]);
GO


CREATE INDEX [IX_Worktrees_SessionId] ON [Worktrees] ([SessionId]);
GO


