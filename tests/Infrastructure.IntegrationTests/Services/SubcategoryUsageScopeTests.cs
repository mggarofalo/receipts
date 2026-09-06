using System.Net;
using System.Text.Json;
using API.Configuration;
using API.Controllers.Core;
using API.Middleware;
using API.Services;
using Application.Interfaces.Services;
using Application.Services;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
public partial class SubcategoryUsageScopeTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Theory]
	[InlineData("subcategory", "A")]
	[InlineData("subcategory", "B")]
	[InlineData("category", "A")]
	[InlineData("category", "B")]
	public async Task ActualDelete_ProtectsUsedParentOnly_WhenChildNamesAreEqual(string resource, string parent)
	{
		Seed seed = await SeedAsync();
		Guid id = (resource, parent) switch { ("category", "A") => seed.CategoryA, ("category", "B") => seed.CategoryB, ("subcategory", "A") => seed.SubcategoryA, _ => seed.SubcategoryB };
		Mock<IEntityChangeNotifier> notifier = new();
		await using WebApplication app = await CreateHostAsync(notifier.Object);
		using HttpClient client = app.GetTestClient();
		using HttpResponseMessage response = await client.DeleteAsync($"/api/{(resource == "category" ? "categories" : "subcategories")}/{id}");
		response.StatusCode.Should().Be(parent == "A" ? HttpStatusCode.Conflict : HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		if (parent == "A")
		{
			using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
			problem.RootElement.GetProperty("status").GetInt32().Should().Be(409);
			problem.RootElement.GetProperty("detail").GetString().Should().Contain("Cannot delete");
			problem.RootElement.GetProperty("receiptItemCount").GetInt32().Should().Be(1);
			notifier.Verify(instance => instance.NotifyDeleted(resource, id), Times.Never);
		}
		else
		{
			notifier.Verify(instance => instance.NotifyDeleted(resource, id), Times.Once);
			if (resource == "category")
			{
				(await verify.Categories.IgnoreQueryFilters().SingleAsync(row => row.Id == id)).DeletedAt.Should().NotBeNull();
			}

			(await verify.Subcategories.IgnoreQueryFilters().IgnoreAutoIncludes().SingleAsync(row => row.Id == seed.SubcategoryB)).DeletedAt.Should().NotBeNull();
		}
		(await verify.Categories.SingleAsync(row => row.Id == seed.CategoryA)).DeletedAt.Should().BeNull();
		(await verify.Subcategories.SingleAsync(row => row.Id == seed.SubcategoryA)).DeletedAt.Should().BeNull();
		ReceiptItemEntity item = await verify.ReceiptItems.SingleAsync(row => row.ReceiptId == seed.Receipt);
		item.Category.Should().Be("A");
		item.Subcategory.Should().Be("Shared");
	}

	private async Task<Seed> SeedAsync()
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		await context.Database.ExecuteSqlRawAsync("""TRUNCATE receipts."ReceiptItems", receipts."Receipts", library."Categories" CASCADE""");
		Guid a = Guid.NewGuid(), b = Guid.NewGuid(), childA = Guid.NewGuid(), childB = Guid.NewGuid();
		context.Categories.AddRange(new CategoryEntity { Id = a, Name = "A", IsActive = true }, new CategoryEntity { Id = b, Name = "B", IsActive = true });
		context.Subcategories.AddRange(new SubcategoryEntity { Id = childA, CategoryId = a, Name = "Shared", IsActive = true }, new SubcategoryEntity { Id = childB, CategoryId = b, Name = "Shared", IsActive = true });
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.Date = new DateOnly(2024, 1, 1);
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.Category = "A"; item.Subcategory = "Shared";
		context.Receipts.Add(receipt); context.ReceiptItems.Add(item);
		await context.SaveChangesAsync();
		return new(a, b, childA, childB, receipt.Id);
	}

	private async Task<WebApplication> CreateHostAsync(IEntityChangeNotifier notifier)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddVersioningServices().AddApplicationServices(builder.Configuration).RegisterProgramServices().RegisterApplicationServices(builder.Configuration);
		builder.Services.AddAuthorization();
		builder.Services.AddControllers().AddApplicationPart(typeof(CategoriesController).Assembly);
		builder.Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture));
		builder.Services.AddSingleton<CategoryMapper>().AddSingleton<SubcategoryMapper>();
		builder.Services.AddScoped<ICategoryRepository, CategoryRepository>().AddScoped<ISubcategoryRepository, SubcategoryRepository>();
		builder.Services.AddScoped<ICategoryService, CategoryService>().AddScoped<ISubcategoryService, SubcategoryService>();
		builder.Services.AddSingleton(notifier);
		WebApplication app = builder.Build();
		app.UseMiddleware<ValidationExceptionMiddleware>();
		app.UseAuthorization();
		app.MapControllers().AllowAnonymous();
		await app.StartAsync();
		return app;
	}

	private sealed record Seed(Guid CategoryA, Guid CategoryB, Guid SubcategoryA, Guid SubcategoryB, Guid Receipt);
	private sealed class Factory(PostgresFixture database) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => database.CreateDbContext();
	}
}
