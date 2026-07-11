using Application.Exceptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests;

// RECEIPTS-772: the unique indexes on Categories.Name / Subcategories(CategoryId, Name) /
// ItemTemplates.Name are filtered on DeletedAt IS NULL, so a name can be re-created after the
// original is soft-deleted. Restoring the original must then fail cleanly (409 via
// DuplicateEntityException) rather than throwing a raw unique-violation (500). These tests run
// against real PostgreSQL, where the filtered unique index is actually enforced.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RestoreConflictTests(PostgresFixture fixture)
{
	private sealed class FixtureContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}

	private CategoryService CreateCategoryService() =>
		new(new CategoryRepository(new FixtureContextFactory(fixture)), new CategoryMapper());

	[Fact]
	public async Task RestoreCategory_WhenActiveNameWasReCreated_ThrowsDuplicateEntityException_NotDbUpdateException()
	{
		// Arrange — unique name so we never collide with seed data or other tests.
		string name = $"RestoreConflict-{Guid.NewGuid()}";
		Guid deletedId;

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			CategoryEntity original = new() { Id = Guid.NewGuid(), Name = name, Description = "original", IsActive = true };
			setup.Categories.Add(original);
			await setup.SaveChangesAsync();
			deletedId = original.Id;

			// Soft-delete the original, freeing the name.
			setup.Categories.Remove(original);
			await setup.SaveChangesAsync();

			// Re-create an ACTIVE category with the same name (allowed by the filtered index).
			setup.Categories.Add(new CategoryEntity { Id = Guid.NewGuid(), Name = name, Description = "re-created", IsActive = true });
			await setup.SaveChangesAsync();
		}

		CategoryService service = CreateCategoryService();

		// Act
		Func<Task> act = async () => await service.RestoreAsync(deletedId, CancellationToken.None);

		// Assert — surfaced as a 409-mapped conflict carrying the conflicting name, not a 500.
		(await act.Should().ThrowAsync<DuplicateEntityException>())
			.Which.Message.Should().Contain(name);

		// The soft-deleted row must remain deleted since the restore was rejected.
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		CategoryEntity? stillDeleted = await verify.Categories
			.IgnoreQueryFilters()
			.SingleAsync(c => c.Id == deletedId);
		stillDeleted.DeletedAt.Should().NotBeNull("a rejected restore must not clear DeletedAt");
	}

	[Fact]
	public async Task RestoreCategory_WhenNoActiveNameConflict_Succeeds()
	{
		// Arrange — soft-delete a category whose name is NOT taken by any active row.
		string name = $"RestoreOk-{Guid.NewGuid()}";
		Guid deletedId;

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			CategoryEntity original = new() { Id = Guid.NewGuid(), Name = name, Description = "original", IsActive = true };
			setup.Categories.Add(original);
			await setup.SaveChangesAsync();
			deletedId = original.Id;

			setup.Categories.Remove(original);
			await setup.SaveChangesAsync();
		}

		CategoryService service = CreateCategoryService();

		// Act
		bool restored = await service.RestoreAsync(deletedId, CancellationToken.None);

		// Assert
		restored.Should().BeTrue();

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		CategoryEntity? active = await verify.Categories.SingleOrDefaultAsync(c => c.Id == deletedId);
		active.Should().NotBeNull("the category should be visible again after a conflict-free restore");
		active!.DeletedAt.Should().BeNull();
	}
}
