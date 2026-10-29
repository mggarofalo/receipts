using System.Text.Json;
using System.Text.RegularExpressions;

namespace Receipts.JevProductionParity;

/// <summary>
/// Resolves the Jev key the way scripts/classification-evaluation.mjs does: a set process variable wins
/// (an empty one is missing); otherwise, only with explicit opt-in, the API project's .NET User Secrets.
/// Returns null on any failure and never exposes paths, file contents or parser errors.
/// </summary>
public static partial class ApiCredential
{
	public const string VariableName = "TYPESAFE_API_KEY";

	public static string? Resolve(string repository, bool apiUserSecrets) =>
		Resolve(repository, apiUserSecrets, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows());

	public static string? Resolve(string repository, bool apiUserSecrets, Func<string, string?> environment, bool isWindows)
	{
		string? value = environment(VariableName);
		if (value is not null)
		{
			return Usable(value);
		}

		if (!apiUserSecrets)
		{
			return null;
		}

		try
		{
			string[] projects = Directory.GetFiles(Path.Combine(repository, "src", "Presentation", "API"), "*.csproj");
			if (projects.Length != 1)
			{
				return null;
			}

			MatchCollection ids = UserSecretsId().Matches(File.ReadAllText(projects[0]));
			if (ids.Count != 1)
			{
				return null;
			}

			string id = ids[0].Groups[1].Value;
			string? home = environment("HOME");
			string? root = isWindows ? environment("APPDATA")
				: string.IsNullOrEmpty(home) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : home;
			if (string.IsNullOrEmpty(root))
			{
				return null;
			}

			string path = isWindows
				? Path.Combine(root, "Microsoft", "UserSecrets", id, "secrets.json")
				: Path.Combine(root, ".microsoft", "usersecrets", id, "secrets.json");
			using JsonDocument secrets = JsonDocument.Parse(File.ReadAllText(path).TrimStart('﻿'));
			return secrets.RootElement.ValueKind == JsonValueKind.Object &&
				secrets.RootElement.TryGetProperty(VariableName, out JsonElement key) &&
				key.ValueKind == JsonValueKind.String
				? Usable(key.GetString())
				: null;
		}
		catch (Exception)
		{
			// Never surface filesystem paths, JSON content or parser details.
			return null;
		}
	}

	// A bearer token is visible ASCII only. Reject anything else before a durable intent is
	// written: an unsendable header would otherwise strand that intent and block the packet.
	private static string? Usable(string? value) =>
		!string.IsNullOrEmpty(value) && value.All(c => c is > ' ' and <= '~') ? value : null;

	[GeneratedRegex(@"<UserSecretsId>\s*([A-Za-z0-9_-]+)\s*</UserSecretsId>")]
	private static partial Regex UserSecretsId();
}
