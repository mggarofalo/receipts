using System.Data.Common;
using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Models.NormalizedDescriptions;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
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
	private readonly Mock<INormalizedDescriptionService> matching = new(MockBehavior.Strict);
	private readonly Mock<ICommittedChangePublisher> publisher = new();

	[Fact]
	public async Task PreviewAndApply_CreateOnlyAfterApprovalPreserveFactsAndPublishOnce()
	{
		var ids = await SeedAsync();
		var service = Service();
		int audits = await AuditCountAsync(ids);
		var queued = await service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", CancellationToken.None);
		queued.State.Should().Be("queued");
		(await service.ProcessNextAsync(CancellationToken.None)).Should().BeTrue();
		var ready = (await service.GetRunAsync(Owner, queued.Id, CancellationToken.None))!;
		ready.State.Should().Be("ready");
		await AssertUnchangedAsync(ids);
		(await AuditCountAsync(ids)).Should().Be(audits);
		publisher.Verify(x => x.PublishAsync(It.IsAny<CommittedEntityChange>()), Times.Never);

		var applied = await service.ApplyAsync(Owner, ready.Id, ready.Proposals.Select(x => x.Id).ToArray(), ready.Revision, CancellationToken.None);
		applied.State.Should().Be("applied");
		await using var db = fixture.CreateDbContext();
		var items = await db.ReceiptItems.Where(x => ids.Contains(x.Id)).ToListAsync();
		items.Should().OnlyContain(x => x.NormalizedDescriptionId != null && x.NormalizedDescriptionMatchScore == null);
		items.Should().OnlyContain(x => x.Quantity == 1.2345m && x.UnitPrice == 2.3456m && x.TotalAmount == 2.90m && x.UnitPriceCurrency == Currency.USD && x.TotalAmountCurrency == Currency.USD);
		items.Should().OnlyContain(x => x.NormalizationOrigin == "reviewed" && x.NormalizationAssignmentId != null,
			"accepted human review must not become eligible for automatic catalog reassignment");
		(await AuditCountAsync(ids)).Should().Be(audits + ids.Length);
		publisher.Verify(x => x.PublishAsync(It.Is<CommittedEntityChange>(change => change.EntityType == CommittedEntityType.ReceiptItem)), Times.Once);
		publisher.Verify(x => x.PublishAsync(It.Is<CommittedEntityChange>(change => change.EntityType == CommittedEntityType.NormalizedDescription)), Times.Once);
		var repeated = await service.ApplyAsync(Owner, ready.Id, ready.Proposals.Select(x => x.Id).ToArray(), ready.Revision, CancellationToken.None);
		repeated.Id.Should().Be(applied.Id);
		(await AuditCountAsync(ids)).Should().Be(audits + ids.Length);
		publisher.Verify(x => x.PublishAsync(It.Is<CommittedEntityChange>(change => change.EntityType == CommittedEntityType.ReceiptItem)), Times.Once);
		publisher.Verify(x => x.PublishAsync(It.Is<CommittedEntityChange>(change => change.EntityType == CommittedEntityType.NormalizedDescription)), Times.Once);
	}

	[Fact]
	public async Task Apply_RejectsWholeBatchAfterEditAndRevertWithoutCreatingCanonicalRows()
	{
		var ids = await SeedAsync();
		var service = Service();
		var run = await service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", CancellationToken.None);
		await service.ProcessNextAsync(CancellationToken.None);
		var ready = (await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		await using (var db = fixture.CreateDbContext())
		{
			var item = await db.ReceiptItems.SingleAsync(x => x.Id == ids[0]);
			string original = item.Description;
			item.Description = "temporary edit"; await db.SaveChangesAsync();
			item.Description = original; await db.SaveChangesAsync();
		}
		int audits = await AuditCountAsync(ids);
		Func<Task> apply = () => service.ApplyAsync(Owner, ready.Id, ready.Proposals.Select(x => x.Id).ToArray(), ready.Revision, CancellationToken.None);
		(await apply.Should().ThrowAsync<NormalizationAiException>()).Which.StatusCode.Should().Be(409);
		await AssertUnchangedAsync(ids);
		(await AuditCountAsync(ids)).Should().Be(audits);
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
		var run = await first.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", CancellationToken.None);
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
		var ids = await SeedAsync();
		var service = Service();
		var run = await service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", CancellationToken.None);
		(await service.ListCandidatesAsync(Owner, null, 0, 50, CancellationToken.None)).Items.Should().BeEmpty();
		await service.BeginConnectionChangeAsync(CancellationToken.None);
		(await service.ProcessNextAsync(CancellationToken.None)).Should().BeFalse();
		(await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!.State.Should().Be("cancelled");
		(await service.ListCandidatesAsync(Owner, null, 0, 50, CancellationToken.None)).Items.Select(x => x.Id).Should().BeEquivalentTo(ids);
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Start_IsIdempotentButRejectsDifferentSelectionWithSameRequestId()
	{
		var ids = await SeedAsync();
		var service = Service();
		var key = Guid.NewGuid();
		var first = await service.StartAsync(Owner, key, ids, "test-model", CancellationToken.None);
		(await service.StartAsync(Owner, key, ids.Reverse().ToArray(), "test-model", CancellationToken.None)).Id.Should().Be(first.Id);
		Func<Task> changed = () => service.StartAsync(Owner, key, [ids[0]], "test-model", CancellationToken.None);
		(await changed.Should().ThrowAsync<NormalizationAiException>()).Which.StatusCode.Should().Be(409);
		Func<Task> wrongOwner = () => service.StartAsync("other-local-user", Guid.NewGuid(), ids, "test-model", CancellationToken.None);
		(await wrongOwner.Should().ThrowAsync<NormalizationAiException>()).Which.Code.Should().Be("owner_required");
	}

	[Fact]
	public async Task ExpiredDispatch_IsUnknownWithoutAutomaticRetryAndExplicitRetryIsIdempotent()
	{
		var ids = await SeedAsync();
		var service = Service();
		var run = await service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", CancellationToken.None);
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
	public async Task ApplyCommitFailure_RollsBackNewCanonicalItemLinksProvenanceAndAudits()
	{
		var ids = await SeedAsync();
		var service = Service();
		var run = await service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", CancellationToken.None);
		await service.ProcessNextAsync(CancellationToken.None);
		var ready = (await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		int audits = await AuditCountAsync(ids);
		var options = new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(new RejectCommit()).Options;
		var failing = new NormalizationAiService(new OptionsFactory(options), runner.Object, matching.Object,
			Options.Create(new NormalizationAiOptions { Enabled = true, OwnerUserId = Owner }), TimeProvider.System, publisher.Object, runnerOptions: bindingOptions);
		Func<Task> apply = () => failing.ApplyAsync(Owner, ready.Id, ready.Proposals.Select(x => x.Id).ToArray(), ready.Revision, CancellationToken.None);
		await apply.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected apply commit failure");
		await AssertUnchangedAsync(ids);
		(await AuditCountAsync(ids)).Should().Be(audits);
		var unchanged = (await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		unchanged.State.Should().Be("ready");
		unchanged.Revision.Should().Be(ready.Revision);
		unchanged.Proposals.Should().OnlyContain(x => x.State == "pending");
		publisher.Verify(x => x.PublishAsync(It.IsAny<CommittedEntityChange>()), Times.Never);
		(await service.ApplyAsync(Owner, ready.Id, ready.Proposals.Select(x => x.Id).ToArray(), ready.Revision, CancellationToken.None)).State.Should().Be("applied");
	}

	[Theory]
	[InlineData("unchanged")]
	[InlineData("target-rejected")]
	[InlineData("target-renamed")]
	[InlineData("source-tombstone")]
	[InlineData("receipt-deleted")]
	public async Task ReviewedReuse_RechecksLiveCanonicalAndParentState(string change)
	{
		var ids = await SeedAsync();
		Guid targetId = Guid.NewGuid();
		await using (var db = fixture.CreateDbContext())
		{
			db.NormalizedDescriptions.Add(new NormalizedDescriptionEntity { Id = targetId, CanonicalName = "Existing milk", Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow });
			await db.SaveChangesAsync();
		}
		matching.Setup(x => x.TestMatchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new MatchTestResult([new(targetId, "Existing milk", .92, "Active")], MatchTestOutcomes.AutoAccept, targetId));
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((ChatGptInferenceRequest request, CancellationToken _) =>
			{
				using var prompt = JsonDocument.Parse(request.Prompt);
				return new ChatGptInferenceResult(JsonSerializer.Serialize(new
				{
					results = prompt.RootElement.EnumerateArray().Select(row => new
					{ id = row.GetProperty("id").GetGuid(), action = "reuse", targetId, displayLabel = (string?)null, reason = "Same declared product" })
				}));
			});
		var service = Service();
		var run = await service.StartAsync(Owner, Guid.NewGuid(), [ids[0]], "test-model", CancellationToken.None);
		await service.ProcessNextAsync(CancellationToken.None);
		var ready = (await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		ready.State.Should().Be("ready");
		await using (var db = fixture.CreateDbContext())
		{
			if (change == "target-rejected")
			{
				(await db.NormalizedDescriptions.SingleAsync(x => x.Id == targetId)).Status = NormalizedDescriptionStatus.Rejected;
			}

			if (change == "target-renamed")
			{
				(await db.NormalizedDescriptions.SingleAsync(x => x.Id == targetId)).CanonicalName = "Different product";
			}

			if (change == "source-tombstone")
			{
				var source = await db.ReceiptItems.SingleAsync(x => x.Id == ids[0]);
				db.NormalizedDescriptions.Add(new NormalizedDescriptionEntity { Id = Guid.NewGuid(), CanonicalName = source.Description, Status = NormalizedDescriptionStatus.Rejected, CreatedAt = DateTimeOffset.UtcNow });
			}
			if (change == "receipt-deleted")
			{
				(await db.Receipts.SingleAsync()).DeletedAt = DateTimeOffset.UtcNow;
			}

			await db.SaveChangesAsync();
		}
		int audits = await AuditCountAsync(ids);
		Func<Task> apply = () => service.ApplyAsync(Owner, ready.Id, ready.Proposals.Select(x => x.Id).ToArray(), ready.Revision, CancellationToken.None);
		if (change == "unchanged")
		{
			await apply();
		}
		else
		{
			(await apply.Should().ThrowAsync<NormalizationAiException>()).Which.StatusCode.Should().Be(409);
		}

		await using var verify = fixture.CreateDbContext();
		var item = await verify.ReceiptItems.IgnoreQueryFilters().SingleAsync(x => x.Id == ids[0]);
		item.NormalizedDescriptionId.Should().Be(change == "unchanged" ? targetId : null);
		(await AuditCountAsync(ids)).Should().Be(audits + (change == "unchanged" ? 1 : 0));
	}

	[Fact]
	public async Task ApplyingSubset_DiscardsRemainderAndReleasesItsReservation()
	{
		var ids = await SeedAsync();
		var service = Service();
		var run = await service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", CancellationToken.None);
		await service.ProcessNextAsync(CancellationToken.None);
		var ready = (await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		var selected = ready.Proposals.Single(x => x.ReceiptItemId == ids[0]);
		var discarded = ready.Proposals.Single(x => x.ReceiptItemId == ids[1]);
		var applied = await service.ApplyAsync(Owner, run.Id, [selected.Id], ready.Revision, CancellationToken.None);
		applied.State.Should().Be("applied");
		applied.Proposals.Single(x => x.Id == discarded.Id).State.Should().Be("discarded");
		await using var db = fixture.CreateDbContext();
		(await db.ReceiptItems.SingleAsync(x => x.Id == ids[0])).NormalizedDescriptionId.Should().NotBeNull();
		(await db.ReceiptItems.SingleAsync(x => x.Id == ids[1])).NormalizedDescriptionId.Should().BeNull();
		var candidates = await service.ListCandidatesAsync(Owner, null, 0, 25, CancellationToken.None);
		candidates.Items.Select(x => x.Id).Should().Contain(ids[1]).And.NotContain(ids[0]);
		Func<Task> later = () => service.ApplyAsync(Owner, run.Id, [discarded.Id], applied.Revision, CancellationToken.None);
		await later.Should().ThrowAsync<NormalizationAiException>();
		var repeated = await service.ApplyAsync(Owner, run.Id, [selected.Id], ready.Revision, CancellationToken.None);
		repeated.State.Should().Be("applied");
		(await db.NormalizedDescriptions.CountAsync()).Should().Be(1);
	}

	[Fact]
	public async Task ConnectionChange_BlocksAdmissionAndOldCompletionCannotUnfenceNewOperator()
	{
		var ids = await SeedAsync();
		var service = Service();
		using var operation = ChatGptOperatorLease.Acquire(bindingOptions.Value);
		Guid first = await service.BeginConnectionChangeAsync(CancellationToken.None);
		Guid second = await service.BeginConnectionChangeAsync(CancellationToken.None);
		await service.CompleteConnectionChangeAsync(first, CancellationToken.None);
		var status = await service.GetConnectionAsync(Owner, CancellationToken.None);
		status.Busy.Should().BeTrue();
		status.CredentialsPresent.Should().BeFalse();
		Func<Task> start = () => service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", CancellationToken.None);
		await start.Should().ThrowAsync<NormalizationAiException>();
		await service.BindConnectionChangeAsync(second, Owner, CancellationToken.None);
		(await service.GetConnectionAsync(Owner, CancellationToken.None)).Busy.Should().BeFalse();
		(await service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", CancellationToken.None)).State.Should().Be("queued");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Worker_DoesNotDispatchAfterOwnerRoleRevocationOrLockout(bool lockout)
	{
		var ids = await SeedAsync();
		var service = Service();
		var run = await service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", CancellationToken.None);
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
		var ids = await SeedAsync();
		var service = Service();
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptInferenceResult("invalid structured output"));
		var failed = await service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", CancellationToken.None);
		await service.ProcessNextAsync(CancellationToken.None);
		(await service.GetRunAsync(Owner, failed.Id, CancellationToken.None))!.State.Should().Be("failed");
		var newer = await service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", CancellationToken.None);
		Func<Task> retry = () => service.RetryAsync(Owner, failed.Id, Guid.NewGuid(), CancellationToken.None);
		(await retry.Should().ThrowAsync<NormalizationAiException>()).Which.Code.Should().Be("source_changed");
		(await service.GetRunAsync(Owner, newer.Id, CancellationToken.None))!.State.Should().Be("queued");
		await using var db = fixture.CreateDbContext();
		(await db.NormalizationAiRuns.CountAsync()).Should().Be(2);
	}

	[Fact]
	public async Task Admission_WaitsForIndependentPostgresNormalizationGateAndCancellationLeavesNoRun()
	{
		var ids = await SeedAsync();
		await using var other = new NpgsqlConnection(fixture.ConnectionString);
		await other.OpenAsync();
		await using var transaction = await other.BeginTransactionAsync();
		// RCPT/NORM literal is the existing normalization writer's cross-process contract.
		await using var acquire = new NpgsqlCommand("SELECT pg_advisory_xact_lock(1380143188, 1313821261)", other, transaction);
		await acquire.ExecuteNonQueryAsync();
		using var cancellation = new CancellationTokenSource();
		var service = Service();
		Task<NormalizationAiRun> start = service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", cancellation.Token);
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
		(await service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", timeout.Token)).State.Should().Be("queued");
	}

	[Fact]
	public async Task NoOpBackupRestore_FencesReadySuggestionsWithoutRestoringOperationState()
	{
		var ids = await SeedAsync();
		var service = Service();
		var run = await service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", CancellationToken.None);
		await service.ProcessNextAsync(CancellationToken.None);
		var ready = (await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		var factory = new Factory(fixture);
		var backup = new BackupService(factory, NullLogger<BackupService>.Instance);
		string path = await backup.ExportToSqliteAsync();
		try
		{
			await using var stream = File.OpenRead(path);
			await new BackupImportService(factory, NullLogger<BackupImportService>.Instance).ImportFromSqliteAsync(stream, CancellationToken.None);
			(await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!.State.Should().Be("cancelled");
			Func<Task> apply = () => service.ApplyAsync(Owner, run.Id, ready.Proposals.Select(x => x.Id).ToArray(), ready.Revision, CancellationToken.None);
			await apply.Should().ThrowAsync<NormalizationAiException>();
			await AssertUnchangedAsync(ids);
			(await service.ListCandidatesAsync(Owner, null, 0, 25, CancellationToken.None)).Total.Should().Be(2);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public async Task AdmissionCap_PreservesIdempotencyAndReleasesCapacityOnCancel()
	{
		var ids = await SeedAsync();
		var service = new NormalizationAiService(new Factory(fixture), runner.Object, matching.Object,
			Options.Create(new NormalizationAiOptions { Enabled = true, OwnerUserId = Owner, PendingRunLimit = 1 }), TimeProvider.System, publisher.Object, runnerOptions: bindingOptions);
		Guid intent = Guid.NewGuid();
		var first = await service.StartAsync(Owner, intent, [ids[0]], "test-model", CancellationToken.None);
		(await service.StartAsync(Owner, intent, [ids[0]], "test-model", CancellationToken.None)).Id.Should().Be(first.Id);
		Func<Task> second = () => service.StartAsync(Owner, Guid.NewGuid(), [ids[1]], "test-model", CancellationToken.None);
		(await second.Should().ThrowAsync<NormalizationAiException>()).Which.Code.Should().Be("pending_run_limit");
		await service.CancelAsync(Owner, first.Id, CancellationToken.None);
		await second();
		await using var db = fixture.CreateDbContext();
		(await db.NormalizationAiRuns.CountAsync()).Should().Be(2);
	}

	[Fact]
	public async Task Retention_RemovesUnacceptedPayloadButPreservesIdempotencyAndAppliedEvidence()
	{
		var ids = await SeedAsync();
		var service = Service();
		Guid intent = Guid.NewGuid();
		var old = await service.StartAsync(Owner, intent, [ids[0]], "test-model", CancellationToken.None);
		await service.ProcessNextAsync(CancellationToken.None);
		var accepted = await service.StartAsync(Owner, Guid.NewGuid(), [ids[1]], "test-model", CancellationToken.None);
		await service.ProcessNextAsync(CancellationToken.None);
		var ready = (await service.GetRunAsync(Owner, accepted.Id, CancellationToken.None))!;
		await service.ApplyAsync(Owner, ready.Id, ready.Proposals.Select(x => x.Id).ToArray(), ready.Revision, CancellationToken.None);
		await using (var db = fixture.CreateDbContext())
		{
			foreach (var run in await db.NormalizationAiRuns.ToListAsync())
			{
				run.CreatedAt = DateTimeOffset.UtcNow.AddDays(-31);
			}

			await db.SaveChangesAsync();
		}
		await service.RecoverExpiredAsync(CancellationToken.None);
		var expired = (await service.GetRunAsync(Owner, old.Id, CancellationToken.None))!;
		expired.State.Should().Be("expired"); expired.Proposals.Should().BeEmpty();
		var retained = (await service.GetRunAsync(Owner, accepted.Id, CancellationToken.None))!;
		retained.State.Should().Be("applied"); retained.Proposals.Should().ContainSingle();
		(await service.StartAsync(Owner, intent, [ids[0]], "test-model", CancellationToken.None)).Id.Should().Be(old.Id);
		(await service.ListCandidatesAsync(Owner, null, 0, 25, CancellationToken.None)).Items.Select(x => x.Id).Should().Contain(ids[0]);
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
		var service = new NormalizationAiService(factory, runner.Object, matching.Object,
			Options.Create(new NormalizationAiOptions { Enabled = true, OwnerUserId = Owner }), TimeProvider.System, publisher.Object, runnerOptions: bindingOptions);
		var run = await service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", CancellationToken.None);
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
		var run = await service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", CancellationToken.None);
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
		var ids = await SeedAsync();
		var service = Service();
		var run = await service.StartAsync(Owner, Guid.NewGuid(), ids, "test-model", CancellationToken.None);
		await service.ProcessNextAsync(CancellationToken.None);
		var ready = (await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		var accepted = ready.Proposals.Single(x => x.ReceiptItemId == ids[0]);
		await service.ApplyAsync(Owner, run.Id, [accepted.Id], ready.Revision, CancellationToken.None);
		int audits = await AuditCountAsync(ids);
		publisher.Invocations.Clear();
		await using (var db = fixture.CreateDbContext())
		{
			(await db.NormalizationAiRuns.SingleAsync(x => x.Id == run.Id)).CreatedAt = DateTimeOffset.UtcNow.AddDays(-31);
			await db.SaveChangesAsync();
		}
		await service.RecoverExpiredAsync(CancellationToken.None);
		var retained = (await service.GetRunAsync(Owner, run.Id, CancellationToken.None))!;
		retained.State.Should().Be("applied");
		retained.Proposals.Should().ContainSingle(x => x.Id == accepted.Id && x.State == "applied");
		(await service.ApplyAsync(Owner, run.Id, [accepted.Id], ready.Revision, CancellationToken.None)).State.Should().Be("applied");
		(await AuditCountAsync(ids)).Should().Be(audits);
		publisher.Verify(x => x.PublishAsync(It.IsAny<CommittedEntityChange>()), Times.Never);
	}

	private sealed class RejectCommit : DbTransactionInterceptor
	{
		public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
			=> throw new InvalidOperationException("Injected apply commit failure");
	}

	private sealed class OptionsFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
	}

	private NormalizationAiService Service()
	{
		return new(new Factory(fixture), runner.Object, matching.Object,
			Options.Create(new NormalizationAiOptions { Enabled = true, OwnerUserId = Owner }), TimeProvider.System, publisher.Object, runnerOptions: bindingOptions);
	}

	private async Task<Guid[]> SeedAsync()
	{
		runner.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptRunnerStatus(true));
		runner.Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "test-model" });
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((ChatGptInferenceRequest request, CancellationToken _) => Response(request));
		matching.Setup(x => x.TestMatchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new MatchTestResult([], MatchTestOutcomes.EmbeddingUnavailable, null));
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
			action = "create",
			targetId = (Guid?)null,
			displayLabel = "Reviewed " + row.GetProperty("description").GetString(),
			reason = "Synthetic explicit product identity",
		});
		return new(JsonSerializer.Serialize(new { results }), new(10, 5, 0, 0));
	}

	private async Task AssertUnchangedAsync(Guid[] ids)
	{
		await using var db = fixture.CreateDbContext();
		var items = await db.ReceiptItems.Where(x => ids.Contains(x.Id)).ToListAsync();
		items.Should().HaveCount(ids.Length).And.OnlyContain(x => x.NormalizedDescriptionId == null && x.NormalizedDescriptionMatchScore == null);
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
