using Serilog;
using Wings.GitManager.Infrastructure;

// Independently deployable background worker: push queue, jobs (backups, recovery clones), backup
// scheduling, monitoring and file-system watching. Run the API with Worker:RunInApi=false when this is deployed.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "WingsGitManagerWorker");
builder.Services.AddSerilog((services, cfg) => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Wings.GitManager.Worker")
    .WriteTo.Console());
builder.Services.AddGitManagerInfrastructure(builder.Configuration);
builder.Services.AddGitManagerWorkers(builder.Configuration);

var host = builder.Build();
using (var scope = host.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
host.Run();
