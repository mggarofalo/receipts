using System.Text.Json;
using Application.Taxonomy;
using Common;
using Infrastructure;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

if (args.Length != 4 || args[0] is not ("preflight" or "apply"))
{
	Console.Error.WriteLine("Usage: preflight|apply <dataset> <admin-actor-id> <frozen-request.json>. Set RECEIPTS_MIGRATION_CONNECTION securely in the process environment.");
	return 1;
}
string connection = Environment.GetEnvironmentVariable("RECEIPTS_MIGRATION_CONNECTION")
	?? throw new InvalidOperationException("RECEIPTS_MIGRATION_CONNECTION is required.");
var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection, npgsql => npgsql.UseVector()).Options;
await using (var db = new ApplicationDbContext(options))
{
	DateTimeOffset now = DateTimeOffset.UtcNow;
	bool admin = await db.Users.AnyAsync(user => user.Id == args[2] && !(user.LockoutEnabled && user.LockoutEnd > now)
		&& db.UserRoles.Any(member => member.UserId == user.Id && db.Roles.Any(role => role.Id == member.RoleId && role.Name == AppRoles.Admin)));
	if (!admin) { throw new InvalidOperationException("An enabled administrator actor is required."); }
}
var service = new LegacyTaxonomyMigrationService(new Factory(options));
LegacyMigrationPlan result;
if (args[0] == "preflight")
{
	if (File.Exists(args[3]) || File.Exists(args[3] + ".report.json")) { throw new InvalidOperationException("Frozen request or report already exists. Preserve it for retry; choose another path for a new operation."); }
	result = await service.PreflightAsync(new(args[1], Guid.NewGuid(), args[2], DateTimeOffset.UtcNow));
	await using (FileStream frozen = new(args[3], FileMode.CreateNew, FileAccess.Write))
	{ await JsonSerializer.SerializeAsync(frozen, LegacyMigrationFreeze.Source(result), new JsonSerializerOptions { WriteIndented = true }); }
	await using (FileStream report = new(args[3] + ".report.json", FileMode.CreateNew, FileAccess.Write))
	{ await JsonSerializer.SerializeAsync(report, result, new JsonSerializerOptions { WriteIndented = true }); }
}
else
{
	LegacyMigrationRequest source = JsonSerializer.Deserialize<LegacyMigrationRequest>(await File.ReadAllTextAsync(args[3]))
		?? throw new InvalidOperationException("Invalid frozen request.");
	if (source.Actor != args[2] || source.SourceDataset != args[1]) { throw new InvalidOperationException("Actor and dataset must match the frozen request."); }
	result = await service.ApplyAsync(source);
}
Console.WriteLine($"Operation {result.OperationId}; revision {result.RevisionId}; items {result.Counts.Items}; unresolved {result.Counts.Unresolved}; commit eligible {result.CanCommit}.");
return result.CanCommit ? 0 : 2;

sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
{
	public ApplicationDbContext CreateDbContext() => new(options);
}
