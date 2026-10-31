using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Normalization;
using Application.NormalizationAi;
using Common;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.NormalizationAi;
using Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed partial class NormalizationAiWorkflowTests(PostgresFixture server) : IsolatedPostgresTest(server), IDisposable
{
	private readonly IOptions<PiNormalizationRunnerOptions> bindingOptions = Options.Create(new PiNormalizationRunnerOptions
	{ HomeDirectory = Path.Combine(Path.GetTempPath(), "receipts-workflow-binding-" + Guid.NewGuid().ToString("N")) });
	public void Dispose() { if (Directory.Exists(bindingOptions.Value.HomeDirectory)) { Directory.Delete(bindingOptions.Value.HomeDirectory, true); } }
	private const string Owner = "normalization-test-owner";
	private readonly Mock<IChatGptRunner> runner = new(MockBehavior.Strict);
	private readonly Mock<ICommittedChangePublisher> publisher = new();

	[Fact]
	public async Task RetiredPerPurchaseRun_StaysReadableButCannotApplyRetryOrDispatch()
	{
		var ids = await SeedAsync();
		Guid ready = await SeedPerPurchaseRunAsync(ids[0], "ready", "create");
		Guid failed = await SeedPerPurchaseRunAsync(ids[1], "failed", "pending");
		Guid queued = await SeedPerPurchaseRunAsync(ids[1], "queued", "pending");
		var service = Service();

		(await service.ListRunsAsync(Owner, CancellationToken.None)).Select(x => x.Id).Should().Contain([ready, failed, queued]);
		var saved = (await service.GetRunAsync(Owner, ready, CancellationToken.None))!;
		saved.Proposals.Should().ContainSingle().Which.CanonicalName.Should().Be("Reviewed Milk");
		Func<Task> apply = () => service.ApplyAsync(Owner, ready, [saved.Proposals[0].Id], saved.Revision, CancellationToken.None);
		(await apply.Should().ThrowAsync<NormalizationAiException>()).Which.Should().Match<NormalizationAiException>(x => x.StatusCode == 409 && x.Code == "workflow_retired");
		Func<Task> retry = () => service.RetryAsync(Owner, failed, Guid.NewGuid(), CancellationToken.None);
		(await retry.Should().ThrowAsync<NormalizationAiException>()).Which.Code.Should().Be("workflow_retired");

		(await service.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();
		var refused = (await service.GetRunAsync(Owner, queued, CancellationToken.None))!;
		refused.State.Should().Be("failed");
		refused.ErrorCode.Should().Be("workflow_retired");
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Never);

		(await service.CancelAsync(Owner, ready, CancellationToken.None)).State.Should().Be("cancelled");
		await AssertUnchangedAsync(ids);
		publisher.Verify(x => x.PublishAsync(It.IsAny<CommittedEntityChange>()), Times.Never);
	}

	[Fact]
	public async Task TwoWorkers_DispatchOnceAndCancelledRunRejectsLateResult()
	{
		var ids = await SeedAsync();
		var first = Service();
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.Returns(async (ChatGptInferenceRequest request, CancellationToken _) => { entered.TrySetResult(); await release.Task; return Response(request); });
		var run = await StartSourcesAsync(first);
		var work = first.ProcessNextAsync(CancellationToken.None);
		try
		{
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
			(await Service().ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
			(await first.CancelAsync(Owner, run.Id, CancellationToken.None)).State.Should().Be("cancelled");
		}
		finally { release.TrySetResult(); }
		await work.WaitAsync(TimeSpan.FromSeconds(15));
		var finished = (await first.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		finished.State.Should().Be("cancelled");
		finished.Proposals.Should().OnlyContain(x => x.Action == "pending");
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Once);
		await AssertUnchangedAsync(ids);
	}

	[Fact]
	public async Task ConnectionChange_FencesQueuedWorkAndReleasesItsReservation()
	{
		await SeedAsync();
		var service = Service();
		var run = await StartSourcesAsync(service);
		Func<Task> duplicate = () => StartSourcesAsync(service);
		(await duplicate.Should().ThrowAsync<NormalizationAiException>()).Which.Code.Should().Be("source_reserved");
		await service.BeginConnectionChangeAsync(CancellationToken.None);
		(await service.ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
		(await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!.State.Should().Be("cancelled");
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
		await using var db = fixture.CreateDbContext();
		(await db.NormalizationAiProposals.Where(x => x.RunId == run.Id).ToListAsync()).Should().OnlyContain(x => x.State == "pending",
			"the fenced run no longer reserves its sources because its state is terminal");
	}

	[Fact]
	public async Task Start_IsIdempotentButRejectsDifferentSelectionWithSameRequestId()
	{
		await SeedAsync();
		var service = Service();
		var key = Guid.NewGuid();
		var first = await StartSourcesAsync(service, key);
		(await StartSourcesAsync(service, key)).Id.Should().Be(first.Id);
		Func<Task> changed = () => service.StartSourcesAsync(Owner, key, [new(Milk, false)], "test-model", CancellationToken.None);
		(await changed.Should().ThrowAsync<NormalizationAiException>()).Which.StatusCode.Should().Be(409);
		Func<Task> wrongOwner = () => service.StartSourcesAsync("other-local-user", Guid.NewGuid(), [new(Milk, false)], "test-model", CancellationToken.None);
		(await wrongOwner.Should().ThrowAsync<NormalizationAiException>()).Which.Code.Should().Be("owner_required");
	}

	[Fact]
	public async Task ExpiredDispatch_IsUnknownWithoutAutomaticRetryAndExplicitRetryIsIdempotent()
	{
		var ids = await SeedAsync();
		var service = Service();
		var run = await StartSourcesAsync(service);
		await using (var db = fixture.CreateDbContext())
		{
			var interrupted = await db.NormalizationAiRuns.SingleAsync(x => x.Id == run.Id);
			interrupted.State = "running";
			interrupted.ClaimToken = Guid.NewGuid();
			interrupted.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
			await db.SaveChangesAsync();
		}
		await service.RecoverExpiredAsync(CancellationToken.None);
		var unknown = (await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		unknown.State.Should().Be("unknown");
		unknown.ErrorCode.Should().Be("unknown_outcome");
		(await service.ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
		Guid key = Guid.NewGuid();
		var retried = await service.RetryAsync(Owner, run.Id, key, CancellationToken.None);
		retried.Id.Should().NotBe(run.Id);
		retried.State.Should().Be("queued");
		(await service.RetryAsync(Owner, run.Id, key, CancellationToken.None)).Id.Should().Be(retried.Id);
		(await service.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Once);
		await AssertUnchangedAsync(ids);
	}

	[Fact]
	public async Task ConnectionChange_BlocksAdmissionAndOldCompletionCannotUnfenceNewOperator()
	{
		await SeedAsync();
		var service = Service();
		using var operation = ChatGptOperatorLease.Acquire(bindingOptions.Value);
		Guid first = await service.BeginConnectionChangeAsync(CancellationToken.None);
		Guid second = await service.BeginConnectionChangeAsync(CancellationToken.None);
		await service.CompleteConnectionChangeAsync(first, CancellationToken.None);
		var status = await service.GetConnectionAsync(Owner, CancellationToken.None);
		status.Busy.Should().BeTrue();
		status.CredentialsPresent.Should().BeFalse();
		Func<Task> start = () => StartSourcesAsync(service);
		await start.Should().ThrowAsync<NormalizationAiException>();
		await service.BindConnectionChangeAsync(second, Owner, CancellationToken.None);
		(await service.GetConnectionAsync(Owner, CancellationToken.None)).Busy.Should().BeFalse();
		(await StartSourcesAsync(service)).State.Should().Be("queued");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Worker_DoesNotDispatchAfterOwnerRoleRevocationOrLockout(bool lockout)
	{
		var ids = await SeedAsync();
		var service = Service();
		var run = await StartSourcesAsync(service);
		await using (var db = fixture.CreateDbContext())
		{
			if (lockout) { var owner = await db.Users.SingleAsync(x => x.Id == Owner); owner.LockoutEnabled = true; owner.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1); }
			else
			{
				db.UserRoles.RemoveRange(await db.UserRoles.Where(x => x.UserId == Owner).ToListAsync());
			}

			await db.SaveChangesAsync();
		}
		await service.ProcessNextAsync(CancellationToken.None);
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
		await AssertUnchangedAsync(ids);
		await using var verify = fixture.CreateDbContext();
		(await verify.NormalizationAiRuns.SingleAsync(x => x.Id == run.Id)).State.Should().NotBe("queued").And.NotBe("running");
	}

	[Fact]
	public async Task Retry_DoesNotStealItemsReservedByAnotherRunAfterFailure()
	{
		await SeedAsync();
		var service = Service();
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptInferenceResult("invalid structured output"));
		var failed = await StartSourcesAsync(service);
		await service.ProcessNextAsync(CancellationToken.None);
		(await service.GetRunAsync(Owner, failed.Id, CancellationToken.None))!.State.Should().Be("failed");
		var newer = await StartSourcesAsync(service);
		Func<Task> retry = () => service.RetryAsync(Owner, failed.Id, Guid.NewGuid(), CancellationToken.None);
		(await retry.Should().ThrowAsync<NormalizationAiException>()).Which.Code.Should().Be("source_changed");
		(await service.GetRunAsync(Owner, newer.Id, CancellationToken.None))!.State.Should().Be("queued");
		await using var db = fixture.CreateDbContext();
		(await db.NormalizationAiRuns.CountAsync()).Should().Be(2);
	}

	[Fact]
	public async Task Admission_WaitsForIndependentPostgresNormalizationGateAndCancellationLeavesNoRun()
	{
		await SeedAsync();
		await using var other = new NpgsqlConnection(fixture.ConnectionString);
		await other.OpenAsync();
		await using var transaction = await other.BeginTransactionAsync();
		// RCPT/NORM literal is the existing normalization writer's cross-process contract.
		await using var acquire = new NpgsqlCommand("SELECT pg_advisory_xact_lock(1380143188, 1313821261)", other, transaction);
		await acquire.ExecuteNonQueryAsync();
		using var cancellation = new CancellationTokenSource();
		var service = Service();
		Task<NormalizationAiRun> start = StartSourcesAsync(service, token: cancellation.Token);
		await using var waiting = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype='advisory' AND NOT granted AND pg_backend_pid() = ANY(pg_blocking_pids(pid)))", other, transaction);
		bool observed = false;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		try
		{
			while (!start.IsCompleted && !observed)
			{
				observed = (bool)(await waiting.ExecuteScalarAsync(timeout.Token))!;
				if (!observed)
				{
					await Task.Delay(20, timeout.Token);
				}
			}
			observed.Should().BeTrue("admission must coordinate with independent normalization writers, not only an in-process semaphore");
		}
		finally { cancellation.Cancel(); }
		await FluentActions.Awaiting(async () => await start).Should().ThrowAsync<OperationCanceledException>();
		await using var verify = fixture.CreateDbContext();
		(await verify.NormalizationAiRuns.CountAsync()).Should().Be(0);
		await transaction.RollbackAsync();
		(await StartSourcesAsync(service, token: timeout.Token)).State.Should().Be("queued");
	}

	[Fact]
	public async Task NoOpBackupRestore_FencesReadySuggestionsWithoutRestoringOperationState()
	{
		var ids = await SeedAsync();
		var service = Service();
		var run = await StartSourcesAsync(service);
		await service.ProcessNextAsync(CancellationToken.None);
		(await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!.State.Should().Be("ready");
		var factory = new Factory(fixture);
		var backup = new BackupService(factory, NullLogger<BackupService>.Instance);
		string path = await backup.ExportToSqliteAsync();
		try
		{
			await using var stream = File.OpenRead(path);
			await new BackupImportService(factory, NullLogger<BackupImportService>.Instance).ImportFromSqliteAsync(stream, CancellationToken.None);
			var fenced = (await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
			fenced.State.Should().Be("cancelled");
			fenced.ErrorCode.Should().Be("context_changed");
			await AssertUnchangedAsync(ids);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public async Task AdmissionCap_PreservesIdempotencyAndReleasesCapacityOnCancel()
	{
		await SeedAsync();
		var service = new NormalizationAiService(new Factory(fixture), runner.Object,
			Options.Create(new NormalizationAiOptions { Enabled = true, OwnerUserId = Owner, PendingRunLimit = 1 }), TimeProvider.System, publisher.Object, runnerOptions: bindingOptions);
		Guid intent = Guid.NewGuid();
		var first = await service.StartSourcesAsync(Owner, intent, [new(Milk, false)], "test-model", CancellationToken.None);
		(await service.StartSourcesAsync(Owner, intent, [new(Milk, false)], "test-model", CancellationToken.None)).Id.Should().Be(first.Id);
		Func<Task> second = () => service.StartSourcesAsync(Owner, Guid.NewGuid(), [new(Bread, false)], "test-model", CancellationToken.None);
		(await second.Should().ThrowAsync<NormalizationAiException>()).Which.Code.Should().Be("pending_run_limit");
		await service.CancelAsync(Owner, first.Id, CancellationToken.None);
		await second();
		await using var db = fixture.CreateDbContext();
		(await db.NormalizationAiRuns.CountAsync()).Should().Be(2);
	}

	[Fact]
	public async Task Retention_RemovesUnacceptedPayloadButPreservesIdempotencyAndAppliedEvidence()
	{
		await SeedAsync();
		var service = Service();
		Guid intent = Guid.NewGuid();
		var old = await service.StartSourcesAsync(Owner, intent, [new(Milk, false)], "test-model", CancellationToken.None);
		await service.ProcessNextAsync(CancellationToken.None);
		var accepted = await service.StartSourcesAsync(Owner, Guid.NewGuid(), [new(Bread, false)], "test-model", CancellationToken.None);
		await service.ProcessNextAsync(CancellationToken.None);
		await using (var db = fixture.CreateDbContext())
		{
			foreach (var run in await db.NormalizationAiRuns.Include(x => x.Proposals).ToListAsync())
			{
				run.CreatedAt = DateTimeOffset.UtcNow.AddDays(-31);
				if (run.Id == accepted.Id) { MarkApproved(run, run.Proposals.Single().Id); }
			}

			await db.SaveChangesAsync();
		}
		await service.RecoverExpiredAsync(CancellationToken.None);
		var expired = (await service.GetRunAsync(Owner, old.Id, CancellationToken.None))!;
		expired.State.Should().Be("expired"); expired.Proposals.Should().BeEmpty();
		var retained = (await service.GetRunAsync(Owner, accepted.Id, CancellationToken.None))!;
		retained.State.Should().Be("applied"); retained.Proposals.Should().ContainSingle();
		(await service.StartSourcesAsync(Owner, intent, [new(Milk, false)], "test-model", CancellationToken.None)).Id.Should().Be(old.Id);
		(await StartSourcesAsync(service, sources: [new(Milk, false)])).State.Should().Be("queued", "an expired run no longer reserves its source");
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
	}

	[Fact]
	public async Task PollingFailure_CancelsAndObservesInferenceBeforeWorkerReturnsUnknown()
	{
		var ids = await SeedAsync();
		var factory = new FailPollingFactory(fixture);
		bool stopped = false;
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.Returns(async (ChatGptInferenceRequest _, CancellationToken token) =>
			{
				factory.FailNext = true;
				try { await Task.Delay(Timeout.Infinite, token); return new ChatGptInferenceResult("unreachable"); }
				finally { stopped = true; }
			});
		var service = new NormalizationAiService(factory, runner.Object,
			Options.Create(new NormalizationAiOptions { Enabled = true, OwnerUserId = Owner }), TimeProvider.System, publisher.Object, runnerOptions: bindingOptions);
		var run = await StartSourcesAsync(service);
		await service.ProcessNextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
		stopped.Should().BeTrue("worker must terminate and observe the provider task even when database polling throws");
		(await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!.State.Should().Be("unknown");
		(await service.ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Once);
		await AssertUnchangedAsync(ids);
	}

	[Theory]
	[InlineData("owner-revoked")]
	[InlineData("connection-switched")]
	public async Task LateProviderResult_CannotPublishAfterAuthorityChanges(string change)
	{
		var ids = await SeedAsync();
		var service = Service();
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.Returns(async (ChatGptInferenceRequest request, CancellationToken _) => { entered.TrySetResult(); await release.Task; return Response(request); });
		var run = await StartSourcesAsync(service);
		var processing = service.ProcessNextAsync(CancellationToken.None);
		try
		{
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			if (change == "connection-switched")
			{
				await service.BeginConnectionChangeAsync(CancellationToken.None);
			}
			else
			{
				await using var db = fixture.CreateDbContext();
				db.UserRoles.RemoveRange(await db.UserRoles.Where(x => x.UserId == Owner).ToListAsync());
				await db.SaveChangesAsync();
			}
		}
		finally { release.TrySetResult(); }
		await processing.WaitAsync(TimeSpan.FromSeconds(10));
		await using var verify = fixture.CreateDbContext();
		(await verify.NormalizationAiRuns.SingleAsync(x => x.Id == run.Id)).State.Should().Be("cancelled");
		(await verify.NormalizationAiProposals.ToListAsync()).Should().OnlyContain(x => x.Action == "pending");
		await AssertUnchangedAsync(ids);
	}

	private sealed class FailPollingFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public bool FailNext { get; set; }
		public ApplicationDbContext CreateDbContext()
		{
			if (FailNext) { FailNext = false; throw new IOException("Injected polling outage"); }
			return fixture.CreateDbContext();
		}
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
	}

	[Fact]
	public async Task Retention_AppliedSubsetKeepsOnlyAcceptedProvenanceAndRemainsIdempotent()
	{
		await SeedAsync();
		var service = Service();
		var run = await StartSourcesAsync(service);
		await service.ProcessNextAsync(CancellationToken.None);
		var ready = (await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		var accepted = ready.Proposals.Single(x => x.Description == "Milk");
		await using (var db = fixture.CreateDbContext())
		{
			var saved = await db.NormalizationAiRuns.Include(x => x.Proposals).SingleAsync(x => x.Id == run.Id);
			MarkApproved(saved, accepted.Id);
			saved.CreatedAt = DateTimeOffset.UtcNow.AddDays(-31);
			await db.SaveChangesAsync();
		}
		await service.RecoverExpiredAsync(CancellationToken.None);
		var retained = (await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		retained.State.Should().Be("applied");
		retained.Proposals.Should().ContainSingle(x => x.Id == accepted.Id && x.State == "applied");
		await service.RecoverExpiredAsync(CancellationToken.None);
		(await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!.Proposals.Should().ContainSingle(x => x.Id == accepted.Id);
	}

	private NormalizationAiService Service()
	{
		return new(new Factory(fixture), runner.Object,
			Options.Create(new NormalizationAiOptions { Enabled = true, OwnerUserId = Owner }), TimeProvider.System, publisher.Object, runnerOptions: bindingOptions);
	}

	private static readonly NormalizationSourceKey Milk = new("Synthetic store", "Milk", null);
	private static readonly NormalizationSourceKey Bread = new("Synthetic store", "Bread", null);

	private static Task<NormalizationAiRun> StartSourcesAsync(NormalizationAiService service, Guid? key = null,
		IReadOnlyList<NormalizationAiSourceSelection>? sources = null, CancellationToken token = default)
		=> service.StartSourcesAsync(Owner, key ?? Guid.NewGuid(), sources ?? [new(Milk, false), new(Bread, false)], "test-model", token);

	// Catalog approval is covered by the CatalogApproval tests; retention only needs its outcome.
	private static void MarkApproved(Infrastructure.Entities.NormalizationAi.NormalizationAiRunEntity run, Guid approved)
	{
		run.State = "applied"; run.Revision = Guid.NewGuid(); run.CompletedAt = DateTimeOffset.UtcNow;
		foreach (var proposal in run.Proposals) { proposal.State = proposal.Id == approved ? "applied" : "discarded"; }
	}

	// Rows shaped like those the retired per-purchase workflow saved, which deployments may still hold.
	private async Task<Guid> SeedPerPurchaseRunAsync(Guid itemId, string state, string action)
	{
		await using var db = fixture.CreateDbContext();
		var item = await db.ReceiptItems.SingleAsync(x => x.Id == itemId);
		var connection = await db.NormalizationAiConnection.SingleAsync();
		Guid id = Guid.NewGuid();
		db.NormalizationAiRuns.Add(new()
		{
			Id = id,
			ClientRequestId = Guid.NewGuid(),
			OwnerUserId = Owner,
			ConnectionGeneration = connection.Generation,
			RequestHash = "per-purchase",
			Model = "test-model",
			State = state,
			CreatedAt = DateTimeOffset.UtcNow,
			Proposals =
			[
				new()
				{
					Id = Guid.NewGuid(),
					RunId = id,
					ReceiptItemId = item.Id,
					ReceiptId = item.ReceiptId,
					SourceRevision = item.NormalizationRevision,
					Description = item.Description,
					Action = action,
					CanonicalName = action == "create" ? "Reviewed " + item.Description : null,
				},
			],
		});
		await db.SaveChangesAsync();
		return id;
	}

	private async Task<Guid[]> SeedAsync()
	{
		runner.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptRunnerStatus(true));
		runner.Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "test-model" });
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((ChatGptInferenceRequest request, CancellationToken _) => Response(request));
		publisher.Setup(x => x.PublishAsync(It.IsAny<CommittedEntityChange>())).Returns(Task.CompletedTask);
		await using var db = fixture.CreateDbContext();
		var owner = await db.Users.FindAsync(Owner);
		if (owner is null)
		{
			db.Users.Add(new ApplicationUser { Id = Owner, UserName = Owner, NormalizedUserName = Owner.ToUpperInvariant(), SecurityStamp = Guid.NewGuid().ToString(), CreatedAt = DateTimeOffset.UtcNow });
		}
		else { owner.LockoutEnabled = false; owner.LockoutEnd = null; }
		var role = await db.Roles.FirstOrDefaultAsync(x => x.Name == "Admin");
		if (role is null) { role = new IdentityRole("Admin") { NormalizedName = "ADMIN" }; db.Roles.Add(role); }
		if (!await db.UserRoles.AnyAsync(x => x.UserId == Owner && x.RoleId == role.Id))
		{
			db.UserRoles.Add(new IdentityUserRole<string> { UserId = Owner, RoleId = role.Id });
		}

		var receipt = new ReceiptEntity { Id = Guid.NewGuid(), Date = new(2026, 1, 1), Location = "Synthetic store", TaxAmount = .11m, TaxAmountCurrency = Currency.USD };
		db.Receipts.Add(receipt);
		var items = new[] { "Milk", "Bread" }.Select(description => new ReceiptItemEntity
		{
			Id = Guid.NewGuid(),
			ReceiptId = receipt.Id,
			Description = description,
			Quantity = 1.2345m,
			UnitPrice = 2.3456m,
			UnitPriceCurrency = Currency.USD,
			TotalAmount = 2.90m,
			TotalAmountCurrency = Currency.USD,
			Category = "Food",
		}).ToArray();
		var connection = new Infrastructure.Entities.NormalizationAi.NormalizationAiConnectionEntity();
		db.NormalizationAiConnection.Add(connection);
		Directory.CreateDirectory(bindingOptions.Value.HomeDirectory);
		new ChatGptCredentialBinding(bindingOptions.Value).Publish(Owner, connection.Generation, DateTimeOffset.UtcNow);
		db.ReceiptItems.AddRange(items); await db.SaveChangesAsync();
		return items.Select(x => x.Id).ToArray();
	}

	private static ChatGptInferenceResult Response(ChatGptInferenceRequest request)
	{
		using var prompt = JsonDocument.Parse(request.Prompt);
		var results = prompt.RootElement.EnumerateArray().Select(row => new
		{
			id = row.GetProperty("id").GetGuid(),
			identityDecision = "different",
			candidateId = (Guid?)null,
			newName = "Reviewed " + row.GetProperty("description").GetString(),
			reason = "Synthetic explicit product identity",
		});
		return new(JsonSerializer.Serialize(new { results }), new(10, 5, 0, 0));
	}

	private async Task AssertUnchangedAsync(Guid[] ids)
	{
		await using var db = fixture.CreateDbContext();
		var items = await db.ReceiptItems.Where(x => ids.Contains(x.Id)).ToListAsync();
		items.Should().HaveCount(ids.Length).And.OnlyContain(x => x.NormalizedDescriptionId == null);
		items.Select(x => x.Description).Should().BeEquivalentTo("Milk", "Bread");
		(await db.NormalizedDescriptions.CountAsync()).Should().Be(0);
	}

	private async Task<int> AuditCountAsync(Guid[] ids)
	{
		var strings = ids.Select(x => x.ToString()).ToArray();
		await using var db = fixture.CreateDbContext();
		return await db.AuditLogs.CountAsync(x => x.EntityType == "ReceiptItem" && strings.Contains(x.EntityId));
	}

	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
