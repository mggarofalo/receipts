using System.Net;
using System.Net.Http.Json;
using API.Configuration;
using API.Controllers.Core;
using API.Generated.Dtos;
using API.Middleware;
using API.Services;
using Application.Interfaces.Services;
using Application.Services;
using Domain;
using Domain.Core;
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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NormalizedDescriptionStatus = Domain.NormalizedDescriptions.NormalizedDescriptionStatus;

namespace Infrastructure.IntegrationTests;

[Trait("Category", "Integration")]
public class UpdateOwnershipTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	public async Task ReceiptService_OrdinaryUpdates_PreserveImagePaths(int count)
	{
		List<ReceiptEntity> originals = Enumerable.Range(0, count).Select(index => new ReceiptEntity
		{
			Id = Guid.NewGuid(),
			Location = $"Original {index}",
			Date = new DateOnly(2025, 1, 1),
			OriginalImagePath = $"original-{index}.jpg",
			ProcessedImagePath = $"processed-{index}.png",
		}).ToList();
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Receipts.AddRange(originals);
			await seed.SaveChangesAsync();
		}
		ReceiptService service = new(new ReceiptRepository(new ContextFactory(fixture)), new ReceiptMapper());
		List<Receipt> updates = originals.Select(original => new Receipt(original.Id, "Changed", new DateOnly(2025, 2, 1), new Money(2.5m))).ToList();

		await service.UpdateAsync(updates, CancellationToken.None);

		await using ApplicationDbContext read = fixture.CreateDbContext();
		foreach (ReceiptEntity original in originals)
		{
			ReceiptEntity stored = await read.Receipts.SingleAsync(receipt => receipt.Id == original.Id);
			stored.Location.Should().Be("Changed");
			stored.Date.Should().Be(new DateOnly(2025, 2, 1));
			stored.TaxAmount.Should().Be(2.5m);
			stored.OriginalImagePath.Should().Be(original.OriginalImagePath);
			stored.ProcessedImagePath.Should().Be(original.ProcessedImagePath);
		}
	}

	[Theory]
	[InlineData(1, null)]
	[InlineData(2, null)]
	[InlineData(1, 0.42)]
	[InlineData(2, 0.42)]
	public async Task ItemService_UnchangedDescriptions_PreserveCanonicalIdentityAndScore(int count, double? score)
	{
		Guid receiptId = Guid.NewGuid();
		Guid canonicalId = Guid.NewGuid();
		List<ReceiptItemEntity> originals = Enumerable.Range(0, count).Select(index => new ReceiptItemEntity
		{
			Id = Guid.NewGuid(),
			ReceiptId = receiptId,
			Description = $"Milk {index}",
			Quantity = 1,
			UnitPrice = 3,
			TotalAmount = 3,
			Category = "Food",
			NormalizedDescriptionId = canonicalId,
			NormalizedDescriptionMatchScore = score,
		}).ToList();
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Receipts.Add(new ReceiptEntity { Id = receiptId, Location = "Store", Date = new DateOnly(2025, 1, 1) });
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = canonicalId,
				CanonicalName = $"Curated dairy {canonicalId}",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			seed.ReceiptItems.AddRange(originals);
			await seed.SaveChangesAsync();
		}
		ReceiptItemService service = new(new ReceiptItemRepository(new ContextFactory(fixture)), new ReceiptItemMapper());
		// Fresh domain inputs deliberately lack the server-owned normalization fields.
		List<ReceiptItem> updates = originals.Select(original => new ReceiptItem(original.Id, "new-code", original.Description,
			2, new Money(4), new Money(8), "Groceries", "Dairy")).ToList();

		await service.UpdateAsync(updates, receiptId, CancellationToken.None);

		await using ApplicationDbContext read = fixture.CreateDbContext();
		foreach (ReceiptItemEntity original in originals)
		{
			ReceiptItemEntity stored = await read.ReceiptItems.IgnoreAutoIncludes().SingleAsync(item => item.Id == original.Id);
			stored.Description.Should().Be(original.Description);
			stored.Quantity.Should().Be(2);
			stored.UnitPrice.Should().Be(4);
			stored.TotalAmount.Should().Be(8);
			stored.Category.Should().Be("Groceries");
			stored.Subcategory.Should().Be("Dairy");
			stored.ReceiptItemCode.Should().Be("new-code");
			stored.NormalizedDescriptionId.Should().Be(canonicalId);
			stored.NormalizedDescriptionMatchScore.Should().Be(score);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ReceiptHttpUpdate_SingleAndBatch_PreserveEachImages(bool batch)
	{
		List<ReceiptEntity> originals = Enumerable.Range(0, batch ? 2 : 1).Select(index => new ReceiptEntity
		{
			Id = Guid.NewGuid(),
			Location = $"Store {index}",
			Date = new DateOnly(2025, 1, 1),
			OriginalImagePath = $"{index}/original.jpg",
			ProcessedImagePath = $"{index}/processed.png",
		}).ToList();
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Receipts.AddRange(originals);
			await seed.SaveChangesAsync();
		}
		await using WebApplication app = await StartHostAsync();
		using HttpClient client = app.GetTestClient();
		List<UpdateReceiptRequest> updates = originals.Select(original => new UpdateReceiptRequest
		{
			Id = original.Id,
			Location = "Edited",
			Date = new DateOnly(2025, 2, 1),
			TaxAmount = 2.5,
		}).ToList();

		using HttpResponseMessage response = batch
			? await client.PutAsJsonAsync("/api/receipts/batch", updates)
			: await client.PutAsJsonAsync($"/api/receipts/{originals[0].Id}", updates[0]);

		response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
		await using ApplicationDbContext read = fixture.CreateDbContext();
		foreach (ReceiptEntity original in originals)
		{
			ReceiptEntity stored = await read.Receipts.SingleAsync(receipt => receipt.Id == original.Id);
			stored.Location.Should().Be("Edited");
			stored.TaxAmount.Should().Be(2.5m);
			stored.Date.Should().Be(new DateOnly(2025, 2, 1));
			stored.OriginalImagePath.Should().Be(original.OriginalImagePath);
			stored.ProcessedImagePath.Should().Be(original.ProcessedImagePath);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ItemHttpUpdate_SingleAndMixedParentBatch_KeepStoredParentAndCanonicalMetadata(bool batch)
	{
		Guid canonicalId = Guid.NewGuid();
		List<ReceiptEntity> parents = Enumerable.Range(0, batch ? 2 : 1).Select(index => new ReceiptEntity
		{
			Id = Guid.NewGuid(),
			Location = $"Parent {index}",
			Date = new DateOnly(2025, 1, 1),
		}).ToList();
		List<ReceiptItemEntity> originals = parents.Select((parent, index) => new ReceiptItemEntity
		{
			Id = Guid.NewGuid(),
			ReceiptId = parent.Id,
			Description = $"Curated item {index}",
			Quantity = 1,
			UnitPrice = 3,
			TotalAmount = 3,
			Category = "Food",
			NormalizedDescriptionId = canonicalId,
			NormalizedDescriptionMatchScore = index == 0 ? null : 0.42,
		}).ToList();
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Receipts.AddRange(parents);
			seed.NormalizedDescriptions.Add(Canonical(canonicalId, $"Curated {canonicalId}"));
			seed.ReceiptItems.AddRange(originals);
			await seed.SaveChangesAsync();
		}
		await using WebApplication app = await StartHostAsync();
		using HttpClient client = app.GetTestClient();
		List<UpdateReceiptItemRequest> updates = originals.Select(original => new UpdateReceiptItemRequest
		{
			Id = original.Id,
			Description = original.Description,
			Quantity = 2,
			UnitPrice = 4,
			Category = "Groceries",
			Subcategory = "Dairy",
			ReceiptItemCode = "new-code",
		}).ToList();

		using HttpResponseMessage response = batch
			? await client.PutAsJsonAsync("/api/receipt-items/batch", updates)
			: await client.PutAsJsonAsync($"/api/receipt-items/{originals[0].Id}", updates[0]);

		response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
		await using ApplicationDbContext read = fixture.CreateDbContext();
		foreach (ReceiptItemEntity original in originals)
		{
			ReceiptItemEntity stored = await read.ReceiptItems.IgnoreAutoIncludes().SingleAsync(item => item.Id == original.Id);
			stored.ReceiptId.Should().Be(original.ReceiptId, "the first batch item's parent must not be applied to other items");
			stored.Description.Should().Be(original.Description);
			stored.Quantity.Should().Be(2);
			stored.UnitPrice.Should().Be(4);
			stored.TotalAmount.Should().Be(8);
			stored.Category.Should().Be("Groceries");
			stored.Subcategory.Should().Be("Dairy");
			stored.ReceiptItemCode.Should().Be("new-code");
			stored.NormalizedDescriptionId.Should().Be(original.NormalizedDescriptionId);
			stored.NormalizedDescriptionMatchScore.Should().Be(original.NormalizedDescriptionMatchScore);
		}
	}

	[Fact]
	public async Task MixedBatch_OnlyChangedDescriptionIsInvalidated_ThenRealResolverLinksNewCanonical()
	{
		Guid receiptId = Guid.NewGuid(), changedId = Guid.NewGuid(), unchangedId = Guid.NewGuid(), milkId = Guid.NewGuid(), breadId = Guid.NewGuid();
		string token = Guid.NewGuid().ToString("N");
		string milk = $"Milk {token}", bread = $"Bread {token}", unchanged = $"Curated dairy {token}";
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Receipts.Add(new ReceiptEntity { Id = receiptId, Location = "Store", Date = new DateOnly(2025, 1, 1) });
			seed.NormalizedDescriptions.AddRange(Canonical(milkId, milk), Canonical(breadId, bread));
			seed.ReceiptItems.AddRange(
				new ReceiptItemEntity { Id = changedId, ReceiptId = receiptId, Description = milk, Quantity = 1, UnitPrice = 3, TotalAmount = 3, Category = "Food", NormalizedDescriptionId = milkId, NormalizedDescriptionMatchScore = 0.42 },
				new ReceiptItemEntity { Id = unchangedId, ReceiptId = receiptId, Description = unchanged, Quantity = 1, UnitPrice = 3, TotalAmount = 3, Category = "Food", NormalizedDescriptionId = milkId, NormalizedDescriptionMatchScore = 0.71 });
			await seed.SaveChangesAsync();
		}
		DescriptionChangeSignal signal = new();
		var subscriber = signal.Subscribe();
		ContextFactory factory = new(fixture, signal);
		ReceiptItemService service = new(new ReceiptItemRepository(factory), new ReceiptItemMapper());
		ReceiptItem unchangedEdit = new(unchangedId, null, unchanged, 2, new Money(3), new Money(6), "Groceries", null);
		await service.UpdateAsync([unchangedEdit], receiptId, CancellationToken.None);
		subscriber.TryRead(out _).Should().BeFalse("quantity/category changes must not reconcile unchanged descriptions");

		await service.UpdateAsync([unchangedEdit, new ReceiptItem(changedId, null, bread, 1, new Money(3), new Money(3), "Food", null)], receiptId, CancellationToken.None);

		subscriber.TryRead(out _).Should().BeTrue("the raw-text edit must wake description consumers");
		await using (ApplicationDbContext read = fixture.CreateDbContext())
		{
			ReceiptItemEntity changed = await read.ReceiptItems.IgnoreAutoIncludes().SingleAsync(item => item.Id == changedId);
			changed.NormalizedDescriptionId.Should().BeNull();
			changed.NormalizedDescriptionMatchScore.Should().BeNull();
			ReceiptItemEntity retained = await read.ReceiptItems.IgnoreAutoIncludes().SingleAsync(item => item.Id == unchangedId);
			retained.NormalizedDescriptionId.Should().Be(milkId);
			retained.NormalizedDescriptionMatchScore.Should().Be(0.71);
			(await read.DistinctDescriptions.AnyAsync(description => description.Description == milk)).Should().BeFalse();
			(await read.DistinctDescriptions.AnyAsync(description => description.Description == bread)).Should().BeTrue();
		}
		Mock<IEmbeddingService> embeddings = new(MockBehavior.Strict);
		embeddings.SetupGet(service => service.IsConfigured).Returns(true);
		ServiceCollection services = new();
		services.AddSingleton<IEmbeddingService>(embeddings.Object);
		services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(factory);
		services.AddSingleton<NormalizedDescriptionMapper>();
		services.AddSingleton<NormalizedDescriptionSettingsMapper>();
		services.AddScoped<INormalizedDescriptionService, NormalizedDescriptionService>();
		await using ServiceProvider provider = services.BuildServiceProvider();
		using NormalizedDescriptionResolutionService resolver = new(provider.GetRequiredService<IServiceScopeFactory>(), signal, NullLogger<NormalizedDescriptionResolutionService>.Instance);

		await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		ReceiptItemEntity resolved = await verify.ReceiptItems.IgnoreAutoIncludes().SingleAsync(item => item.Id == changedId);
		resolved.NormalizedDescriptionId.Should().Be(breadId);
		resolved.NormalizedDescriptionMatchScore.Should().Be(1.0);
		ReceiptItemEntity stillCurated = await verify.ReceiptItems.IgnoreAutoIncludes().SingleAsync(item => item.Id == unchangedId);
		stillCurated.NormalizedDescriptionId.Should().Be(milkId);
		stillCurated.NormalizedDescriptionMatchScore.Should().Be(0.71);
		embeddings.Verify(service => service.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
		embeddings.Verify(service => service.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	private static NormalizedDescriptionEntity Canonical(Guid id, string name) => new()
	{
		Id = id,
		CanonicalName = name,
		Status = NormalizedDescriptionStatus.Active,
		CreatedAt = DateTimeOffset.UtcNow,
	};

	private async Task<WebApplication> StartHostAsync()
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddVersioningServices().AddApplicationServices(builder.Configuration)
			.RegisterProgramServices().RegisterApplicationServices(builder.Configuration);
		builder.Services.AddAuthorization();
		builder.Services.AddControllers().AddApplicationPart(typeof(ReceiptsController).Assembly);
		builder.Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new ContextFactory(fixture));
		builder.Services.AddScoped<IReceiptRepository, ReceiptRepository>();
		builder.Services.AddScoped<IReceiptItemRepository, ReceiptItemRepository>();
		builder.Services.AddSingleton<ReceiptMapper>();
		builder.Services.AddSingleton<ReceiptItemMapper>();
		builder.Services.AddScoped<IReceiptService, ReceiptService>();
		builder.Services.AddScoped<IReceiptItemService, ReceiptItemService>();
		builder.Services.AddSingleton(Mock.Of<IEntityChangeNotifier>());
		WebApplication app = builder.Build();
		app.UseMiddleware<ValidationExceptionMiddleware>();
		app.UseAuthorization();
		app.MapControllers().AllowAnonymous();
		await app.StartAsync();
		return app;
	}

	private sealed class ContextFactory(PostgresFixture database, IDescriptionChangeSignal? signal = null) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => signal is null
			? database.CreateDbContext()
			: new ApplicationDbContext(database.CreateOptions(), Mock.Of<ICurrentUserAccessor>(), signal);
	}
}
