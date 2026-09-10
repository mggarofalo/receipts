using System.Net;
using System.Net.Http.Json;
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
using SampleData;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class ReceiptArithmeticHttpTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	public static TheoryData<string> VectorIds
	{
		get { TheoryData<string> result = []; foreach (var vector in ReceiptArithmeticVectors.Cases) { result.Add(vector.Id); } return result; }
	}

	[Theory]
	[MemberData(nameof(VectorIds))]
	public Task ActualHttpCreateAndPostgresReadback_MatchSharedArithmeticAndTolerance(string id) => VerifyAsync(ReceiptArithmeticVectors.Find(id));

	[Fact]
	public Task NoTransactionCompleteReceipt_RemainsAllowedByBackend() => VerifyAsync(ReceiptArithmeticVectors.Find("two-positive-midpoint-lines"), noPayments: true);

	[Fact]
	public Task HistoricalLineTotals_RemainAuthoritativeInActualTripResponse() => VerifyAsync(ReceiptArithmeticVectors.Find("two-positive-midpoint-lines"), historicalTotals: true);

	private async Task VerifyAsync(ReceiptArithmeticVector vector, bool noPayments = false, bool historicalTotals = false)
	{
		Guid account = Guid.NewGuid(), card = Guid.NewGuid();
		string location = $"Arithmetic {Guid.NewGuid()}";
		int auditCount;
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Accounts.Add(new() { Id = account, Name = "Arithmetic account", IsActive = true });
			seed.Cards.Add(new() { Id = card, AccountId = account, CardCode = "1234", Name = "Arithmetic card", IsActive = true });
			await seed.SaveChangesAsync();
			auditCount = await seed.AuditLogs.CountAsync();
		}
		Mock<IEntityChangeNotifier> notifier = new();
		await using WebApplication app = await CreateHostAsync(notifier.Object);
		using HttpClient client = app.GetTestClient();
		double[] payments = noPayments ? [] : [.. vector.Payments];
		using HttpResponseMessage response = await client.PostAsJsonAsync("/api/receipts/complete", new
		{
			receipt = new { location, date = "2024-01-01", taxAmount = vector.Tax },
			items = vector.Lines.Select((line, index) => new { description = $"Line {index}", category = "Food", quantity = line.Quantity, unitPrice = line.UnitPrice }),
			transactions = payments.Select(amount => new { cardId = card, date = "2024-01-01", amount }),
			adjustments = vector.Adjustments.Select(amount => new { type = amount < 0 ? "discount" : "other", description = "Vector adjustment", amount })
		});
		bool accepted = noPayments || vector.IsWithinCreationTolerance;
		response.StatusCode.Should().Be(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
		if (!accepted)
		{
			using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
			problem.RootElement.GetProperty("status").GetInt32().Should().Be(400);
			problem.RootElement.GetProperty("detail").GetString().Should().Contain("Balance equation violated");
			await using ApplicationDbContext rejected = fixture.CreateDbContext();
			(await rejected.Receipts.AnyAsync(row => row.Location == location)).Should().BeFalse();
			(await rejected.AuditLogs.CountAsync()).Should().Be(auditCount);
			notifier.Verify(instance => instance.NotifyCreated("receipt", It.IsAny<Guid>()), Times.Never);
			return;
		}
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid receiptId = created.RootElement.GetProperty("receipt").GetProperty("id").GetGuid();
		notifier.Verify(instance => instance.NotifyCreated("receipt", receiptId), Times.Once);
		await using (ApplicationDbContext stored = fixture.CreateDbContext())
		{
			var items = await stored.ReceiptItems.Where(row => row.ReceiptId == receiptId).OrderBy(row => row.Description).ToListAsync();
			items.Select(row => row.TotalAmount).Should().Equal(vector.Lines.Select(line => line.Total));
			items.Select(row => row.Quantity).Should().Equal(vector.Lines.Select(line => (decimal)line.Quantity));
			items.Select(row => row.UnitPrice).Should().Equal(vector.Lines.Select(line => (decimal)line.UnitPrice));
			(await stored.Transactions.Where(row => row.ReceiptId == receiptId).SumAsync(row => row.Amount)).Should().Be(noPayments ? 0 : vector.PaymentTotal);
			if (historicalTotals)
			{
				foreach (var item in items)
				{
					item.TotalAmount = 1m;
				}

				await stored.SaveChangesAsync();
			}
		}
		using HttpResponseMessage read = await client.GetAsync($"/api/trips?receiptId={receiptId}");
		read.StatusCode.Should().Be(HttpStatusCode.OK);
		using JsonDocument trip = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
		JsonElement receipt = trip.RootElement.GetProperty("receipt");
		receipt.GetProperty("subtotal").GetDecimal().Should().Be(historicalTotals ? 2m : vector.Subtotal);
		receipt.GetProperty("adjustmentTotal").GetDecimal().Should().Be(vector.AdjustmentTotal);
		receipt.GetProperty("expectedTotal").GetDecimal().Should().Be(historicalTotals ? 2m : vector.ExpectedTotal);
		trip.RootElement.GetProperty("transactions").GetArrayLength().Should().Be(payments.Length);
	}

	private async Task<WebApplication> CreateHostAsync(IEntityChangeNotifier notifier)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddVersioningServices().AddApplicationServices(builder.Configuration).RegisterProgramServices().RegisterApplicationServices(builder.Configuration);
		builder.Services.AddAuthorization();
		builder.Services.AddControllers().AddApplicationPart(typeof(ReceiptsController).Assembly);
		builder.Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture));
		builder.Services.AddSingleton<ReceiptMapper>().AddSingleton<ReceiptItemMapper>().AddSingleton<TransactionMapper>().AddSingleton<AdjustmentMapper>().AddSingleton<AccountMapper>();
		builder.Services.AddScoped<IReceiptRepository, ReceiptRepository>().AddScoped<IReceiptItemRepository, ReceiptItemRepository>().AddScoped<ITransactionRepository, TransactionRepository>().AddScoped<IAdjustmentRepository, AdjustmentRepository>();
		builder.Services.AddScoped<IReceiptService, ReceiptService>().AddScoped<IReceiptItemService, ReceiptItemService>().AddScoped<ITransactionService, TransactionService>().AddScoped<IAdjustmentService, AdjustmentService>().AddScoped<ICompleteReceiptService, CompleteReceiptService>();
		builder.Services.AddSingleton(notifier);
		WebApplication app = builder.Build();
		app.UseMiddleware<ValidationExceptionMiddleware>();
		app.UseAuthorization();
		app.MapControllers().AllowAnonymous();
		await app.StartAsync();
		return app;
	}

	private sealed class Factory(PostgresFixture database) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => database.CreateDbContext();
	}
}
