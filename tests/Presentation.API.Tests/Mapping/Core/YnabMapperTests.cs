using API.Generated.Dtos;
using API.Mapping.Core;
using Application.Models.Ynab;

namespace Presentation.API.Tests.Mapping.Core;

public class YnabMapperTests
{
	private readonly YnabMapper _mapper = new();

	[Fact]
	public void ToBudgetListResponse_MapsAllBudgets()
	{
		// Arrange
		List<YnabBudget> budgets =
		[
			new("budget-1", "My Budget"),
			new("budget-2", "Other Budget"),
		];

		// Act
		YnabBudgetListResponse result = _mapper.ToBudgetListResponse(budgets);

		// Assert
		Assert.Equal(2, result.Data.Count);

		YnabBudgetSummary first = result.Data.ElementAt(0);
		Assert.Equal("budget-1", first.Id);
		Assert.Equal("My Budget", first.Name);

		YnabBudgetSummary second = result.Data.ElementAt(1);
		Assert.Equal("budget-2", second.Id);
		Assert.Equal("Other Budget", second.Name);
	}

	[Fact]
	public void ToBudgetListResponse_MapsEmptyList()
	{
		// Arrange
		List<YnabBudget> budgets = [];

		// Act
		YnabBudgetListResponse result = _mapper.ToBudgetListResponse(budgets);

		// Assert
		Assert.Empty(result.Data);
	}

	[Fact]
	public void ToBudgetSettingsResponse_MapsSelectedBudgetId()
	{
		// Arrange
		string budgetId = Guid.NewGuid().ToString();
		YnabBudgetSelection selection = new(budgetId);

		// Act
		YnabBudgetSettingsResponse result = _mapper.ToBudgetSettingsResponse(selection);

		// Assert
		Assert.Equal(budgetId, result.SelectedBudgetId);
	}

	[Fact]
	public void ToBudgetSettingsResponse_MapsNullBudgetId()
	{
		// Arrange
		YnabBudgetSelection selection = new(null);

		// Act
		YnabBudgetSettingsResponse result = _mapper.ToBudgetSettingsResponse(selection);

		// Assert
		Assert.Null(result.SelectedBudgetId);
	}

	[Fact]
	public void ToCategoryListResponse_MapsAllCategories()
	{
		// Arrange
		List<YnabCategory> categories =
		[
			new("cat-1", "Groceries", "group-1", "Needs", false),
			new("cat-2", "Rent", "group-1", "Needs", false),
		];

		// Act
		YnabCategoryListResponse result = _mapper.ToCategoryListResponse(categories);

		// Assert
		Assert.Equal(2, result.Data.Count);
		Assert.Equal("cat-1", result.Data.ElementAt(0).Id);
		Assert.Equal("Groceries", result.Data.ElementAt(0).Name);
		Assert.Equal("group-1", result.Data.ElementAt(0).CategoryGroupId);
		Assert.Equal("Needs", result.Data.ElementAt(0).CategoryGroupName);
		Assert.False(result.Data.ElementAt(0).Hidden);
	}

	[Fact]
	public void ToEnvelopeMappingResponse_MapsAllFields()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		Guid envelopeId = Guid.NewGuid();
		DateTimeOffset created = DateTimeOffset.UtcNow.AddDays(-1);
		DateTimeOffset updated = DateTimeOffset.UtcNow;
		YnabEnvelopeMappingDto dto = new(id, envelopeId, "Groceries", "cat-1", "Groceries YNAB", "Needs", "budget-1", created, updated);

		// Act
		YnabEnvelopeMappingResponse result = _mapper.ToEnvelopeMappingResponse(dto);

