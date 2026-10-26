using System.Net;
using System.Text;
using System.Text.Json;
using Application.Classification;
using Application.Taxonomy;
using Common;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.NormalizationAi;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Jev;
using Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class CategoryAssistanceDispatchStoreTests
{
	private PostgresFixture fixture;

	public CategoryAssistanceDispatchStoreTests(PostgresFixture server)
	{
		fixture = server;
	}

	[Fact]
	public async Task WorkerSendsOneBoundedRequestFromDurableConsentAndPersistsSuggestion()
	{
		// Keep the transport boundary case independent of categories accumulated by other tests.
		await using var isolated = await fixture.CreateIsolatedAsync();
		fixture = isolated;
		var seed = await SeedAsync("worker acceptance " + Guid.NewGuid());
		try
		{
			var handler = new CountingSuggestionHandler(seed.CategoryId);
			var options = new JevOptions { Enabled = true, ApiKey = "jev-test-key" };
			var monitor = new Mock<IOptionsMonitor<JevOptions>>();
			monitor.SetupGet(x => x.CurrentValue).Returns(() => options);
			var coordinator = Connection();
			ServiceCollection registrations = new();
			registrations.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture));
			registrations.AddSingleton<ICategoryAssistanceSettingsStore>(Settings());
			registrations.AddSingleton<ICategoryAssistanceConnectionReconciler>(coordinator);
			registrations.AddSingleton<ICategoryAssistanceConnectionBinding>(coordinator);
			registrations.AddSingleton<ICategoryAssistanceDispatchStore>(Dispatch());
			registrations.AddSingleton<IOptionsMonitor<JevOptions>>(monitor.Object);
			registrations.AddSingleton(new JevSuggestionConcurrency());
			registrations.AddSingleton(new HttpClient(handler));
			registrations.AddSingleton<JevSuggestionProvider>();
			registrations.AddSingleton<JevCategoryAssistanceSelectedProvider>();
			registrations.AddSingleton<ICategoryAssistanceSelectedProvider>(p => p.GetRequiredService<JevCategoryAssistanceSelectedProvider>());
			registrations.AddSingleton<ICategoryAssistanceCapabilitySource>(p => p.GetRequiredService<JevCategoryAssistanceSelectedProvider>());
			registrations.AddSingleton<TimeProvider>(TimeProvider.System);
			registrations.AddScoped<CategoryAssistanceDispatcher>();
			using ServiceProvider services = registrations.BuildServiceProvider();
			var worker = new CategoryAssistanceWorker(services.GetRequiredService<IServiceScopeFactory>(),
					monitor.Object, NullLogger<CategoryAssistanceWorker>.Instance);

			options.Enabled = false;
			(await worker.RunOnceAsync()).Should().BeFalse();
			options.Enabled = true;
			options.ApiKey = null;
			(await worker.RunOnceAsync()).Should().BeFalse();
			handler.Calls.Should().Be(0);
			options.ApiKey = "jev-test-key";
			(await worker.RunOnceAsync()).Should().BeTrue();
			handler.Calls.Should().Be(1);
			(await worker.RunOnceAsync()).Should().BeFalse();
			handler.Calls.Should().Be(1);
			await using (var verify = fixture.CreateDbContext())
			{
				var attempt = await verify.CategoryAssistanceAttempts.AsNoTracking()
						.SingleAsync(x => x.ItemId == seed.ItemId);
				attempt.State.Should().Be("suggested");
				attempt.ResultJson.Should().NotBeNull();
				(await verify.CategoryAssistanceDailyUsage.Where(x => x.UtcDay == DateOnly.FromDateTime(DateTime.UtcNow))
						.Select(x => x.DispatchAttempts).SingleAsync()).Should().Be(1);
			}

			Guid revokedItem = Guid.NewGuid();
			await using (var db = fixture.CreateDbContext())
			{
				db.ReceiptItems.Add(new ReceiptItemEntity
				{
					Id = revokedItem,
					ReceiptId = seed.ReceiptId,
					Description = "revoked pending " + Guid.NewGuid(),
					Category = "",
					UnitPriceCurrency = Currency.USD,
					TotalAmountCurrency = Currency.USD
				});
				await db.SaveChangesAsync();
			}
			await using (var db = fixture.CreateDbContext())
			await using (var tx = await db.Database.BeginTransactionAsync())
			{
				(await CategoryAssistanceObservationWriter.RecordNoChoiceSaveAsync(db,
						seed.Actor, Guid.NewGuid(), revokedItem, false, false)).Should().BeTrue();
				await tx.CommitAsync();
			}
			await DisableAsync(seed.Actor);
			(await worker.RunOnceAsync()).Should().BeFalse();
			options.Enabled = false;
			(await worker.RunOnceAsync()).Should().BeFalse();
			options.Enabled = true;
			options.ApiKey = null;
			(await worker.RunOnceAsync()).Should().BeFalse();
			handler.Calls.Should().Be(1);
		}
		finally { await DisableAsync(seed.Actor); }
	}

	private sealed class CountingSuggestionHandler(Guid categoryId) : HttpMessageHandler
	{
		public int Calls { get; private set; }
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			Calls++;
			request.RequestUri!.AbsoluteUri.Should().Be("https://api.typesafe.ai/v1/systemone");
			request.Headers.Authorization!.Parameter.Should().Be("jev-test-key");
			string id = categoryId.ToString("D");
			using var requestBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
			var probabilities = requestBody.RootElement.GetProperty("state").GetProperty("categories")
				.EnumerateArray().Select(category => category.GetProperty("id").GetString()!)
				.ToDictionary(category => category, category => category == id ? 1.0 : 0.0);
			probabilities.Should().ContainKey(id);
			string body = JsonSerializer.Serialize(new
			{
				model = "jev-1.14.0",
				answers = new
				{
					category = new
					{
						type = "choice",
						choice = id,
						confidence = 0.9,
						probabilities
					},
					fits = new { type = "noul", noul = 0.9 }
				},
				usage = new { input_tokens = 1, output_tokens = 1 }
			});
			return new HttpResponseMessage(HttpStatusCode.OK)
			{ Content = new StringContent(body, Encoding.UTF8, "application/json") };
		}
	}
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }
	private sealed class FaultFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => new FaultContext(fixture.CreateOptions()); }
	private sealed class FaultContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
	{
		public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
		{
			int result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
			if (CategoryAssistanceAttempts.Local.Any(x => x.State == "dispatching"))
			{
				throw new InvalidOperationException("Injected failure after debit and state write");
			}

			return result;
		}
	}
	private CategoryAssistanceDispatchStore Dispatch() => new(new Factory(fixture), TimeProvider.System);
	private CategoryAssistanceConnectionCoordinator Connection() => new(new Factory(fixture));
	private static CategoryAssistanceConnectionCoordinator.Configuration Binding(string credential = "jev-test-key", string server = "https://example.test/")
		=> new("jev", "jev-1.14.0", credential, new Uri(server), "pinned-transport");
	private CategoryAssistanceSettingsStore Settings() => new(new Factory(fixture), TimeProvider.System);
	private static CategoryAssistanceCapability Ready => new("jev", CategoryAssistanceCapabilityState.Ready, ["jev-1.14.0"]);

	private async Task ImportCurrentBackupAsync()
	{
		string path = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance)
			.ExportToSqliteAsync();
		try
		{
			await using var input = File.OpenRead(path);
			await new BackupImportService(new Factory(fixture), NullLogger<BackupImportService>.Instance)
				.ImportFromSqliteAsync(input, default);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public async Task PortableRestoreRevokesQueuedClaimWithoutDebit()
	{
		var seed = await SeedAsync("queued restore " + Guid.NewGuid());
		var store = Dispatch();
		var work = (await store.ClaimQueuedAsync(default))!;
		var day = DateOnly.FromDateTime(DateTime.UtcNow);
		await using var beforeDb = fixture.CreateDbContext();
		int before = await beforeDb.CategoryAssistanceDailyUsage.Where(x => x.UtcDay == day)
			.Select(x => x.DispatchAttempts).SingleOrDefaultAsync();
		Guid priorRestore = (await beforeDb.CategoryAssistanceSettings.AsNoTracking().SingleAsync()).RestoreGeneration;
		Guid priorConnection = (await beforeDb.CategoryAssistanceConnection.AsNoTracking().SingleAsync()).Generation;

		await ImportCurrentBackupAsync();

		(await store.TryStartDispatchAsync(work, Ready, DateTimeOffset.UtcNow, default)).Should().BeNull();
		await using var verify = fixture.CreateDbContext();
		var settings = await verify.CategoryAssistanceSettings.AsNoTracking().SingleAsync();
		settings.Enabled.Should().BeFalse();
		settings.RestoreGeneration.Should().NotBe(priorRestore);
		var connection = await verify.CategoryAssistanceConnection.AsNoTracking().SingleAsync();
		connection.Generation.Should().NotBe(priorConnection);
		connection.Changing.Should().BeTrue();
		(await verify.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == work.AttemptId))
			.State.Should().Be("stale");
		(await verify.CategoryAssistanceDailyUsage.Where(x => x.UtcDay == day)
			.Select(x => x.DispatchAttempts).SingleOrDefaultAsync()).Should().Be(before);
		(await verify.CategoryAssistanceSettingsAudits.CountAsync(x => x.Actor == "backup_restore"))
			.Should().BeGreaterThan(0);
	}

	[Fact]
	public async Task PortableRestoreMakesStartedClaimIndeterminateAndRetainsDebit()
	{
		var seed = await SeedAsync("started restore " + Guid.NewGuid());
		var store = Dispatch();
		var work = (await store.ClaimQueuedAsync(default))!;
		var claim = (await store.TryStartDispatchAsync(work, Ready, DateTimeOffset.UtcNow, default))!;
		var day = DateOnly.FromDateTime(DateTime.UtcNow);
		await using var beforeDb = fixture.CreateDbContext();
		int debited = (await beforeDb.CategoryAssistanceDailyUsage.SingleAsync(x => x.UtcDay == day)).DispatchAttempts;

		await ImportCurrentBackupAsync();
		await store.CompleteAsync(claim, new(CategoryAssistanceAttemptOutcome.Abstained), Ready, default);

		await using var verify = fixture.CreateDbContext();
		var attempt = await verify.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == work.AttemptId);
		attempt.State.Should().Be("indeterminate");
		attempt.ResultJson.Should().BeNull();
		attempt.ClaimToken.Should().BeNull();
		(await verify.CategoryAssistanceDailyUsage.SingleAsync(x => x.UtcDay == day))
			.DispatchAttempts.Should().Be(debited);
	}

	[Fact]
	public async Task ObservationUsesCurrentReceiptWriterIdentityWithoutAdminRole()
	{
		var seed = await SeedAsync("writer policy " + Guid.NewGuid());
		string writer = "assistance-writer-" + Guid.NewGuid();
		Guid allowedItem = Guid.NewGuid(), lockedItem = Guid.NewGuid();
		try
		{
			await using (var db = fixture.CreateDbContext())
			{
				db.Users.Add(new ApplicationUser { Id = writer, UserName = writer });
				db.ReceiptItems.AddRange(
					new ReceiptItemEntity
					{
						Id = allowedItem,
						ReceiptId = seed.ReceiptId,
						Description = "ordinary writer " + Guid.NewGuid(),
						Category = "",
						UnitPriceCurrency = Currency.USD,
						TotalAmountCurrency = Currency.USD
					},
					new ReceiptItemEntity
					{
						Id = lockedItem,
						ReceiptId = seed.ReceiptId,
						Description = "locked writer " + Guid.NewGuid(),
						Category = "",
						UnitPriceCurrency = Currency.USD,
						TotalAmountCurrency = Currency.USD
					});
				await db.SaveChangesAsync();
			}
			await using (var db = fixture.CreateDbContext())
			await using (var tx = await db.Database.BeginTransactionAsync())
			{
				(await CategoryAssistanceObservationWriter.RecordNoChoiceSaveAsync(db, writer, Guid.NewGuid(), allowedItem, false, false))
					.Should().BeTrue();
				await tx.CommitAsync();
			}
			await using (var db = fixture.CreateDbContext())
			{
				var user = await db.Users.SingleAsync(x => x.Id == writer);
				user.LockoutEnabled = true;
				user.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1);
				await db.SaveChangesAsync();
			}
			await using (var db = fixture.CreateDbContext())
			await using (var tx = await db.Database.BeginTransactionAsync())
			{
				(await CategoryAssistanceObservationWriter.RecordNoChoiceSaveAsync(db, writer, Guid.NewGuid(), lockedItem, false, false))
					.Should().BeFalse();
				await tx.CommitAsync();
			}
		}
		finally { await DisableAsync(seed.Actor); }
	}

	[Fact]
	public async Task ChatGptConnectionRotationDoesNotFenceJevWork()
	{
		var seed = await SeedAsync("independent connection " + Guid.NewGuid());
		try
		{
			var store = Dispatch();
			var work = (await store.ClaimQueuedAsync(default))!;
			await using (var db = fixture.CreateDbContext())
			{
				var chatGpt = await db.NormalizationAiConnection.SingleAsync();
				chatGpt.Generation = Guid.NewGuid();
				chatGpt.Changing = true;
				await db.SaveChangesAsync();
			}
			(await Connection().IsCurrentAsync(Binding(), work.Fence.ConnectionGeneration)).Should().BeTrue();
			var claim = await store.TryStartDispatchAsync(work, Ready, DateTimeOffset.UtcNow, default);
			claim.Should().NotBeNull();
			await store.CompleteAsync(claim!, new(CategoryAssistanceAttemptOutcome.Abstained), Ready, default);
			await using var verify = fixture.CreateDbContext();
			(await verify.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == work.AttemptId))
				.State.Should().Be("abstained");
		}
		finally { await DisableAsync(seed.Actor); }
	}

	[Fact]
	public async Task UnknownBindingFailsClosedAndServerOnlyRotationChangesGeneration()
	{
		var seed = await SeedAsync("unknown binding " + Guid.NewGuid());
		try
		{
			var store = Dispatch();
			await Connection().InvalidateAsync();
			(await store.ClaimQueuedAsync(default)).Should().BeNull();
			await using (var db = fixture.CreateDbContext())
			{
				(await db.CategoryAssistanceAttempts.CountAsync(x => x.ItemId == seed.ItemId)).Should().Be(0);
				var row = await db.CategoryAssistanceConnection.AsNoTracking().SingleAsync();
				row.Changing.Should().BeTrue();
				row.ConfigurationFingerprint.Should().BeEmpty();
			}
			var first = await Connection().ReconcileAsync(Binding());
			var second = await Connection().ReconcileAsync(Binding(server: "https://other.example.test/"));
			second.Should().NotBe(first);
			(await Connection().IsCurrentAsync(Binding(), first)).Should().BeFalse();
			(await Connection().IsCurrentAsync(Binding(server: "https://other.example.test/"), second)).Should().BeTrue();
			await using var verify = fixture.CreateDbContext();
			var connection = await verify.CategoryAssistanceConnection.AsNoTracking().SingleAsync();
			connection.ConfigurationFingerprint.Should().HaveLength(64);
			connection.ConfigurationFingerprint.Should().NotContain("jev-test-key");
			connection.ConfigurationFingerprint.Should().NotContain("other.example.test");
		}
		finally { await DisableAsync(seed.Actor); await Connection().InvalidateAsync(); }
	}

	[Fact]
	public async Task ConcurrentTransportRotationsLeaveOnlyOneCurrentGeneration()
	{
		var seed = await SeedAsync("concurrent transport rotation " + Guid.NewGuid());
		try
		{
			var firstBinding = Binding("first-key", "https://first.example.test/");
			var secondBinding = Binding("second-key", "https://second.example.test/");
			var generations = await Task.WhenAll(Connection().ReconcileAsync(firstBinding),
				Connection().ReconcileAsync(secondBinding));

			generations[0].Should().NotBe(generations[1]);
			var current = await Task.WhenAll(
				Connection().IsCurrentAsync(firstBinding, generations[0]),
				Connection().IsCurrentAsync(secondBinding, generations[1]));
			current.Count(value => value).Should().Be(1);

			await using var db = fixture.CreateDbContext();
			var row = await db.CategoryAssistanceConnection.AsNoTracking().SingleAsync();
			generations.Should().Contain(row.Generation);
			row.Changing.Should().BeFalse();
		}
		finally { await DisableAsync(seed.Actor); await Connection().InvalidateAsync(); }
	}

	[Fact]
	public async Task AssistanceRotationFencesQueuedAndInFlightWorkWithoutExtraDebitOrVisibleProposal()
	{
		var seed = await SeedAsync("assistance rotation " + Guid.NewGuid());
		try
		{
			var store = Dispatch();
			var day = DateOnly.FromDateTime(DateTime.UtcNow);
			await using var baseline = fixture.CreateDbContext();
			int before = await baseline.CategoryAssistanceDailyUsage.Where(x => x.UtcDay == day)
				.Select(x => x.DispatchAttempts).SingleOrDefaultAsync();
			var queued = (await store.ClaimQueuedAsync(default))!;
			var first = queued.Fence.ConnectionGeneration;
			var second = await Connection().ReconcileAsync(Binding("jev-rotated-key"));
			second.Should().NotBe(first);
			(await Connection().IsCurrentAsync(Binding(), first)).Should().BeFalse();
			(await store.TryStartDispatchAsync(queued, Ready, DateTimeOffset.UtcNow, default)).Should().BeNull();
			await store.DeferQueuedAsync(queued, CategoryAssistanceQueuedDeferral.StartRejected, DateTimeOffset.UtcNow, default);
			await using (var verifyQueued = fixture.CreateDbContext())
			{
				var attempt = await verifyQueued.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == queued.AttemptId);
				attempt.State.Should().Be("stale"); attempt.DebitUtcDay.Should().BeNull();
				(await verifyQueued.CategoryAssistanceDailyUsage.Where(x => x.UtcDay == day)
					.Select(x => x.DispatchAttempts).SingleOrDefaultAsync()).Should().Be(before);
			}
			await Connection().ReconcileAsync(Binding());
			Guid secondItem = Guid.NewGuid();
			await using (var db = fixture.CreateDbContext())
			{
				db.ReceiptItems.Add(new ReceiptItemEntity
				{
					Id = secondItem,
					ReceiptId = seed.ReceiptId,
					Description = "in flight " + Guid.NewGuid(),
					Category = "",
					UnitPriceCurrency = Currency.USD,
					TotalAmountCurrency = Currency.USD
				});
				await db.SaveChangesAsync();
			}
			await using (var db = fixture.CreateDbContext())
			await using (var tx = await db.Database.BeginTransactionAsync())
			{
				(await CategoryAssistanceObservationWriter.RecordNoChoiceSaveAsync(db, seed.Actor, Guid.NewGuid(), secondItem, false, false)).Should().BeTrue();
				await tx.CommitAsync();
			}
			var inFlight = (await store.ClaimQueuedAsync(default))!;
			var claim = (await store.TryStartDispatchAsync(inFlight, Ready, DateTimeOffset.UtcNow, default))!;
			await Connection().ReconcileAsync(Binding("jev-rotated-key", "https://new.example.test/"));
			(await Connection().IsCurrentAsync(Binding(), claim.Work.Fence.ConnectionGeneration)).Should().BeFalse();
			await store.CompleteAsync(claim, new(CategoryAssistanceAttemptOutcome.Suggested,
				new("some-category", ["some-category"], null, 0.9, 0.9, 0.9,
					new("jev", "jev-1.14.0", CategorySuggestionPolicy.Version, seed.Taxonomy.ToString("D"), 1, 1))), Ready, default);
			await using var verify = fixture.CreateDbContext();
			var final = await verify.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == inFlight.AttemptId);
			final.State.Should().Be("stale"); final.ResultJson.Should().BeNull();
			(await verify.CategoryAssistanceDailyUsage.SingleAsync(x => x.UtcDay == day)).DispatchAttempts.Should().Be(before + 1);
		}
		finally { await DisableAsync(seed.Actor); await Connection().InvalidateAsync(); }
	}

	private async Task<(string Actor, Guid ReceiptId, Guid ItemId, Guid Taxonomy, Guid CategoryId, CategoryAssistanceSettings Settings)> SeedAsync(string description)
	{
		string actor = "assistance-dispatch-" + Guid.NewGuid();
		Guid receiptId = Guid.NewGuid(), itemId = Guid.NewGuid(), categoryId = Guid.NewGuid();
		await using (var db = fixture.CreateDbContext())
		{
			db.Users.Add(new ApplicationUser { Id = actor, UserName = actor });
			var role = await db.Roles.SingleOrDefaultAsync(x => x.Name == AppRoles.Admin);
			if (role == null) { role = new IdentityRole(AppRoles.Admin); db.Roles.Add(role); }
			db.UserRoles.Add(new IdentityUserRole<string> { UserId = actor, RoleId = role.Id });
			if (!await db.NormalizationAiConnection.AnyAsync())
			{
				db.NormalizationAiConnection.Add(new NormalizationAiConnectionEntity { Id = 1, Generation = Guid.NewGuid() });
			}

			await db.SaveChangesAsync();
		}
		Guid before;
		await using (var db = fixture.CreateDbContext())
		{
			before = await db.TaxonomyHeads.Select(x => x.RevisionId).SingleAsync();
		}

		Guid taxonomy = (await new TaxonomyService(new Factory(fixture), TimeProvider.System).ChangeAsync(actor,
			new(before, Guid.NewGuid(), "Dispatch store test", [new TaxonomyEdit("create", categoryId, Name: "Category " + categoryId)]))).Id;
		var settings = await Settings().GetAsync();
		settings = await Settings().UpdateAsync(actor, settings.Revision, settings with
		{ Enabled = true, Provider = "jev", Model = "jev-1.14.0", DailyLimit = 100, PendingLimit = 1 });
		await Connection().ReconcileAsync(Binding());
		await using (var db = fixture.CreateDbContext())
		{
			db.Receipts.Add(new ReceiptEntity { Id = receiptId, Location = "Dispatch store", Date = new DateOnly(2020, 1, 1), TaxAmountCurrency = Currency.USD });
			db.ReceiptItems.Add(new ReceiptItemEntity
			{
				Id = itemId,
				ReceiptId = receiptId,
				Description = description,
				Category = "",
				UnitPriceCurrency = Currency.USD,
				TotalAmountCurrency = Currency.USD
			});
			await db.SaveChangesAsync();
		}
		await using (var db = fixture.CreateDbContext())
		await using (var tx = await db.Database.BeginTransactionAsync())
		{
			(await CategoryAssistanceObservationWriter.RecordNoChoiceSaveAsync(db, actor, Guid.NewGuid(), itemId, false, false)).Should().BeTrue();
			await tx.CommitAsync();
		}
		return (actor, receiptId, itemId, taxonomy, categoryId, settings);
	}

	[Fact]
	public async Task ConcurrentClaimsAndStartsDebitOnceAndDeduplicateExactSource()
	{
		var seed = await SeedAsync("same exact source " + Guid.NewGuid());
		try
		{
			var store = Dispatch();
			await using var baselineDb = fixture.CreateDbContext();
			int before = await baselineDb.CategoryAssistanceDailyUsage.Where(x => x.UtcDay == DateOnly.FromDateTime(DateTime.UtcNow))
				.Select(x => x.DispatchAttempts).SingleOrDefaultAsync();
			var claims = await Task.WhenAll(store.ClaimQueuedAsync(default), store.ClaimQueuedAsync(default));
			var work = claims.Single(x => x != null)!;
			var starts = await Task.WhenAll(store.TryStartDispatchAsync(work, Ready, DateTimeOffset.UtcNow, default),
				store.TryStartDispatchAsync(work, Ready, DateTimeOffset.UtcNow, default));
			var dispatch = starts.Single(x => x != null)!;
			await using var verify = fixture.CreateDbContext();
			(await verify.CategoryAssistanceAttempts.CountAsync(x => x.Id == work.AttemptId)).Should().Be(1);
			(await verify.CategoryAssistanceDailyUsage.SingleAsync(x => x.UtcDay == DateOnly.FromDateTime(DateTime.UtcNow))).DispatchAttempts.Should().Be(before + 1);
			(await store.ClaimQueuedAsync(default)).Should().BeNull();
			await store.CompleteAsync(dispatch, new(CategoryAssistanceAttemptOutcome.Abstained), Ready, default);
			(await verify.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == work.AttemptId)).State.Should().Be("abstained");
		}
		finally { await DisableAsync(seed.Actor); }
	}

	[Fact]
	public async Task ManualChoiceBeforeCompletionDiscardsSuggestionAndKeepsDebit()
	{
		var seed = await SeedAsync("manual race " + Guid.NewGuid());
		try
		{
			var store = Dispatch();
			await using var baselineDb = fixture.CreateDbContext();
			int before = await baselineDb.CategoryAssistanceDailyUsage.Where(x => x.UtcDay == DateOnly.FromDateTime(DateTime.UtcNow))
				.Select(x => x.DispatchAttempts).SingleOrDefaultAsync();
			var work = (await store.ClaimQueuedAsync(default))!;
			var claim = (await store.TryStartDispatchAsync(work, Ready, DateTimeOffset.UtcNow, default))!;
			claim.Should().NotBeNull();
			await using (var db = fixture.CreateDbContext())
			await using (var tx = await db.Database.BeginTransactionAsync())
			{
				await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync();
				var manual = new PurchaseClassificationEntity
				{
					Id = Guid.NewGuid(),
					ReceiptItemId = seed.ItemId,
					Sequence = 1,
					TaxonomyRevisionId = seed.Taxonomy,
					Actor = seed.Actor,
					Source = "manual",
					RecordedAt = DateTimeOffset.UtcNow,
					OperationId = Guid.NewGuid(),
					Reason = "Explicit clear"
				};
				db.Classifications.Add(manual);
				db.CurrentClassifications.Add(new CurrentClassificationEntity { ReceiptItemId = seed.ItemId, AssignmentId = manual.Id });
				await db.SaveChangesAsync(); await tx.CommitAsync();
			}
			await store.CompleteAsync(claim, new(CategoryAssistanceAttemptOutcome.Suggested,
				new("some-category", ["some-category"], null, 0.9, 0.9, 0.9,
					new("jev", "jev-1.14.0", CategorySuggestionPolicy.Version, seed.Taxonomy.ToString("D"), 1, 1))), Ready, default);
			await using var verify = fixture.CreateDbContext();
			var attempt = await verify.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == work.AttemptId);
			attempt.State.Should().Be("stale"); attempt.ResultJson.Should().BeNull();
			(await verify.CategoryAssistanceDailyUsage.SingleAsync(x => x.UtcDay == DateOnly.FromDateTime(DateTime.UtcNow))).DispatchAttempts.Should().Be(before + 1);
		}
		finally { await DisableAsync(seed.Actor); }
	}

	[Fact]
	public async Task FailedStartRollsBackBothDebitAndStateAndExpiredStartIsNeverResent()
	{
		var seed = await SeedAsync("rollback " + Guid.NewGuid());
		try
		{
			var store = Dispatch();
			var work = (await store.ClaimQueuedAsync(default))!;
			await using var baselineDb = fixture.CreateDbContext();
			int before = await baselineDb.CategoryAssistanceDailyUsage.Where(x => x.UtcDay == DateOnly.FromDateTime(DateTime.UtcNow))
				.Select(x => x.DispatchAttempts).SingleOrDefaultAsync();
			var failing = new CategoryAssistanceDispatchStore(new FaultFactory(fixture), TimeProvider.System);
			Func<Task> start = () => failing.TryStartDispatchAsync(work, Ready, DateTimeOffset.UtcNow, default);
			await start.Should().ThrowAsync<InvalidOperationException>();
			await using (var verify = fixture.CreateDbContext())
			{
				(await verify.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == work.AttemptId)).State.Should().Be("queued");
				(await verify.CategoryAssistanceDailyUsage.Where(x => x.UtcDay == DateOnly.FromDateTime(DateTime.UtcNow))
					.Select(x => x.DispatchAttempts).SingleOrDefaultAsync()).Should().Be(before);
			}
			var claim = (await store.TryStartDispatchAsync(work, Ready, DateTimeOffset.UtcNow, default))!;
			await using (var db = fixture.CreateDbContext())
			{
				var row = await db.CategoryAssistanceAttempts.SingleAsync(x => x.Id == work.AttemptId);
				row.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
				await db.SaveChangesAsync();
			}
			(await store.ClaimQueuedAsync(default)).Should().BeNull();
			await using (var verify = fixture.CreateDbContext())
			{
				var row = await verify.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == work.AttemptId);
				row.State.Should().Be("indeterminate"); row.DebitUtcDay.Should().NotBeNull();
				(await verify.CategoryAssistanceDailyUsage.SingleAsync(x => x.UtcDay == DateOnly.FromDateTime(DateTime.UtcNow)))
					.DispatchAttempts.Should().Be(before + 1);
			}
			await store.CompleteAsync(claim, new(CategoryAssistanceAttemptOutcome.Abstained), Ready, default);
			await using var last = fixture.CreateDbContext();
			(await last.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == work.AttemptId)).State.Should().Be("indeterminate");
		}
		finally { await DisableAsync(seed.Actor); }
	}

	[Fact]
	public async Task RepeatedPostConsentSaveAttachesToExactAttemptWithoutAnotherDebit()
	{
		string description = "duplicate " + Guid.NewGuid();
		var seed = await SeedAsync(description);
		try
		{
			var store = Dispatch();
			var work = (await store.ClaimQueuedAsync(default))!;
			var claim = (await store.TryStartDispatchAsync(work, Ready, DateTimeOffset.UtcNow, default))!;
			await store.CompleteAsync(claim, new(CategoryAssistanceAttemptOutcome.Abstained), Ready, default);
			Guid secondReceipt = Guid.NewGuid(), secondItem = Guid.NewGuid();
			await using (var db = fixture.CreateDbContext())
			{
				db.Receipts.Add(new ReceiptEntity { Id = secondReceipt, Location = "Dispatch store", Date = new DateOnly(2020, 1, 1), TaxAmountCurrency = Currency.USD });
				db.ReceiptItems.Add(new ReceiptItemEntity
				{
					Id = secondItem,
					ReceiptId = secondReceipt,
					Description = description,
					Category = "",
					UnitPriceCurrency = Currency.USD,
					TotalAmountCurrency = Currency.USD
				});
				await db.SaveChangesAsync();
			}
			await using (var db = fixture.CreateDbContext())
			await using (var tx = await db.Database.BeginTransactionAsync())
			{
				(await CategoryAssistanceObservationWriter.RecordNoChoiceSaveAsync(db, seed.Actor, Guid.NewGuid(), secondItem, false, false)).Should().BeTrue();
				await tx.CommitAsync();
			}
			(await store.ClaimQueuedAsync(default)).Should().BeNull();
			await using var verify = fixture.CreateDbContext();
			(await verify.CategoryAssistanceObservations.AsNoTracking().SingleAsync(x => x.ReceiptItemId == secondItem)).AttemptId.Should().Be(work.AttemptId);
			(await verify.CategoryAssistanceAttempts.CountAsync(x => x.SourceHash == work.Source.Hash)).Should().Be(1);
		}
		finally { await DisableAsync(seed.Actor); }
	}

	[Fact]
	public async Task PendingLimitWaitsForTerminalAttemptBeforeAdmittingAnotherSource()
	{
		var seed = await SeedAsync("first pending " + Guid.NewGuid());
		try
		{
			var store = Dispatch();
			var first = (await store.ClaimQueuedAsync(default))!;
			Guid secondItem = Guid.NewGuid();
			await using (var db = fixture.CreateDbContext())
			{
				db.ReceiptItems.Add(new ReceiptItemEntity
				{
					Id = secondItem,
					ReceiptId = seed.ReceiptId,
					Description = "second pending " + Guid.NewGuid(),
					Category = "",
					UnitPriceCurrency = Currency.USD,
					TotalAmountCurrency = Currency.USD
				});
				await db.SaveChangesAsync();
			}
			await using (var db = fixture.CreateDbContext())
			await using (var tx = await db.Database.BeginTransactionAsync())
			{
				(await CategoryAssistanceObservationWriter.RecordNoChoiceSaveAsync(db, seed.Actor, Guid.NewGuid(), secondItem, false, false)).Should().BeTrue();
				await tx.CommitAsync();
			}
			(await store.ClaimQueuedAsync(default)).Should().BeNull();
			await store.RejectQueuedAsync(first, SuggestionFailure.InvalidInput, default);
			var second = await store.ClaimQueuedAsync(default);
			second.Should().NotBeNull(); second!.ItemId.Should().Be(secondItem);
			await store.RejectQueuedAsync(second, SuggestionFailure.InvalidInput, default);
		}
		finally { await DisableAsync(seed.Actor); }
	}

	[Fact]
	public async Task ConsentRevokedAfterQueueClaimPreventsDebitAndTerminatesDeferredWork()
	{
		var seed = await SeedAsync("revoked before dispatch " + Guid.NewGuid());
		try
		{
			var store = Dispatch();
			var work = (await store.ClaimQueuedAsync(default))!;
			await using var baselineDb = fixture.CreateDbContext();
			var day = DateOnly.FromDateTime(DateTime.UtcNow);
			int before = await baselineDb.CategoryAssistanceDailyUsage.Where(x => x.UtcDay == day)
				.Select(x => x.DispatchAttempts).SingleOrDefaultAsync();

			var current = await Settings().GetAsync();
			await Settings().UpdateAsync(seed.Actor, current.Revision, current with { Enabled = false });

			(await store.TryStartDispatchAsync(work, Ready, DateTimeOffset.UtcNow, default)).Should().BeNull();
			await store.DeferQueuedAsync(work, CategoryAssistanceQueuedDeferral.StartRejected,
				DateTimeOffset.UtcNow, default);

			await using var verify = fixture.CreateDbContext();
			var attempt = await verify.CategoryAssistanceAttempts.AsNoTracking()
				.SingleAsync(x => x.Id == work.AttemptId);
			attempt.State.Should().Be("stale");
			attempt.DebitUtcDay.Should().BeNull();
			(await verify.CategoryAssistanceDailyUsage.Where(x => x.UtcDay == day)
				.Select(x => x.DispatchAttempts).SingleOrDefaultAsync()).Should().Be(before);
		}
		finally { await DisableAsync(seed.Actor); }
	}

	private async Task DisableAsync(string actor)
	{
		var current = await Settings().GetAsync();
		await Settings().UpdateAsync(actor, current.Revision, current with { Enabled = false });
	}
}
