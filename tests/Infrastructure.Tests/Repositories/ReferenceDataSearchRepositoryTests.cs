using Application.Models;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Repositories;

public class ReferenceDataSearchRepositoryTests
{
	private readonly IDbContextFactory<ApplicationDbContext> _contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

	[Fact]
	public async Task Accounts_SearchIsTrimmedCaseInsensitiveAndCombinesWithIsActive()
	{
		await using ApplicationDbContext context = await _contextFactory.CreateDbContextAsync();
		AccountEntity match = new() { Id = Guid.NewGuid(), Name = "Primary Checking", IsActive = true };
		AccountEntity inactiveMatch = new() { Id = Guid.NewGuid(), Name = "PRIMARY Savings", IsActive = false };
		AccountEntity other = new() { Id = Guid.NewGuid(), Name = "Vacation", IsActive = true };
		context.Accounts.AddRange(match, inactiveMatch, other);
		await context.SaveChangesAsync();
		AccountRepository repository = new(_contextFactory);

		List<AccountEntity> result = await repository.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None, true, "  pRiMaRy  ");
		int total = await repository.GetCountAsync(CancellationToken.None, true, "  pRiMaRy  ");

		result.Select(x => x.Id).Should().Equal(match.Id);
		total.Should().Be(1);
	}

	[Fact]
	public async Task Accounts_WhitespaceSearchIsIgnored()
	{
		await using ApplicationDbContext context = await _contextFactory.CreateDbContextAsync();
		context.Accounts.AddRange(
			new AccountEntity { Id = Guid.NewGuid(), Name = "One", IsActive = true },
			new AccountEntity { Id = Guid.NewGuid(), Name = "Two", IsActive = true });
		await context.SaveChangesAsync();
		AccountRepository repository = new(_contextFactory);

		List<AccountEntity> result = await repository.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None, q: "   ");

		result.Should().HaveCount(2);
	}

	[Fact]
	public async Task Cards_SearchMatchesNameOrCardCodeAndTotalPrecedesPagination()
	{
		await using ApplicationDbContext context = await _contextFactory.CreateDbContextAsync();
		context.Cards.AddRange(
			new CardEntity { Id = Guid.NewGuid(), Name = "Travel Visa", CardCode = "V111", IsActive = true },
			new CardEntity { Id = Guid.NewGuid(), Name = "Everyday", CardCode = "TRAVEL-22", IsActive = true },
			new CardEntity { Id = Guid.NewGuid(), Name = "Cash", CardCode = "C333", IsActive = true });
		await context.SaveChangesAsync();
		CardRepository repository = new(_contextFactory);

		List<CardEntity> page = await repository.GetAllAsync(0, 1, SortParams.Default, CancellationToken.None, true, " travel ");
		int total = await repository.GetCountAsync(CancellationToken.None, true, " travel ");

		page.Should().HaveCount(1);
		total.Should().Be(2);
	}

	[Fact]
	public async Task Categories_SearchIsTrimmedCaseInsensitiveAndCombinesWithIsActive()
	{
		await using ApplicationDbContext context = await _contextFactory.CreateDbContextAsync();
		CategoryEntity match = new() { Id = Guid.NewGuid(), Name = "Food and Drink", IsActive = true };
		context.Categories.AddRange(
			match,
			new CategoryEntity { Id = Guid.NewGuid(), Name = "FOOD Supplies", IsActive = false },
			new CategoryEntity { Id = Guid.NewGuid(), Name = "Travel", IsActive = true });
		await context.SaveChangesAsync();
		CategoryRepository repository = new(_contextFactory);

		List<CategoryEntity> result = await repository.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None, true, " food ");
		int total = await repository.GetCountAsync(CancellationToken.None, true, " food ");

		result.Select(x => x.Id).Should().Equal(match.Id);
		total.Should().Be(1);
	}

	[Fact]
	public async Task Subcategories_SearchCombinesWithCategoryIdAndIsActive()
	{
		Guid categoryId = Guid.NewGuid();
		Guid otherCategoryId = Guid.NewGuid();
		await using ApplicationDbContext context = await _contextFactory.CreateDbContextAsync();
		context.Categories.AddRange(
			new CategoryEntity { Id = categoryId, Name = "Food", IsActive = true },
			new CategoryEntity { Id = otherCategoryId, Name = "Other", IsActive = true });
		SubcategoryEntity match = new() { Id = Guid.NewGuid(), CategoryId = categoryId, Name = "Fresh Dairy", IsActive = true };
		context.Subcategories.AddRange(
			match,
			new SubcategoryEntity { Id = Guid.NewGuid(), CategoryId = categoryId, Name = "DAIRY Treats", IsActive = false },
			new SubcategoryEntity { Id = Guid.NewGuid(), CategoryId = otherCategoryId, Name = "Dairy", IsActive = true });
		await context.SaveChangesAsync();
		SubcategoryRepository repository = new(_contextFactory);

		List<SubcategoryEntity> result = await repository.GetByCategoryIdAsync(categoryId, 0, 50, SortParams.Default, CancellationToken.None, true, " dairy ");
		int total = await repository.GetByCategoryIdCountAsync(categoryId, CancellationToken.None, true, " dairy ");

		result.Select(x => x.Id).Should().Equal(match.Id);
		total.Should().Be(1);
	}
}
