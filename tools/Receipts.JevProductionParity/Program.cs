using System.Text.Json;
using Receipts.JevProductionParity;

if (args.Length > 0 && args[0] == "live-run")
{
	return await LiveCommand.RunAsync(args);
}

if (args.Length > 0 && args[0].StartsWith("offline-", StringComparison.Ordinal))
{
	return await OfflineCommand.RunAsync(args);
}

if (args.Length != 1)
{
	Console.Error.WriteLine("Usage: Receipts.JevProductionParity <sanitized-fixture.json>");
	return 2;
}

try
{
	string json = File.ReadAllText(args[0]);
	ProductionParityFixture fixture = JsonSerializer.Deserialize<ProductionParityFixture>(json, ProductionParity.JsonOptions)
		?? throw new JsonException("Fixture must be a JSON object.");
	ProductionParityRecord record = ProductionParity.Evaluate(fixture);
	Console.WriteLine(JsonSerializer.Serialize(record, ProductionParity.JsonOptions));
	return 0;
}
catch (Exception exception) when (exception is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
{
	// Do not echo fixture contents or exception text into logs.
	Console.Error.WriteLine("Invalid or unreadable fixture. Check the documented fixture schema and production input limits.");
	return 2;
}
