using System.Text.Json;

namespace Infrastructure.NormalizationAi;

/// <summary>Non-secret consent evidence lives beside credentials, outside ordinary backups.
/// The database generation is the other half of the binding. Mutations require the operator lease.</summary>
public sealed class ChatGptCredentialBinding(PiNormalizationRunnerOptions options)
{
	public const string ConsentVersion = "receipts-chatgpt-v1";
	private string FilePath => Path.Combine(Path.GetFullPath(options.HomeDirectory), ".receipts-consent.json");

	public bool Exists => File.Exists(FilePath);

	public bool Matches(string? owner, Guid generation)
	{
		if (string.IsNullOrWhiteSpace(owner) || generation == Guid.Empty) { return false; }
		try
		{
			Evidence? evidence = JsonSerializer.Deserialize<Evidence>(File.ReadAllText(FilePath));
			return evidence is not null && evidence.OwnerUserId == owner && evidence.Generation == generation
				&& evidence.ConsentVersion == ConsentVersion && evidence.ConsentedAt != default;
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return false; }
	}

	public void Invalidate() => File.Delete(FilePath);

	public void Publish(string owner, Guid generation, DateTimeOffset consentedAt)
	{
		string temporary = FilePath + ".tmp";
		try
		{
			File.WriteAllText(temporary, JsonSerializer.Serialize(new Evidence(owner, generation, ConsentVersion, consentedAt)));
			if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
			File.Move(temporary, FilePath, overwrite: true);
		}
		finally { File.Delete(temporary); }
	}

	private sealed record Evidence(string OwnerUserId, Guid Generation, string ConsentVersion, DateTimeOffset ConsentedAt);
}
