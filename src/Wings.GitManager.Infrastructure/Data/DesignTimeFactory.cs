using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Wings.GitManager.Infrastructure.Data;

/// <summary>Used by `dotnet ef` to generate the SQL Server schema script (database/sqlserver/create.sql).</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<GitManagerDbContext>
{
    public GitManagerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<GitManagerDbContext>();
        if (Environment.GetEnvironmentVariable("WGM_EF_PROVIDER") == "Sqlite") options.UseSqlite("Data Source=design.db");
        else options.UseSqlServer("Server=localhost;Database=WingsGitManager;Trusted_Connection=True;TrustServerCertificate=True");
        return new GitManagerDbContext(options.Options);
    }
}
