using Application.Interfaces.Services;
using Infrastructure;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

if (!InfrastructureService.IsDatabaseConfigured(builder.Configuration))
{
	Console.Error.WriteLine("Database is not configured. Set POSTGRES_* env vars or an Aspire connection string.");
	return 1;
}

NpgsqlDataSourceBuilder dataSourceBuilder = new(InfrastructureService.GetConnectionString(builder.Configuration));
dataSourceBuilder.UseVector();
NpgsqlDataSource dataSource = dataSourceBuilder.Build();

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
{
	options.UseNpgsql(dataSource, b =>
	{
		string? assemblyName = typeof(ApplicationDbContext).Assembly.FullName;
		b.MigrationsAssembly(assemblyName);
		b.UseVector();
		b.UsePublicMigrationsHistory();
	});
	options.ConfigureWarnings(w => w.Log(
		(RelationalEventId.PendingModelChangesWarning, LogLevel.Warning)));
});

// This tool registers the EF factory directly (no RegisterInfrastructureServices), so it must also
// register the accessor that the factory-built ApplicationDbContext now requires (RECEIPTS-753). The
// 3-param ctor carries [ActivatorUtilitiesConstructor]; without this, factory.CreateDbContextAsync()
// throws "Unable to resolve service for type 'ICurrentUserAccessor'", which under docker-entrypoint.sh
// (set -e) would abort container boot before the API starts. Migrations need no user attribution, so the
// null accessor is correct. (IDescriptionChangeSignal stays unregistered — the ctor parameter is optional
// and migrations never call SaveChangesAsync.)
builder.Services.AddSingleton<ICurrentUserAccessor, NullCurrentUserAccessor>();

IHost host = builder.Build();
try
{
	await host.StartAsync();

	IDbContextFactory<ApplicationDbContext> factory = host.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
	await using ApplicationDbContext context = await factory.CreateDbContextAsync();

	ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DbMigrator");
	logger.LogInformation("Applying EF Core migrations...");

	await context.Database.MigrateAsync();

	logger.LogInformation("Migrations applied successfully.");

	await host.StopAsync();
	return 0;
}
catch (Exception ex)
{
	Console.Error.WriteLine($"Migration failed: {ex}");
	return 1;
}
