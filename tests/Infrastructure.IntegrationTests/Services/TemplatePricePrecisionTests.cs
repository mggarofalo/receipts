using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public partial class TemplatePricePrecisionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Theory]
	[InlineData("3.459")]
	[InlineData("7.1234")]
	[InlineData("3.45")]
	public async Task TemplateAndReceiptItemUnitPrices_PreserveTheSamePrecision_AfterFreshContextRead(string input)
	{
		decimal price = decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture);
		Guid templateId = Guid.NewGuid();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.UnitPrice = price;
		item.TotalAmount = decimal.Round(price, 2);
		await using (ApplicationDbContext context = fixture.CreateDbContext())
		{
			context.Receipts.Add(receipt);
			context.ReceiptItems.Add(item);
			context.ItemTemplates.Add(new ItemTemplateEntity { Id = templateId, Name = $"Precision {templateId}", DefaultUnitPrice = price, DefaultUnitPriceCurrency = Currency.USD });
			await context.SaveChangesAsync();
		}

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		(await verify.ReceiptItems.SingleAsync(row => row.Id == item.Id)).UnitPrice.Should().Be(price, "receipt-item unit prices already support four fraction digits");
		(await verify.ItemTemplates.SingleAsync(row => row.Id == templateId)).DefaultUnitPrice.Should().Be(price, "a reusable template must preserve the same unit price as the receipt item");
	}
}
