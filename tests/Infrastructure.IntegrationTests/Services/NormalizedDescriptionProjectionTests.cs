using Application.Interfaces.Services;
using Application.Models;
using Application.Models.NormalizedDescriptions;
using Common;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class NormalizedDescriptionProjectionTests(PostgresFixture fixture)
{
	[Fact]
	public async Task GetAllAsync_DuplicateRawDescriptions_ReturnsDistinctAlphabeticalCappedSamplesOnPostgres()
	{
		await using (ApplicationDbContext cleanup = fixture.CreateDbContext())
		{
			await cleanup.Database.ExecuteSqlRawAsync(
				"""TRUNCATE "ReceiptItems", "Receipts", "NormalizedDescriptions" RESTART IDENTITY CASCADE;""");
		}

		Guid normalizedId = Guid.NewGuid();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Receipts.Add(receipt);
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normalizedId,
				CanonicalName = "Fruit spread",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			seed.ReceiptItems.AddRange(
				BuildItem(receipt.Id, normalizedId, "date spread"),
				BuildItem(receipt.Id, normalizedId, "banana spread"),
				BuildItem(receipt.Id, normalizedId, "apple spread"),
				BuildItem(receipt.Id, normalizedId, "banana spread"),
				BuildItem(receipt.Id, normalizedId, "cherry spread"));
			await seed.SaveChangesAsync();
		}

		NormalizedDescriptionService service = new(
			new FixtureContextFactory(fixture),
			new UnconfiguredEmbeddingService(),
			new NormalizedDescriptionMapper(),
			new NormalizedDescriptionSettingsMapper());

		PagedResult<NormalizedDescriptionDetail> result = await service.GetAllAsync(
			[NormalizedDescriptionStatus.Active], null, 0, 50, CancellationToken.None);

		NormalizedDescriptionDetail row = result.Data.Should().ContainSingle().Subject;
		row.LinkedItemCount.Should().Be(5);
		row.SampleRawDescriptions.Should().Equal("apple spread", "banana spread", "cherry spread");
	}

	private static ReceiptItemEntity BuildItem(Guid receiptId, Guid normalizedId, string description) => new()
	{
		Id = Guid.NewGuid(),
		ReceiptId = receiptId,
		Description = description,
		Quantity = 1,
		UnitPrice = 1,
		UnitPriceCurrency = Currency.USD,
		TotalAmount = 1,
		TotalAmountCurrency = Currency.USD,
		Category = "Groceries",
		NormalizedDescriptionId = normalizedId,
	};

	private sealed class FixtureContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}

	private sealed class UnconfiguredEmbeddingService : IEmbeddingService
	{
		public bool IsConfigured => false;

		public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken) =>
			Task.FromResult(Array.Empty<float>());

		public Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken) =>
			Task.FromResult(new List<float[]>());
	}
}
