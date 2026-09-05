using System.Net;
using System.Net.Http.Json;
using API.Configuration;
using API.Controllers.Core;
using API.Generated.Dtos;
using API.Middleware;
using API.Services;
using Application.Interfaces.Services;
using Application.Services;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Interfaces.Repositories;
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
public class BatchValidationAtomicityTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	private async Task<WebApplication> StartAsync()
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
		builder.Services.AddSingleton<Infrastructure.Mapping.ReceiptMapper>();
		builder.Services.AddScoped<IReceiptService, ReceiptService>();
		builder.Services.AddSingleton(Mock.Of<IEntityChangeNotifier>());
		WebApplication app = builder.Build();
		app.UseMiddleware<ValidationExceptionMiddleware>();
		app.UseAuthorization();
		app.MapControllers().AllowAnonymous();
		await app.StartAsync();
		return app;
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task InvalidCreateBatch_PersistsNoRows_WhileValidBatchPersistsEveryElement(bool futureDate)
	{
		await using WebApplication app = await StartAsync();
		using HttpClient client = app.GetTestClient();
		await using ApplicationDbContext context = fixture.CreateDbContext();
		int before = await context.Receipts.CountAsync();
		int auditBefore = await context.AuditLogs.CountAsync();
		CreateReceiptRequest first = Request("first");
		CreateReceiptRequest second = Request(futureDate ? "second" : new string('x', 201));
		if (futureDate)
		{
			second.Date = DateOnly.FromDateTime(DateTime.Today.AddDays(2));
		}

		using HttpResponseMessage invalid = await client.PostAsJsonAsync("/api/receipts/batch", new[] { first, second });

		invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest, await invalid.Content.ReadAsStringAsync());
		(await context.Receipts.CountAsync()).Should().Be(before);
		(await context.AuditLogs.CountAsync()).Should().Be(auditBefore);
		second.Location = "second";
		second.Date = new DateOnly(2025, 1, 1);
		using HttpResponseMessage valid = await client.PostAsJsonAsync("/api/receipts/batch", new[] { first, second });
		valid.StatusCode.Should().Be(HttpStatusCode.OK, await valid.Content.ReadAsStringAsync());
		(await context.Receipts.CountAsync()).Should().Be(before + 2);
		List<ReceiptResponse> created = (await valid.Content.ReadFromJsonAsync<List<ReceiptResponse>>())!;
		created.Should().HaveCount(2);
		created.Select(receipt => receipt.Location).Should().BeEquivalentTo(["first", "second"]);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task InvalidUpdateBatch_ChangesNeitherRow_WhileValidBatchUpdatesBoth(bool futureDate)
	{
		await using WebApplication app = await StartAsync();
		using HttpClient client = app.GetTestClient();
		using HttpResponseMessage seed = await client.PostAsJsonAsync("/api/receipts/batch", new[] { Request("original first"), Request("original second") });
		seed.StatusCode.Should().Be(HttpStatusCode.OK, await seed.Content.ReadAsStringAsync());
		List<ReceiptResponse> originals = (await seed.Content.ReadFromJsonAsync<List<ReceiptResponse>>())!;
		UpdateReceiptRequest first = Update(originals[0], "changed first");
		UpdateReceiptRequest second = Update(originals[1], futureDate ? "changed second" : new string('x', 201));
		if (futureDate)
		{
			second.Date = DateOnly.FromDateTime(DateTime.Today.AddDays(2));
		}

		using HttpResponseMessage invalid = await client.PutAsJsonAsync("/api/receipts/batch", new[] { first, second });

		invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest, await invalid.Content.ReadAsStringAsync());
		await using ApplicationDbContext context = fixture.CreateDbContext();
		List<Guid> ids = originals.Select(receipt => receipt.Id).ToList();
		List<ReceiptEntity> unchanged = await context.Receipts.AsNoTracking().Where(receipt => ids.Contains(receipt.Id)).ToListAsync();
		foreach (ReceiptResponse original in originals)
		{
			ReceiptEntity stored = unchanged.Single(receipt => receipt.Id == original.Id);
			stored.Location.Should().Be(original.Location);
			stored.Date.Should().Be(original.Date);
		}
		second.Location = "changed second";
		second.Date = new DateOnly(2025, 1, 1);
		using HttpResponseMessage valid = await client.PutAsJsonAsync("/api/receipts/batch", new[] { first, second });
		valid.StatusCode.Should().Be(HttpStatusCode.NoContent, await valid.Content.ReadAsStringAsync());
		(await context.Receipts.AsNoTracking().Where(receipt => ids.Contains(receipt.Id)).Select(receipt => receipt.Location).ToListAsync())
			.Should().BeEquivalentTo(["changed first", "changed second"]);
	}

	private static CreateReceiptRequest Request(string location) => new() { Location = location, Date = new DateOnly(2025, 1, 1), TaxAmount = 0 };
	private static UpdateReceiptRequest Update(ReceiptResponse original, string location) => new()
	{
		Id = original.Id,
		Location = location,
		Date = original.Date,
		TaxAmount = original.TaxAmount,
	};
	private sealed class ContextFactory(PostgresFixture database) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => database.CreateDbContext();
	}
}
