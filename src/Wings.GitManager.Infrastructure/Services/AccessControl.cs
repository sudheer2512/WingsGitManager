using Microsoft.EntityFrameworkCore;
using Wings.GitManager.Contracts;
using Wings.GitManager.Core;
using Wings.GitManager.Core.Entities;
using Wings.GitManager.Infrastructure.Data;

namespace Wings.GitManager.Infrastructure.Services;

/// <summary>
/// Company isolation and repository authorization, enforced here independently of Gitea permissions.
/// Effective role = highest of the company membership and the repository permission; token scopes cap it.
/// </summary>
public sealed class AccessControl(GitManagerDbContext db, ICallerContext caller, AuditService audit)
{
    private Dictionary<Guid, WorkspaceRole>? _companyRoles;

    public ICallerContext Caller => caller;

    private async Task<Dictionary<Guid, WorkspaceRole>> CompanyRolesAsync(CancellationToken ct)
    {
        if (_companyRoles is not null) return _companyRoles;
        _companyRoles = caller.UserId is { } uid
            ? await db.CompanyMemberships.AsNoTracking().Where(m => m.UserId == uid).ToDictionaryAsync(m => m.CompanyId, m => m.Role, ct)
            : [];
        return _companyRoles;
    }

    public async Task<IReadOnlyCollection<Guid>?> AccessibleCompanyIdsAsync(CancellationToken ct)
    {
        if (caller.IsSystemAdministrator) return null; // all
        var roles = await CompanyRolesAsync(ct);
        var viaRepo = caller.UserId is { } uid
            ? await db.RepositoryPermissions.AsNoTracking().Where(p => p.UserId == uid)
                .Join(db.Repositories, p => p.RepositoryId, r => r.Id, (p, r) => r.CompanyId).Distinct().ToListAsync(ct)
            : [];
        return roles.Keys.Concat(viaRepo).Distinct().ToList();
    }

    /// <summary>Repositories visible to the caller (company membership or explicit repository permission).</summary>
    public async Task<IQueryable<Repository>> VisibleRepositoriesAsync(CancellationToken ct)
    {
        var q = db.Repositories.Where(r => r.State != RepositoryState.Deleted);
        if (caller.IsSystemAdministrator) return q;
        RequireScope(Scopes.ReposRead);
        var companies = (await CompanyRolesAsync(ct)).Keys.ToList();
        var uid = caller.UserId ?? Guid.Empty;
        return q.Where(r => companies.Contains(r.CompanyId) || db.RepositoryPermissions.Any(p => p.RepositoryId == r.Id && p.UserId == uid));
    }

    public async Task<WorkspaceRole?> EffectiveRoleAsync(Repository repo, CancellationToken ct)
    {
        if (caller.IsSystemAdministrator) return WorkspaceRole.CompanyAdministrator;
        var roles = await CompanyRolesAsync(ct);
        WorkspaceRole? role = roles.TryGetValue(repo.CompanyId, out var cr) ? cr : null;
        if (caller.UserId is { } uid)
        {
            var rp = await db.RepositoryPermissions.AsNoTracking().Where(p => p.RepositoryId == repo.Id && p.UserId == uid).Select(p => (WorkspaceRole?)p.Role).FirstOrDefaultAsync(ct);
            if (rp is not null && (role is null || rp > role)) role = rp;
        }
        return role;
    }

    public async Task<Repository> RequireRepositoryAsync(Guid id, RepoAction action, CancellationToken ct, bool track = true)
    {
        var query = db.Repositories.Include(r => r.Company).Include(r => r.Project).Include(r => r.Settings).Include(r => r.Remotes).AsQueryable();
        if (!track) query = query.AsNoTracking();
        var repo = await query.FirstOrDefaultAsync(r => r.Id == id && r.State != RepositoryState.Deleted, ct);
        if (repo is null) throw new NotFoundException("Repository");

        var role = await EffectiveRoleAsync(repo, ct);
        if (role is null)
        {
            await DenyAsync("repository.access", repo, action, "no membership", ct);
            throw new NotFoundException("Repository"); // do not reveal other tenants' repositories
        }
        var (needed, scope) = action switch
        {
            RepoAction.Read => (WorkspaceRole.ReadOnly, Scopes.ReposRead),
            RepoAction.Write => (WorkspaceRole.Developer, Scopes.ReposWrite),
            RepoAction.Recover => (WorkspaceRole.Developer, Scopes.RecoveryExecute),
            RepoAction.Administer => (WorkspaceRole.RepositoryAdministrator, Scopes.ReposWrite),
            RepoAction.Delete => (WorkspaceRole.CompanyAdministrator, Scopes.Admin),
            _ => (WorkspaceRole.CompanyAdministrator, Scopes.Admin)
        };
        if (role < needed || !HasScope(scope) && !(action == RepoAction.Write && HasScope(Scopes.ChangesWrite)))
        {
            await DenyAsync("repository.access", repo, action, $"role {role} / scopes", ct);
            throw new ForbiddenException($"This action requires the {needed} role and the '{scope}' scope.");
        }
        return repo;
    }

    public async Task<Company> RequireCompanyAsync(Guid companyId, WorkspaceRole minimum, string scope, CancellationToken ct)
    {
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == companyId && c.IsActive, ct) ?? throw new NotFoundException("Company");
        if (caller.IsSystemAdministrator) return company;
        var roles = await CompanyRolesAsync(ct);
        if (!roles.TryGetValue(companyId, out var role))
        {
            await audit.RecordNowAsync("company.access", null, companyId, companyId.ToString(), "denied", new { reason = "no membership" }, ct);
            throw new NotFoundException("Company");
        }
        if (role < minimum || !HasScope(scope))
        {
            await audit.RecordNowAsync("company.access", null, companyId, companyId.ToString(), "denied", new { role, minimum, scope }, ct);
            throw new ForbiddenException();
        }
        return company;
    }

    public bool HasScope(string scope) => caller.IsSystemAdministrator && caller.Scopes.Count == 0 || caller.Scopes.Contains(scope) || caller.Scopes.Contains(Scopes.Admin);

    public void RequireScope(string scope)
    {
        if (!HasScope(scope)) throw new ForbiddenException($"The '{scope}' scope is required.");
    }

    public void RequireSystemAdministrator()
    {
        if (!caller.IsSystemAdministrator) throw new ForbiddenException("System administrator role required.");
    }

    public async Task<bool> CanOverridePoliciesAsync(Repository repo, CancellationToken ct) =>
        !caller.IsServiceAccount && await EffectiveRoleAsync(repo, ct) >= WorkspaceRole.RepositoryAdministrator;

    private async Task DenyAsync(string action, Repository repo, RepoAction requested, string reason, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await audit.RecordNowAsync(action, repo.Id, repo.CompanyId, requested.ToString(), "denied", new { reason }, ct);
    }
}
