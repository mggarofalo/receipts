using Application.Envelopes;
using Infrastructure;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

// Read-only budget parity checks. Prints counts only, never names or amounts.
//   (no arguments)              envelope path vs the retired node mappings; holds only before a restructure
//   --write-baseline <file>     record each item's YNAB category through its envelope (ids only)
//   --compare-baseline <file>   compare today's routes with a baseline; use across a restructure
const string Header = "receipts-envelope-baseline v1";
string? mode = args.Length switch { 0 => "legacy", 2 when args[0] is "--write-baseline" or "--compare-baseline" => args[0], _ => null };
if (mode is null)
{
	Console.Error.WriteLine("Usage: [--write-baseline <file> | --compare-baseline <file>]. Set RECEIPTS_MIGRATION_CONNECTION securely in the process environment.");
	return 1;
}
string connection = Environment.GetEnvironmentVariable("RECEIPTS_MIGRATION_CONNECTION")
	?? throw new InvalidOperationException("RECEIPTS_MIGRATION_CONNECTION is required.");
var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection, npgsql => npgsql.UseVector()).Options;
await using var db = new ApplicationDbContext(options);
await using var snapshot = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

if (mode == "legacy")
{
	EnvelopeParityReport report = await EnvelopeParityReader.CheckAsync(db, CancellationToken.None);
	Console.WriteLine($"items {report.Items}; envelopes {report.Envelopes}; budgets {report.Budgets}; " +
		$"mapped by node path {report.ItemsMappedByOldPath}; mapped by envelope path {report.ItemsMappedByNewPath}; mismatches {report.Mismatches}");
	return report.Holds ? 0 : 2;
}

IReadOnlyList<EnvelopeYnabRoute> routes = await EnvelopeParityReader.RoutesAsync(db, CancellationToken.None);
if (mode == "--write-baseline")
{
	// Routes exist only for budgets an envelope is linked in; with none there is nothing to protect.
	if (routes.Count == 0)
	{
		Console.Error.WriteLine("No envelope is linked to a YNAB category, so there is nothing to record.");
		return 1;
	}
	await File.WriteAllLinesAsync(args[1], [Header, .. routes.Select(route => $"{route.ItemId}\t{route.BudgetId}\t{route.YnabCategoryId}")]);
	Console.WriteLine($"items {routes.Select(route => route.ItemId).Distinct().Count()}; routes {routes.Count}; baseline written");
	return 0;
}

string[] lines = await File.ReadAllLinesAsync(args[1]);
if (lines.Length == 0 || lines[0] != Header)
{
	Console.Error.WriteLine("Not an envelope baseline file.");
	return 1;
}
List<EnvelopeYnabRoute> baseline = [.. lines.Skip(1).Select(line => line.Split('\t')).Select(parts =>
	new EnvelopeYnabRoute(Guid.Parse(parts[0]), parts[1], parts[2].Length == 0 ? null : parts[2]))];
if (baseline.Count == 0 || baseline.DistinctBy(route => (route.ItemId, route.BudgetId)).Count() != baseline.Count)
{
	Console.Error.WriteLine("The baseline is empty or lists an item and budget twice.");
	return 1;
}
EnvelopeBaselineReport comparison = EnvelopeBaseline.Compare(baseline, routes);
Console.WriteLine($"items {comparison.Items}; unchanged {comparison.Unchanged}; changed {comparison.Changed}; " +
	$"added {comparison.Added}; removed {comparison.Removed}");
return comparison.Holds ? 0 : 2;
