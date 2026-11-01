using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Core.Policies;
using Wings.GitManager.Core.Safety;
using Wings.GitManager.Infrastructure.Data;

namespace Wings.GitManager.Infrastructure.Services;

/// <summary>Companies, projects, users, memberships, repository permissions, API tokens, policies and audit queries.</summary>
public sealed class AdminService(GitManagerDbContext db, AccessControl access, AuditService audit)
{
    public const string TokenPrefix = "wgm_";

    public async Task<IReadOnlyList<CompanyDto>> CompaniesAsync(CancellationToken ct)
    {
        var ids = await access.AccessibleCompanyIdsAsync(ct);
        var q = db.Companies.AsNoTracking();
        if (ids is not null) q = q.Where(c => ids.Contains(c.Id));
        return await q.OrderBy(c => c.Name).Select(c => new CompanyDto(c.Id, c.Name, c.Slug, c.ExternalId, c.GitNamespace, c.IsActive,
            db.Repositories.Count(r => r.CompanyId == c.Id && r.State != RepositoryState.Deleted))).ToListAsync(ct);
    }

    public async Task<CompanyDto> CreateCompanyAsync(CreateCompanyRequest req, CancellationToken ct)
    {
        access.RequireSystemAdministrator();
        if (string.IsNullOrWhiteSpace(req.Name)) throw new GitManagerException(ErrorCodes.Validation, "Name is required.");
        var slug = RepoPath.Slugify(req.Slug ?? req.Name);
        if (req.ExternalId is not null && await db.Companies.FirstOrDefaultAsync(c => c.ExternalId == req.ExternalId, ct) is { } existing)
            return new CompanyDto(existing.Id, existing.Name, existing.Slug, existing.ExternalId, existing.GitNamespace, existing.IsActive, 0);
        if (await db.Companies.AnyAsync(c => c.Slug == slug, ct)) throw new ConflictException("company_exists", $"Company slug '{slug}' is taken.");
        var c = new Company { Name = req.Name.Trim(), Slug = slug, ExternalId = req.ExternalId };
        db.Companies.Add(c);
        audit.Record("company.created", null, c.Id, c.Slug);
        await db.SaveChangesAsync(ct);
        return new CompanyDto(c.Id, c.Name, c.Slug, c.ExternalId, c.GitNamespace, c.IsActive, 0);
    }

