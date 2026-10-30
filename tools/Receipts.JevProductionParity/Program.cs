using Receipts.JevProductionParity;

if (args.Length > 0 && args[0] == "live-run")
{
	return await LiveCommand.RunAsync(args);
}

if (args.Length > 0 && args[0].StartsWith("offline-", StringComparison.Ordinal))
{
	return await OfflineCommand.RunAsync(args);
}

Console.Error.WriteLine("Usage: Receipts.JevProductionParity live-run ... | offline-plan ... | offline-replay-v4 ... | offline-production-v4 ...");
return 2;
