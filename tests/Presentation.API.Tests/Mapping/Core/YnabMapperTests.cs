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
	public void ToCategoryMappingResponse_MapsAllFields()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		DateTimeOffset now = DateTimeOffset.UtcNow;
		YnabCategoryMappingDto dto = new(id, "Groceries", "cat-1", "Groceries", "Needs", "budget-1", now, now);

		// Act
		YnabCategoryMappingResponse result = _mapper.ToCategoryMappingResponse(dto);

		// Assert
		Assert.Equal(id, result.Id);
		Assert.Equal("Groceries", result.ReceiptsCategory);
		Assert.Equal("cat-1", result.YnabCategoryId);
		Assert.Equal("Groceries", result.YnabCategoryName);
		Assert.Equal("Needs", result.YnabCategoryGroupName);
		Assert.Equal("budget-1", result.YnabBudgetId);
		Assert.Equal(now, result.CreatedAt);
		Assert.Equal(now, result.UpdatedAt);
	}

	[Fact]
	public void ToCategoryMappingListResponse_MapsAllMappings()
	{
		// Arrange
		List<YnabCategoryMappingDto> mappings =
		[
			new(Guid.NewGuid(), "Groceries", "cat-1", "Groceries", "Needs", "budget-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			new(Guid.NewGuid(), "Electronics", "cat-2", "Technology", "Wants", "budget-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
		];

		// Act
		YnabCategoryMappingListResponse result = _mapper.ToCategoryMappingListResponse(mappings);

		// Assert
		Assert.Equal(2, result.Data.Count);
		Assert.Equal("Groceries", result.Data.ElementAt(0).ReceiptsCategory);
		Assert.Equal("Electronics", result.Data.ElementAt(1).ReceiptsCategory);
	}

	[Fact]
	public void ToCategoryMappingListResponse_MapsEmptyList()
	{
		// Arrange
		List<YnabCategoryMappingDto> mappings = [];

		// Act
		YnabCategoryMappingListResponse result = _mapper.ToCategoryMappingListResponse(mappings);

		// Assert
		Assert.Empty(result.Data);
	}
}
