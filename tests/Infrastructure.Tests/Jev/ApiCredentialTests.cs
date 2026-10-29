using System.Text;
using System.Text.Json;
using Receipts.JevProductionParity;

namespace Infrastructure.Tests.Jev;

/// <summary>Synthetic home/appdata roots only; the real environment and user secrets are never read.</summary>
public sealed class ApiCredentialTests : IDisposable
{
	private const string Id = "synthetic-secrets-id";
	private readonly string root = Path.Combine(Path.GetTempPath(), "jev-secrets-" + Guid.NewGuid().ToString("N"));
	private string Repository => Path.Combine(root, "repository");
	private string Home => Path.Combine(root, "home");

	public ApiCredentialTests()
	{
		Directory.CreateDirectory(Path.Combine(Repository, "src", "Presentation", "API"));
		WriteProject($"<Project><PropertyGroup><UserSecretsId> {Id} </UserSecretsId></PropertyGroup></Project>");
	}

	public void Dispose() => Directory.Delete(root, recursive: true);

	private void WriteProject(string text) => File.WriteAllText(Path.Combine(Repository, "src", "Presentation", "API", "API.csproj"), text);

	private void WriteSecrets(string json, params string[] layout)
	{
		string directory = Path.Combine([Home, .. layout, Id]);
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "secrets.json"), json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
	}

	private string? Resolve(Dictionary<string, string> variables, bool apiUserSecrets = true, bool isWindows = false) =>
		ApiCredential.Resolve(Repository, apiUserSecrets, name => variables.GetValueOrDefault(name), isWindows);

	[Fact]
	public void UserSecretsWithByteOrderMarkResolveOnlyWithOptIn()
	{
		WriteSecrets("""{"TYPESAFE_API_KEY":"synthetic-file-key","Other":"x"}""", ".microsoft", "usersecrets");
		Assert.Equal("synthetic-file-key", Resolve(new() { ["HOME"] = Home }));
		Assert.Null(Resolve(new() { ["HOME"] = Home }, apiUserSecrets: false));
	}

	[Fact]
	public void WindowsLayoutUsesAppData()
	{
		WriteSecrets("""{"TYPESAFE_API_KEY":"synthetic-file-key"}""", "Microsoft", "UserSecrets");
		Assert.Equal("synthetic-file-key", Resolve(new() { ["APPDATA"] = Home }, isWindows: true));
		Assert.Null(Resolve(new() { ["HOME"] = Home }, isWindows: true));
	}

	[Fact]
	public void EnvironmentTakesPrecedenceAndEmptyIsMissing()
	{
		WriteSecrets("""{"TYPESAFE_API_KEY":"synthetic-file-key"}""", ".microsoft", "usersecrets");
		Assert.Equal("synthetic-env-key", Resolve(new() { ["HOME"] = Home, ["TYPESAFE_API_KEY"] = "synthetic-env-key" }));
		Assert.Null(Resolve(new() { ["HOME"] = Home, ["TYPESAFE_API_KEY"] = "" }));
		Assert.Null(Resolve(new() { ["HOME"] = Home, ["TYPESAFE_API_KEY"] = "  " }));
	}

	[Theory]
	[InlineData("<Project></Project>")]
	[InlineData("<Project><UserSecretsId>a</UserSecretsId><UserSecretsId>b</UserSecretsId></Project>")]
	[InlineData("<Project><UserSecretsId>../escape</UserSecretsId></Project>")]
	public void MissingOrInvalidUserSecretsIdFails(string project)
	{
		WriteSecrets("""{"TYPESAFE_API_KEY":"synthetic-file-key"}""", ".microsoft", "usersecrets");
		WriteProject(project);
		Assert.Null(Resolve(new() { ["HOME"] = Home }));
	}

	[Theory]
	[InlineData("""{"TYPESAFE_API_KEY":42}""")]
	[InlineData("""{"TYPESAFE_API_KEY":" "}""")]
	[InlineData("""{"Jev":{"TYPESAFE_API_KEY":"nested"}}""")]
	[InlineData("not json")]
	public void UnusableSecretsFail(string json)
	{
		WriteSecrets(json, ".microsoft", "usersecrets");
		Assert.Null(Resolve(new() { ["HOME"] = Home }));
	}

	public static TheoryData<string> UnsendableKeys => ["synthetic key", "synthetic\tkey", "synthetic-key\n", "synthetic-k\u00e9y", "\u00a0synthetic-key"];

	[Theory]
	[MemberData(nameof(UnsendableKeys))]
	public void EnvironmentKeyWithInvisibleOrNonAsciiCharactersIsMissing(string key)
	{
		Assert.Null(Resolve(new() { ["TYPESAFE_API_KEY"] = key }, apiUserSecrets: false));
	}

	[Theory]
	[MemberData(nameof(UnsendableKeys))]
	public void SecretsKeyWithInvisibleOrNonAsciiCharactersIsMissing(string key)
	{
		WriteSecrets(JsonSerializer.Serialize(new Dictionary<string, string> { ["TYPESAFE_API_KEY"] = key }), ".microsoft", "usersecrets");
		Assert.Null(Resolve(new() { ["HOME"] = Home }));
	}

	[Fact]
	public void VisibleAsciiKeyResolvesFromEitherSource()
	{
		const string key = "!synthetic-Key_0123.~";
		Assert.Equal(key, Resolve(new() { ["TYPESAFE_API_KEY"] = key }, apiUserSecrets: false));
		WriteSecrets(JsonSerializer.Serialize(new Dictionary<string, string> { ["TYPESAFE_API_KEY"] = key }), ".microsoft", "usersecrets");
		Assert.Equal(key, Resolve(new() { ["HOME"] = Home }));
	}

	[Fact]
	public void MissingSecretsFileFails()
	{
		Assert.Null(Resolve(new() { ["HOME"] = Home }));
	}
}
