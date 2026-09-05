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

namespace Infrastructure.IntegrationTests;

[Trait("Category", "Integration")]
public class TransactionAccountHttpTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Theory]
	[InlineData("single")]
	[InlineData("batch")]
	[InlineData("complete")]
	public async Task HttpWritesAndReads_DeriveAccountFromCard_IgnoringContradictoryLegacyInput(string mode)
	{
		Guid account = Guid.NewGuid(), replacement = Guid.NewGuid(), card = Guid.NewGuid(), replacementCard = Guid.NewGuid(), receipt = Guid.NewGuid();
		DateOnly date = new(2025, 1, 1);
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Accounts.AddRange(new() { Id = account, Name = "Owner", IsActive = true }, new() { Id = replacement, Name = "Replacement", IsActive = true });
			seed.Cards.AddRange(new() { Id = card, AccountId = account, CardCode = "1234", Name = "First", IsActive = true }, new() { Id = replacementCard, AccountId = replacement, CardCode = "5678", Name = "Second", IsActive = true });
			if (mode != "complete")
			{
				seed.Receipts.Add(new() { Id = receipt, Location = "Payment owner", Date = date, TaxAmount = 10 });
			}

			await seed.SaveChangesAsync();
		}
		await using WebApplication app = await StartHostAsync();
		using HttpClient client = app.GetTestClient();
		int count = mode == "single" ? 1 : 2;
		var payment = new { accountId = replacement, cardId = card, amount = 10d / count, date };
		using HttpResponseMessage created = mode switch
		{
			"single" => await client.PostAsJsonAsync($"/api/receipts/{receipt}/transactions", payment),
			"batch" => await client.PostAsJsonAsync($"/api/receipts/{receipt}/transactions/batch", new[] { payment, payment }),
			_ => await client.PostAsJsonAsync("/api/receipts/complete", new { receipt = new { location = "Complete owner", date, taxAmount = 10 }, transactions = new[] { payment, payment }, items = Array.Empty<object>(), adjustments = Array.Empty<object>() }),
		};
		created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
		using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
		JsonElement[] payments = mode == "single" ? [json.RootElement] : (mode == "complete" ? json.RootElement.GetProperty("transactions") : json.RootElement).EnumerateArray().ToArray();
		payments.Should().HaveCount(count);
		payments.Should().OnlyContain(row => row.GetProperty("accountId").GetGuid() == account && row.GetProperty("cardId").GetGuid() == card);
		if (mode == "complete")
		{
			receipt = json.RootElement.GetProperty("receipt").GetProperty("id").GetGuid();
		}

		Guid id = payments[0].GetProperty("id").GetGuid();

		using HttpResponseMessage updated = await client.PutAsJsonAsync($"/api/transactions/{id}", new { id, accountId = account, cardId = replacementCard, amount = 10d / count, date });
		updated.StatusCode.Should().Be(HttpStatusCode.NoContent, await updated.Content.ReadAsStringAsync());
		using JsonDocument single = JsonDocument.Parse(await client.GetStringAsync($"/api/transactions/{id}"));
		single.RootElement.GetProperty("accountId").GetGuid().Should().Be(replacement);
		foreach (string url in new[] { $"/api/transactions?receiptId={receipt}", "/api/transactions?limit=500" })
		{
			using JsonDocument list = JsonDocument.Parse(await client.GetStringAsync(url));
			list.RootElement.GetProperty("data").EnumerateArray().Single(row => row.GetProperty("id").GetGuid() == id).GetProperty("accountId").GetGuid().Should().Be(replacement);
		}
		await using ApplicationDbContext read = fixture.CreateDbContext();
		var stored = await read.Transactions.IgnoreAutoIncludes().SingleAsync(row => row.Id == id);
		stored.CardId.Should().Be(replacementCard);
		read.Model.FindEntityType(stored.GetType())!.FindProperty("AccountId").Should().BeNull("there must be no second persisted source of account ownership");
	}

	private async Task<WebApplication> StartHostAsync()
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddVersioningServices().AddApplicationServices(builder.Configuration).RegisterProgramServices().RegisterApplicationServices(builder.Configuration);
		builder.Services.AddAuthorization();
		builder.Services.AddControllers().AddApplicationPart(typeof(TransactionsController).Assembly);
		builder.Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture));
		builder.Services.AddScoped<ITransactionRepository, TransactionRepository>();
		builder.Services.AddScoped<ITransactionService, TransactionService>();
		builder.Services.AddScoped<ICompleteReceiptService, CompleteReceiptService>();
		builder.Services.AddSingleton<TransactionMapper>();
		builder.Services.AddSingleton<AccountMapper>();
		builder.Services.AddSingleton<ReceiptMapper>();
		builder.Services.AddSingleton<ReceiptItemMapper>();
		builder.Services.AddSingleton<AdjustmentMapper>();
		builder.Services.AddSingleton(Mock.Of<IEntityChangeNotifier>());
		WebApplication app = builder.Build();
		app.UseMiddleware<ValidationExceptionMiddleware>();
		app.UseAuthorization();
		app.MapControllers().AllowAnonymous();
		await app.StartAsync();
		return app;
	}

	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
