string log = Environment.GetEnvironmentVariable("RESTORE_TEST_LOG") ?? throw new InvalidOperationException("Missing log.");
await File.AppendAllTextAsync(log, string.Join(' ', args) + Environment.NewLine);
if (args.SequenceEqual(new[] { "compose", "stop", "app" }))
    return Environment.GetEnvironmentVariable("RESTORE_TEST_STOP_FAIL") == "1" ? 7 : 0;
if (args.SequenceEqual(new[] { "compose", "start", "app" }))
    return Environment.GetEnvironmentVariable("RESTORE_TEST_START_FAIL") == "1" ? 8 : 0;
int command = Array.IndexOf(args, "-c");
if (command >= 0)
{
    string sql = args[command + 1];
    if (sql.Contains("COPY (SELECT")) Console.WriteLine("2026-10-25,7");
    else if (sql.Contains("DailyUsage") && !sql.Contains("Settings"))
        Console.WriteLine(Environment.GetEnvironmentVariable("RESTORE_TEST_PRESENT") == "1" ? "t" : "f");
    else Console.WriteLine(Environment.GetEnvironmentVariable("RESTORE_TEST_SCHEMA") == "1" ? "t" : "f");
    return 0;
}
string input = await Console.In.ReadToEndAsync();
if (input.Contains("captured_usage"))
    return Environment.GetEnvironmentVariable("RESTORE_TEST_FENCE_FAIL") == "1" ? 10 : 0;
return Environment.GetEnvironmentVariable("RESTORE_TEST_RESTORE_OK") == "1" ? 0 : 9;
