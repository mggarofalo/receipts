using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using API.Configuration;
using API.Controllers;
using API.Generated.Dtos;
using API.Services;
using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Infrastructure.IntegrationTests;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class BackupImportNotificationHttpTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ImportHttp_EnqueuesAfterCommittedDataIsVisible_AndNeverForRolledBackWrites(bool failAfterReceiptWrite)
	{
		Guid id = Guid.NewGuid();
		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Receipts.Add(new ReceiptEntity { Id = id, Location = "Before restore", Date = new DateOnly(2026, 1, 1), TaxAmountCurrency = Common.Currency.USD });
			await seed.SaveChangesAsync();
		}
		int auditBefore;
		await using (ApplicationDbContext read = fixture.CreateDbContext())
		{
			auditBefore = await read.AuditLogs.CountAsync(row => row.EntityId == id.ToString());
		}
		ReceiptWriteProbe writes = new();
		Mock<IEntityChangeNotifier> notifier = new();
		List<(string Location, int Audits)> visibleAtEnqueue = [];
		notifier.Setup(service => service.NotifyAllChanged("backup-import", "updated"))
			.Returns(async () =>
			{
				// This independent context has no access to the importer's transaction.
				await using ApplicationDbContext observer = fixture.CreateDbContext();
				string location = await observer.Receipts.Where(row => row.Id == id).Select(row => row.Location).SingleAsync();
				int audits = await observer.AuditLogs.CountAsync(row => row.EntityId == id.ToString());
				visibleAtEnqueue.Add((location, audits));
			});
		await using WebApplication app = await StartHostAsync(notifier.Object, writes);
		using HttpClient client = app.GetTestClient();
		using Artifact artifact = await Artifact.CreateAsync(id, failAfterReceiptWrite);
		using MultipartFormDataContent multipart = new();
		multipart.Add(new ByteArrayContent(await File.ReadAllBytesAsync(artifact.Path)), "file", "restore.db");

		using HttpResponseMessage response = await client.PostAsync("/api/backup/import", multipart);

		writes.Executed.Should().BeGreaterThan(0, "a later malformed adjustment must test rollback after an actual receipt UPDATE, not just input validation");
		await using ApplicationDbContext final = fixture.CreateDbContext();
		string finalLocation = await final.Receipts.Where(row => row.Id == id).Select(row => row.Location).SingleAsync();
		int finalAudits = await final.AuditLogs.CountAsync(row => row.EntityId == id.ToString());
		if (failAfterReceiptWrite)
		{
			response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
			finalLocation.Should().Be("Before restore");
			finalAudits.Should().Be(auditBefore);
			visibleAtEnqueue.Should().BeEmpty();
			notifier.VerifyNoOtherCalls();
		}
		else
		{
			response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
			BackupImportResponse result = (await response.Content.ReadFromJsonAsync<BackupImportResponse>())!;
			result.ReceiptsUpdated.Should().Be(1);
			finalLocation.Should().Be("Restored receipt");
			finalAudits.Should().Be(auditBefore + 1);
			visibleAtEnqueue.Should().Equal(("Restored receipt", auditBefore + 1));
			notifier.Verify(service => service.NotifyAllChanged("backup-import", "updated"), Times.Once);
			notifier.VerifyNoOtherCalls();
		}
	}

	private async Task<WebApplication> StartHostAsync(IEntityChangeNotifier notifier, ReceiptWriteProbe writes)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddVersioningServices();
		builder.Services.AddAuthorization(options => options.AddPolicy("RequireAdmin", policy => policy.RequireAssertion(_ => true)));
		builder.Services.AddControllers().AddApplicationPart(typeof(BackupController).Assembly);
		DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(writes).Options;
		builder.Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new ContextFactory(options));
		builder.Services.AddScoped<IBackupImportService, BackupImportService>();
		builder.Services.AddScoped<IBackupService, BackupService>();
		builder.Services.AddSingleton(notifier);
		WebApplication app = builder.Build();
		app.UseAuthorization();
		app.MapControllers().AllowAnonymous();
		await app.StartAsync();
		return app;
	}

	private sealed class ContextFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
	}

	private sealed class ReceiptWriteProbe : DbCommandInterceptor
	{
		public int Executed { get; private set; }
		public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("UPDATE receipts.\"Receipts\"", StringComparison.Ordinal))
			{
				Executed++;
			}

			return ValueTask.FromResult(result);
		}
	}

	private sealed class Artifact : IDisposable
	{
		public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"notification-import-{Guid.NewGuid():N}.db");
		public static async Task<Artifact> CreateAsync(Guid id, bool malformedAdjustment)
		{
			Artifact artifact = new();
			await using SqliteConnection sqlite = new(new SqliteConnectionStringBuilder { DataSource = artifact.Path, Pooling = false, ForeignKeys = true }.ToString());
			await sqlite.OpenAsync();
			await BackupService.CreateSchemaAsync(sqlite, CancellationToken.None);
			await using SqliteCommand command = sqlite.CreateCommand();
			command.CommandText = """
				INSERT INTO backup_metadata VALUES ('export_version', '5');
				INSERT INTO receipts (id, location, date, tax_amount, tax_amount_currency)
				VALUES ($id, 'Restored receipt', '2026-01-01', '0', 'USD');
				""";
			command.Parameters.AddWithValue("$id", id.ToString());
			await command.ExecuteNonQueryAsync();
			if (malformedAdjustment)
			{
				command.CommandText = "INSERT INTO adjustments (id, receipt_id, type, amount, amount_currency) VALUES ('not-a-guid', $id, 'Discount', '1', 'USD')";
				await command.ExecuteNonQueryAsync();
			}
			return artifact;
		}
		public void Dispose() => File.Delete(Path);
	}
}