    public async Task<IReadOnlyList<ProjectDto>> ProjectsAsync(Guid? companyId, CancellationToken ct)
    {
        var ids = await access.AccessibleCompanyIdsAsync(ct);
        var q = db.Projects.AsNoTracking();
        if (ids is not null) q = q.Where(p => ids.Contains(p.CompanyId));
        if (companyId is not null) q = q.Where(p => p.CompanyId == companyId);
        return await q.OrderBy(p => p.Name).Select(p => new ProjectDto(p.Id, p.CompanyId, p.Name, p.Slug, p.ExternalId,
            db.Repositories.Count(r => r.ProjectId == p.Id && r.State != RepositoryState.Deleted))).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<UserDto>> UsersAsync(Guid? companyId, CancellationToken ct)
    {
        if (companyId is { } cid) await access.RequireCompanyAsync(cid, WorkspaceRole.CompanyAdministrator, Scopes.Admin, ct);
        else access.RequireSystemAdministrator();
        var q = db.Users.AsNoTracking().Include(u => u.Memberships).ThenInclude(m => m.Company).AsQueryable();
        if (companyId is not null) q = q.Where(u => u.Memberships.Any(m => m.CompanyId == companyId));
        return (await q.OrderBy(u => u.Email).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<UserDto> CreateUserAsync(CreateUserRequest req, CancellationToken ct)
    {
        if (req.CompanyId is { } cid && !req.IsSystemAdministrator) await access.RequireCompanyAsync(cid, WorkspaceRole.CompanyAdministrator, Scopes.Admin, ct);
        else access.RequireSystemAdministrator();
        var email = req.Email.Trim().ToLowerInvariant();
        if (!email.Contains('@')) throw new GitManagerException(ErrorCodes.Validation, "A valid email is required.");
        var user = await db.Users.Include(u => u.Memberships).FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            user = new User { Email = email, DisplayName = req.DisplayName, IsSystemAdministrator = req.IsSystemAdministrator, IsServiceAccount = req.IsServiceAccount };
            db.Users.Add(user);
        }
        if (req.CompanyId is { } companyId && req.Role is { } role && user.Memberships.All(m => m.CompanyId != companyId))
            db.CompanyMemberships.Add(new CompanyMembership { CompanyId = companyId, UserId = user.Id, Role = role });
        audit.Record("user.created", null, req.CompanyId, email, details: new { req.Role, req.IsServiceAccount, req.IsSystemAdministrator });
        await db.SaveChangesAsync(ct);
        return ToDto(await db.Users.AsNoTracking().Include(u => u.Memberships).ThenInclude(m => m.Company).FirstAsync(u => u.Id == user.Id, ct));
    }

    public async Task SetMembershipAsync(Guid userId, SetMembershipRequest req, CancellationToken ct)
    {
        await access.RequireCompanyAsync(req.CompanyId, WorkspaceRole.CompanyAdministrator, Scopes.Admin, ct);
        var m = await db.CompanyMemberships.FirstOrDefaultAsync(x => x.UserId == userId && x.CompanyId == req.CompanyId, ct);
        if (m is null) db.CompanyMemberships.Add(new CompanyMembership { UserId = userId, CompanyId = req.CompanyId, Role = req.Role });
        else m.Role = req.Role;
        audit.Record("membership.set", null, req.CompanyId, userId.ToString(), details: req);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveMembershipAsync(Guid userId, Guid companyId, CancellationToken ct)
    {
        await access.RequireCompanyAsync(companyId, WorkspaceRole.CompanyAdministrator, Scopes.Admin, ct);
        await db.CompanyMemberships.Where(x => x.UserId == userId && x.CompanyId == companyId).ExecuteDeleteAsync(ct);
        audit.Record("membership.removed", null, companyId, userId.ToString());
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<RepositoryPermissionDto>> PermissionsAsync(Guid repositoryId, CancellationToken ct)
    {
        await access.RequireRepositoryAsync(repositoryId, RepoAction.Read, ct, track: false);
        return await db.RepositoryPermissions.AsNoTracking().Where(p => p.RepositoryId == repositoryId)
            .Select(p => new RepositoryPermissionDto(p.Id, p.RepositoryId, p.UserId, p.User!.Email, p.Role)).ToListAsync(ct);
    }

    public async Task SetPermissionAsync(Guid repositoryId, SetRepositoryPermissionRequest req, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Administer, ct);
        if (req.Role == WorkspaceRole.CompanyAdministrator) throw new GitManagerException(ErrorCodes.Validation, "Company administrator is granted through company membership.");
        var p = await db.RepositoryPermissions.FirstOrDefaultAsync(x => x.RepositoryId == repositoryId && x.UserId == req.UserId, ct);
        if (p is null) db.RepositoryPermissions.Add(new RepositoryPermission { RepositoryId = repositoryId, UserId = req.UserId, Role = req.Role });
        else p.Role = req.Role;
        audit.Record("permission.set", repo.Id, repo.CompanyId, req.UserId.ToString(), details: req);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemovePermissionAsync(Guid repositoryId, Guid userId, CancellationToken ct)
    {
        var repo = await access.RequireRepositoryAsync(repositoryId, RepoAction.Administer, ct);
        await db.RepositoryPermissions.Where(x => x.RepositoryId == repositoryId && x.UserId == userId).ExecuteDeleteAsync(ct);
        audit.Record("permission.removed", repo.Id, repo.CompanyId, userId.ToString());
        await db.SaveChangesAsync(ct);
    }

    // ---- API tokens (stored as SHA-256 hashes; the secret is shown once) ----

    public async Task<CreatedApiTokenDto> CreateTokenAsync(CreateApiTokenRequest req, CancellationToken ct)
    {
        var ownerId = req.UserId ?? access.Caller.UserId ?? throw new ForbiddenException("Tokens must belong to a user.");
        if (ownerId != access.Caller.UserId) access.RequireSystemAdministrator();
        var scopes = req.Scopes.Length == 0 ? Scopes.AgentService : req.Scopes;
        if (scopes.Except(Scopes.All).Any()) throw new GitManagerException(ErrorCodes.Validation, $"Unknown scope. Valid scopes: {string.Join(", ", Scopes.All)}");
        if (scopes.Contains(Scopes.Admin) && !access.Caller.IsSystemAdministrator) throw new ForbiddenException("Only system administrators can grant the admin scope.");
        var (secret, token) = await IssueTokenAsync(db, ownerId, req.Name, scopes, req.ExpiresInDays is { } d ? DateTime.UtcNow.AddDays(d) : null, ct);
        audit.Record("token.created", null, null, token.Prefix, details: new { req.Name, scopes, ownerId });
        await db.SaveChangesAsync(ct);
        return new CreatedApiTokenDto(ToDto(token), secret);
    }

    public static async Task<(string Secret, ApiToken Token)> IssueTokenAsync(GitManagerDbContext db, Guid userId, string name, IEnumerable<string> scopes, DateTime? expires, CancellationToken ct)
    {
        var secret = TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var token = new ApiToken { Name = name, UserId = userId, Scopes = scopes.ToList(), TokenHash = Hash(secret), Prefix = secret[..12], ExpiresAt = expires };
        db.ApiTokens.Add(token);
        await Task.CompletedTask;
        return (secret, token);
    }

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    public async Task<IReadOnlyList<ApiTokenDto>> TokensAsync(CancellationToken ct)
    {
        var q = db.ApiTokens.AsNoTracking();
        if (!access.Caller.IsSystemAdministrator) q = q.Where(t => t.UserId == access.Caller.UserId);
        return (await q.OrderByDescending(t => t.CreatedAt).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task RevokeTokenAsync(Guid id, CancellationToken ct)
    {
        var t = await db.ApiTokens.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Token");
        if (t.UserId != access.Caller.UserId) access.RequireSystemAdministrator();
        t.RevokedAt = DateTime.UtcNow;
        audit.Record("token.revoked", null, null, t.Prefix);
        await db.SaveChangesAsync(ct);
    }

    // ---- Policies ----

    public async Task<IReadOnlyList<PolicyDto>> PoliciesAsync(CancellationToken ct)
    {
        var ids = await access.AccessibleCompanyIdsAsync(ct);
        var list = await db.PolicyDefinitions.AsNoTracking().OrderBy(p => p.Key).ThenBy(p => p.Scope).ToListAsync(ct);
        if (ids is not null)
        {
            var repoIds = await (await access.VisibleRepositoriesAsync(ct)).Select(r => r.Id).ToListAsync(ct);
            var projectIds = await db.Projects.Where(p => ids.Contains(p.CompanyId)).Select(p => p.Id).ToListAsync(ct);
            list = list.Where(p => p.Scope == "global" || p.ScopeId is { } s && (ids.Contains(s) || repoIds.Contains(s) || projectIds.Contains(s))).ToList();
        }
        return list.Select(ToDto).ToList();
    }

    public async Task<PolicyDto> UpsertPolicyAsync(UpsertPolicyRequest req, CancellationToken ct)
    {
        var scope = req.Scope.ToLowerInvariant();
        switch (scope)
        {
            case "global": access.RequireSystemAdministrator(); break;
            case "company": await access.RequireCompanyAsync(req.ScopeId ?? Guid.Empty, WorkspaceRole.CompanyAdministrator, Scopes.Admin, ct); break;
            case "project":
                var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == req.ScopeId, ct) ?? throw new NotFoundException("Project");
                await access.RequireCompanyAsync(project.CompanyId, WorkspaceRole.CompanyAdministrator, Scopes.Admin, ct); break;
            case "repository": await access.RequireRepositoryAsync(req.ScopeId ?? Guid.Empty, RepoAction.Administer, ct); break;
            default: throw new GitManagerException(ErrorCodes.Validation, "Scope must be global, company, project or repository.");
        }
        if (req.Key == HighRiskPolicySettings.Key)
        {
            try { _ = Json.Deserialize<HighRiskPolicySettings>(req.SettingsJson) ?? throw new FormatException(); }
            catch (Exception) { throw new GitManagerException(ErrorCodes.Validation, "SettingsJson is not a valid high-risk policy."); }
        }
        var p = await db.PolicyDefinitions.FirstOrDefaultAsync(x => x.Key == req.Key && x.Scope == scope && x.ScopeId == req.ScopeId, ct);
        if (p is null) db.PolicyDefinitions.Add(p = new PolicyDefinition { Key = req.Key, Scope = scope, ScopeId = req.ScopeId });
        p.Enabled = req.Enabled;
        p.SettingsJson = req.SettingsJson;
        p.UpdatedAt = DateTime.UtcNow;
        p.UpdatedBy = access.Caller.Name;
        audit.Record("policy.updated", scope == "repository" ? req.ScopeId : null, scope == "company" ? req.ScopeId : null, $"{req.Key}@{scope}", details: req);
        await db.SaveChangesAsync(ct);
        return ToDto(p);
    }

    public async Task<PagedResult<AuditLogDto>> AuditAsync(Guid? companyId, Guid? repositoryId, string? action, string? actor, string? outcome, DateTime? from, DateTime? to, int page, int pageSize, CancellationToken ct)
    {
        var q = db.AuditLogs.AsNoTracking().AsQueryable();
        if (repositoryId is { } rid) { await access.RequireRepositoryAsync(rid, RepoAction.Read, ct, track: false); q = q.Where(a => a.RepositoryId == rid); }
        else if (!access.Caller.IsSystemAdministrator)
        {
            var companies = await access.AccessibleCompanyIdsAsync(ct) ?? [];
            var repoIds = await (await access.VisibleRepositoriesAsync(ct)).Select(r => r.Id).ToListAsync(ct);
            q = q.Where(a => (a.CompanyId != null && companies.Contains(a.CompanyId.Value)) || (a.RepositoryId != null && repoIds.Contains(a.RepositoryId.Value)));
        }
        if (companyId is not null) q = q.Where(a => a.CompanyId == companyId);
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action.StartsWith(action));
        if (!string.IsNullOrWhiteSpace(actor)) q = q.Where(a => a.Actor.Contains(actor));
        if (!string.IsNullOrWhiteSpace(outcome)) q = q.Where(a => a.Outcome == outcome);
        if (from is not null) q = q.Where(a => a.Timestamp >= from);
        if (to is not null) q = q.Where(a => a.Timestamp < to);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(a => a.Timestamp).Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<AuditLogDto>(items.Select(a => new AuditLogDto
        {
            Id = a.Id, Timestamp = a.Timestamp, UserId = a.UserId, Actor = a.Actor, ActorType = a.ActorType, CompanyId = a.CompanyId, RepositoryId = a.RepositoryId,
            Action = a.Action, Target = a.Target, Outcome = a.Outcome, Details = a.Details, CorrelationId = a.CorrelationId
        }).ToList(), total, page, pageSize);
    }

    public async Task<MeDto> MeAsync(CancellationToken ct)
    {
        var c = access.Caller;
        var memberships = c.UserId is { } uid
            ? await db.CompanyMemberships.AsNoTracking().Include(m => m.Company).Where(m => m.UserId == uid).Select(m => new MembershipDto(m.CompanyId, m.Company!.Name, m.Role)).ToListAsync(ct)
            : [];
        var email = c.UserId is { } id ? await db.Users.Where(u => u.Id == id).Select(u => u.Email).FirstOrDefaultAsync(ct) : null;
        return new MeDto(c.UserId ?? Guid.Empty, email ?? c.Name, c.Name, c.IsSystemAdministrator, memberships, c.Scopes.ToList());
    }

    private static UserDto ToDto(User u) => new(u.Id, u.Email, u.DisplayName, u.IsSystemAdministrator, u.IsServiceAccount, u.IsActive,
        u.Memberships.Select(m => new MembershipDto(m.CompanyId, m.Company?.Name ?? "", m.Role)).ToList());

    private static ApiTokenDto ToDto(ApiToken t) => new(t.Id, t.Name, t.Prefix, t.UserId, t.Scopes.ToArray(), t.CreatedAt, t.ExpiresAt, t.RevokedAt, t.LastUsedAt);

    private static PolicyDto ToDto(PolicyDefinition p) => new()
    {
        Id = p.Id, Scope = p.Scope, ScopeId = p.ScopeId, Key = p.Key, Enabled = p.Enabled, SettingsJson = p.SettingsJson, UpdatedAt = p.UpdatedAt, UpdatedBy = p.UpdatedBy
    };
}
