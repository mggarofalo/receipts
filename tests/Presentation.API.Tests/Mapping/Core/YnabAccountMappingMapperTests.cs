using API.Generated.Dtos;
using API.Mapping.Core;
using Application.Models.Ynab;

namespace Presentation.API.Tests.Mapping.Core;

public class YnabAccountMappingMapperTests
{
	private readonly YnabMapper _mapper = new();

	[Fact]
	public void ToAccountMappingResponse_MapsAllFields()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();
		DateTimeOffset now = DateTimeOffset.UtcNow;
		YnabAccountMappingDto dto = new(id, accountId, "ynab-1", "Checking", "budget-1", now, now);

		// Act
		YnabAccountMappingResponse result = _mapper.ToAccountMappingResponse(dto);

		// Assert
		Assert.Equal(id, result.Id);
		Assert.Equal(accountId, result.ReceiptsAccountId);
		Assert.Equal("ynab-1", result.YnabAccountId);
		Assert.Equal("Checking", result.YnabAccountName);
		Assert.Equal("budget-1", result.YnabBudgetId);
		Assert.Equal(now, result.CreatedAt);
		Assert.Equal(now, result.UpdatedAt);
	}

	[Fact]
	public void ToAccountMappingListResponse_MapsAllMappings()
	{
		// Arrange
		List<YnabAccountMappingDto> mappings =
		[
			new(Guid.NewGuid(), Guid.NewGuid(), "ynab-1", "Checking", "budget-1",
				DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			new(Guid.NewGuid(), Guid.NewGuid(), "ynab-2", "Savings", "budget-1",
				DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
		];

		// Act
		YnabAccountMappingListResponse result = _mapper.ToAccountMappingListResponse(mappings);

		// Assert
		Assert.Equal(2, result.Data.Count);
	}

	[Fact]
	public void ToAccountMappingListResponse_MapsEmptyList()
	{
		// Arrange
		List<YnabAccountMappingDto> mappings = [];

		// Act
		YnabAccountMappingListResponse result = _mapper.ToAccountMappingListResponse(mappings);

		// Assert
		Assert.Empty(result.Data);
	}

	[Fact]
	public void ToAccountSummary_MapsAllFields()
	{
		// Arrange
		YnabAccount account = new("acc-1", "Checking", "checking", true, false, 100000);

		// Act
		YnabAccountSummary result = _mapper.ToAccountSummary(account);

		// Assert
		Assert.Equal("acc-1", result.Id);
		Assert.Equal("Checking", result.Name);
		Assert.Equal("checking", result.Type);
		Assert.True(result.OnBudget);
		Assert.False(result.Closed);
		Assert.Equal(100000, result.Balance);
	}

	[Fact]
	public void ToAccountListResponse_MapsAllAccounts()
	{
		// Arrange
		List<YnabAccount> accounts =
		[
			new("acc-1", "Checking", "checking", true, false, 100000),
			new("acc-2", "Savings", "savings", true, false, 50000),
		];

		// Act
		YnabAccountListResponse result = _mapper.ToAccountListResponse(accounts);

		// Assert
		Assert.Equal(2, result.Data.Count);
	}
}
