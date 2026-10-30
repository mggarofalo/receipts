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
}
