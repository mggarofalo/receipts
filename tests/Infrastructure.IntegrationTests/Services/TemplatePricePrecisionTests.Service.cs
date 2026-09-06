using Application.Interfaces.Services;
using Common;
using Domain;
using Domain.Core;
using FluentAssertions;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public partial class TemplatePricePrecisionTests
{
	[Fact]
	public async Task ServiceCreateReadUpdateAndUnrelatedEdit_PreserveUnitPriceIncludingNullAndCents()
	{
		Mock<INormalizedDescriptionService> canonical = new();
		canonical.Setup(service => service.GetOrCreateForTemplateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Classifier unavailable in this storage fixture"));
		ItemTemplateService service = new(new ItemTemplateRepository(new Factory(fixture)), new ItemTemplateMapper(), canonical.Object);
		Guid id = Guid.NewGuid();
		await service.CreateAsync([new ItemTemplate(id, $"Service price {id}", defaultUnitPrice: new Money(3.459m, Currency.USD))], CancellationToken.None);
		(await service.GetByIdAsync(id, CancellationToken.None))!.DefaultUnitPrice!.Amount.Should().Be(3.459m);
		foreach (decimal? value in new decimal?[] { 7.1234m, 3.45m, null })
		{
			ItemTemplate template = (await service.GetByIdAsync(id, CancellationToken.None))!;
			template.DefaultUnitPrice = value is decimal price ? new Money(price, Currency.USD) : null;
			await service.UpdateAsync([template], CancellationToken.None);
			ItemTemplate reloaded = (await service.GetByIdAsync(id, CancellationToken.None))!;
			(reloaded.DefaultUnitPrice?.Amount).Should().Be(value);
			reloaded.DefaultCategory = "Changed category";
			await service.UpdateAsync([reloaded], CancellationToken.None);
			await using ApplicationDbContext verify = fixture.CreateDbContext();
			var stored = await verify.ItemTemplates.SingleAsync(row => row.Id == id);
			stored.DefaultUnitPrice.Should().Be(value);
			stored.DefaultCategory.Should().Be("Changed category");
		}
	}

	private sealed class Factory(Infrastructure.IntegrationTests.Fixtures.PostgresFixture database) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => database.CreateDbContext();
	}
}
