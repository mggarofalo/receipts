using Application.Models.Ynab;
using FluentAssertions;
using Infrastructure.Ynab;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Tests.Services;

public class YnabCredentialSourceTests
{
	// Expected CacheIdentity below is the first 8 hex chars of SHA-256("test-pat"), computed outside the production code.
	private const string TestPat = "test-pat";

	private static YnabCredentialSource CreateSource(string? pat, bool includeKey = true)
	{
		Dictionary<string, string?> values = [];
		if (includeKey)
		{
			values["YNAB_PAT"] = pat;
		}

		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(values)
			.Build();
		return Infrastructure.Tests.Ynab.YnabCredentialSourceFactory.PatOnly(configuration);
	}

	[Fact]
	public async Task GetStateAsync_PatSet_ReportsUsablePersonalAccessToken()
	{
		// Arrange
		YnabCredentialSource source = CreateSource(TestPat);

		// Act
		YnabCredentialState state = await source.GetStateAsync(CancellationToken.None);

		// Assert
		state.Kind.Should().Be(YnabCredentialKind.PersonalAccessToken);
		state.IsUsable.Should().BeTrue();
	}

	[Fact]
	public async Task GetCredentialAsync_PatSet_ReturnsPatWithHashedCacheIdentity()
	{
		// Arrange
		YnabCredentialSource source = CreateSource(TestPat);

		// Act
		YnabBearerCredential? credential = await source.GetCredentialAsync(CancellationToken.None);

		// Assert
		credential.Should().NotBeNull();
		credential!.Token.Should().Be(TestPat);
		credential.CacheIdentity.Should().Be("27573E06");
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("	")]
	public async Task GetStateAndCredential_BlankPat_ReportsNoneAndNoCredential(string pat)
	{
		// Arrange
		YnabCredentialSource source = CreateSource(pat);

		// Act
		YnabCredentialState state = await source.GetStateAsync(CancellationToken.None);
		YnabBearerCredential? credential = await source.GetCredentialAsync(CancellationToken.None);

		// Assert
		state.Kind.Should().Be(YnabCredentialKind.None);
		state.IsUsable.Should().BeFalse();
		credential.Should().BeNull();
	}

	[Fact]
	public async Task GetStateAndCredential_PatMissing_ReportsNoneAndNoCredential()
	{
		// Arrange
		YnabCredentialSource source = CreateSource(pat: null, includeKey: false);

		// Act
		YnabCredentialState state = await source.GetStateAsync(CancellationToken.None);
		YnabBearerCredential? credential = await source.GetCredentialAsync(CancellationToken.None);

		// Assert
		state.Kind.Should().Be(YnabCredentialKind.None);
		state.IsUsable.Should().BeFalse();
		credential.Should().BeNull();
	}

	[Fact]
	public async Task GetCredentialAsync_DifferentPats_GiveDifferentCacheIdentities()
	{
		// Arrange
		YnabCredentialSource first = CreateSource("pat-one");
		YnabCredentialSource second = CreateSource("pat-two");

		// Act
		YnabBearerCredential? firstCredential = await first.GetCredentialAsync(CancellationToken.None);
		YnabBearerCredential? secondCredential = await second.GetCredentialAsync(CancellationToken.None);

		// Assert
		firstCredential!.CacheIdentity.Should().NotBe(secondCredential!.CacheIdentity);
	}

	[Fact]
	public void BearerCredential_ToString_DoesNotContainToken()
	{
		// Arrange
		YnabBearerCredential credential = new("super-secret-token", "ABCD1234");

		// Act
		string text = credential.ToString();

		// Assert
		text.Should().NotContain("super-secret-token");
		text.Should().Contain("ABCD1234");
	}
}