		// Assert
		Assert.Equal(id, result.Id);
		Assert.Equal(envelopeId, result.EnvelopeId);
		Assert.Equal("Groceries", result.EnvelopeName);
		Assert.Equal("cat-1", result.YnabCategoryId);
		Assert.Equal("Groceries YNAB", result.YnabCategoryName);
		Assert.Equal("Needs", result.YnabCategoryGroupName);
		Assert.Equal("budget-1", result.YnabBudgetId);
		Assert.Equal(created, result.CreatedAt);
		Assert.Equal(updated, result.UpdatedAt);
	}

	[Fact]
	public void ToEnvelopeMappingListResponse_MapsAllMappingsInOrder()
	{
		// Arrange
		List<YnabEnvelopeMappingDto> mappings =
		[
			new(Guid.NewGuid(), Guid.NewGuid(), "Groceries", "cat-1", "Groceries", "Needs", "budget-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			new(Guid.NewGuid(), Guid.NewGuid(), "Electronics", "cat-2", "Technology", "Wants", "budget-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
		];

		// Act
		YnabEnvelopeMappingListResponse result = _mapper.ToEnvelopeMappingListResponse(mappings);

		// Assert
		Assert.Equal(["Groceries", "Electronics"], result.Data.Select(m => m.EnvelopeName));
	}

	[Fact]
	public void ToConnectionStatusResponse_ConnectedWithOAuth_ReportsOAuthAndWhenItWasConnected()
	{
		// Arrange
		DateTimeOffset connectedAt = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
		DateTimeOffset lastSync = new(2026, 11, 19, 13, 30, 0, TimeSpan.Zero);
		YnabConnectionStatus status = new(
			true, true, lastSync, new YnabCredentialState(YnabCredentialKind.OAuth, OAuthAvailable: true, OAuthConnectedAtUtc: connectedAt));

		// Act
		YnabConnectionStatusResponse result = _mapper.ToConnectionStatusResponse(status);

		// Assert
		Assert.Equal(YnabCredentialSource.Oauth, result.CredentialSource);
		Assert.True(result.IsConfigured);
		Assert.True(result.IsConnected);
		Assert.True(result.OauthAvailable);
		Assert.False(result.ReconnectRequired);
		Assert.Equal(connectedAt, result.OauthConnectedAtUtc);
		Assert.Equal(lastSync, result.LastSuccessfulSyncUtc);
	}

	[Fact]
	public void ToConnectionStatusResponse_PersonalAccessTokenOnly_ReportsThePatAndKeepsExistingProperties()
	{
		// Arrange
		DateTimeOffset lastSync = new(2026, 11, 19, 13, 30, 0, TimeSpan.Zero);
		YnabConnectionStatus status = new(
			true, true, lastSync, new YnabCredentialState(YnabCredentialKind.PersonalAccessToken));

		// Act
		YnabConnectionStatusResponse result = _mapper.ToConnectionStatusResponse(status);

		// Assert
		Assert.Equal(YnabCredentialSource.PersonalAccessToken, result.CredentialSource);
		Assert.True(result.IsConfigured);
		Assert.True(result.IsConnected);
		Assert.False(result.OauthAvailable);
		Assert.False(result.ReconnectRequired);
		Assert.Null(result.OauthConnectedAtUtc);
		Assert.Equal(lastSync, result.LastSuccessfulSyncUtc);
	}

	[Fact]
	public void ToConnectionStatusResponse_OAuthAvailableButNotConnected_ReportsNoCredentialAndOAuthAvailable()
	{
		// Arrange
		YnabConnectionStatus status = new(
			false, false, null, new YnabCredentialState(YnabCredentialKind.None, OAuthAvailable: true));

		// Act
		YnabConnectionStatusResponse result = _mapper.ToConnectionStatusResponse(status);

		// Assert
		Assert.Equal(YnabCredentialSource.None, result.CredentialSource);
		Assert.False(result.IsConfigured);
		Assert.False(result.IsConnected);
		Assert.True(result.OauthAvailable);
		Assert.False(result.ReconnectRequired);
		Assert.Null(result.OauthConnectedAtUtc);
		Assert.Null(result.LastSuccessfulSyncUtc);
	}

	[Fact]
	public void ToConnectionStatusResponse_ReconnectRequired_ReportsItWithNoCredentialInUse()
	{
		// Arrange
		YnabConnectionStatus status = new(
			false, false, null, new YnabCredentialState(YnabCredentialKind.OAuthReconnectRequired, OAuthAvailable: true));

		// Act
		YnabConnectionStatusResponse result = _mapper.ToConnectionStatusResponse(status);

		// Assert
		Assert.True(result.ReconnectRequired);
		Assert.False(result.IsConnected);
		Assert.False(result.IsConfigured);
		Assert.Equal(YnabCredentialSource.None, result.CredentialSource);
		Assert.True(result.OauthAvailable);
		Assert.Null(result.OauthConnectedAtUtc);
	}

	[Theory]
	[InlineData(YnabCredentialKind.None)]
	[InlineData(YnabCredentialKind.PersonalAccessToken)]
	[InlineData(YnabCredentialKind.OAuth)]
	[InlineData(YnabCredentialKind.OAuthReconnectRequired)]
	public void ToConnectionStatusResponse_Serialized_HasOnlyTheDocumentedMembersAndNoneThatCouldCarryASecret(YnabCredentialKind kind)
	{
		// Arrange
		YnabConnectionStatus status = new(
			true, true, DateTimeOffset.UnixEpoch, new YnabCredentialState(kind, OAuthAvailable: true, OAuthConnectedAtUtc: DateTimeOffset.UnixEpoch));

		// Act
		string json = System.Text.Json.JsonSerializer.Serialize(_mapper.ToConnectionStatusResponse(status));

		// Assert
		using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
		string[] members = [.. document.RootElement.EnumerateObject().Select(member => member.Name)];
		Assert.Equal(
			["credentialSource", "isConfigured", "isConnected", "lastSuccessfulSyncUtc", "oauthAvailable", "oauthConnectedAtUtc", "reconnectRequired"],
			members.Order(StringComparer.Ordinal));
		Assert.DoesNotContain(members, member =>
			member.Contains("token", StringComparison.OrdinalIgnoreCase)
			|| member.Contains("secret", StringComparison.OrdinalIgnoreCase)
			|| member.Contains("code", StringComparison.OrdinalIgnoreCase)
			|| member.Contains("refresh", StringComparison.OrdinalIgnoreCase));
	}

	[Theory]
	[InlineData(Application.Models.Ynab.YnabRemoteRevocation.Succeeded, global::API.Generated.Dtos.YnabRemoteRevocation.Succeeded)]
	[InlineData(Application.Models.Ynab.YnabRemoteRevocation.Failed, global::API.Generated.Dtos.YnabRemoteRevocation.Failed)]
	[InlineData(Application.Models.Ynab.YnabRemoteRevocation.NotAttempted, global::API.Generated.Dtos.YnabRemoteRevocation.NotAttempted)]
	public void ToDisconnectResponse_MapsEachRemoteRevocationOutcome(
		Application.Models.Ynab.YnabRemoteRevocation source, global::API.Generated.Dtos.YnabRemoteRevocation expected)
	{
		// Act
		YnabDisconnectResponse result = _mapper.ToDisconnectResponse(source);

		// Assert
		Assert.Equal(expected, result.RemoteRevocation);
	}
}
