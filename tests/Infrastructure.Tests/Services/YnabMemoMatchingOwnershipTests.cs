using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using Common;
using FluentAssertions;
using FluentAssertions.Execution;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Infrastructure.Tests.Services;

public class YnabMemoMatchingOwnershipTests
{
	[Fact]
	public async Task SoleCandidateInAnotherAccount_IsNotPatchedOrBound()
	{
		using Fixture fixture = new();
		Guid receipt = fixture.AddReceipt();
		fixture.RemoteAccount = "other-account";
		var results = await fixture.Service.SyncMemosByReceiptAsync(receipt, CancellationToken.None);
		using AssertionScope assertions = new();
		results.Should().ContainSingle().Which.Outcome.Should().Be(YnabMemoSyncOutcome.NoMatch);
		fixture.Http.Patches.Should().BeEmpty();
		fixture.Records.Should().BeEmpty();
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("Unrelated Coffee Shop")]
	public async Task SoleCandidateWithInsufficientPayeeIdentity_RequiresExplicitResolution(string? payee)
	{
		using Fixture fixture = new();
		Guid receipt = fixture.AddReceipt();
		fixture.RemotePayee = payee;
		var results = await fixture.Service.SyncMemosByReceiptAsync(receipt, CancellationToken.None);
		using AssertionScope assertions = new();
		results.Should().ContainSingle().Which.Outcome.Should().Be(YnabMemoSyncOutcome.Ambiguous);
		results[0].AmbiguousCandidates.Should().ContainSingle(candidate => candidate.Id == "remote-payment");
		fixture.Http.Patches.Should().BeEmpty();
		fixture.Records.Should().BeEmpty();
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task EquallyCompetingLocals_AreAllUnresolved_RegardlessOfBulkOrOrder(bool bulk, bool reverse)
	{
		using Fixture fixture = new();
		Guid first = fixture.AddReceipt();
		Guid second = bulk ? fixture.AddReceipt() : first;
		if (!bulk)
		{
			fixture.AddPayment(first);
		}

		List<Guid> receipts = reverse ? [second, first] : [first, second];
		if (reverse)
		{
			fixture.Transactions[first].Reverse();
		}

		var results = bulk
			? await fixture.Service.SyncMemosBulkAsync(receipts, CancellationToken.None)
			: await fixture.Service.SyncMemosByReceiptAsync(first, CancellationToken.None);
		using AssertionScope assertions = new();
		results.Should().HaveCount(2).And.OnlyContain(result => result.Outcome == YnabMemoSyncOutcome.Ambiguous);
		fixture.Http.Patches.Should().BeEmpty("list order must not choose an arbitrary owner of the shared candidate");
		fixture.Records.Should().BeEmpty();
	}

	[Fact]
	public async Task UniqueSameAccountDateAmountAndPayee_PatchesTheActualRemoteTarget()
	{
		using Fixture fixture = new();
		Guid receipt = fixture.AddReceipt();
		var results = await fixture.Service.SyncMemosByReceiptAsync(receipt, CancellationToken.None);
		results.Should().ContainSingle().Which.Outcome.Should().Be(YnabMemoSyncOutcome.Synced);
		fixture.Http.Patches.Should().ContainSingle().Which.Should().Be("/v1/budgets/budget-1/transactions/remote-payment");
		fixture.RemoteMemo.Should().Contain($"/receipts/{receipt}");
		fixture.Records.Values.Should().ContainSingle(record => record.YnabTransactionId == "remote-payment" && record.SyncStatus == YnabSyncStatus.Synced);
	}

	[Theory]
	[InlineData("missing")]
	[InlineData("other-budget")]
	[InlineData("duplicate")]
	[InlineData("blank-target")]
	[InlineData("different-card-parent")]
	[InlineData("missing-card")]
	public async Task UnprovableAccountMapping_FailsWithoutMutation(string defect)
	{
		using Fixture fixture = new();
		Guid receipt = fixture.AddReceipt();
		switch (defect)
		{
			case "missing": fixture.Mappings.Clear(); break;
			case "other-budget": fixture.Mappings[0] = fixture.Mappings[0] with { YnabBudgetId = "budget-2" }; break;
			case "duplicate": fixture.Mappings.Add(fixture.Mappings[0] with { Id = Guid.NewGuid() }); break;
			case "blank-target": fixture.Mappings[0] = fixture.Mappings[0] with { YnabAccountId = " " }; break;
			case "different-card-parent": fixture.Transactions[receipt][0].Card!.AccountId = Guid.NewGuid(); break;
			case "missing-card": fixture.Transactions[receipt][0].Card = null!; break;
		}
		var results = await fixture.Service.SyncMemosByReceiptAsync(receipt, CancellationToken.None);
		results.Should().ContainSingle().Which.Outcome.Should().Be(YnabMemoSyncOutcome.Failed);
		fixture.Http.Patches.Should().BeEmpty();
		fixture.Records.Should().BeEmpty();
	}

	[Theory]
	[InlineData(YnabSyncType.MemoUpdate, YnabSyncStatus.Synced, false)]
	[InlineData(YnabSyncType.MemoUpdate, YnabSyncStatus.Pending, true)]
	[InlineData(YnabSyncType.MemoUpdate, YnabSyncStatus.Failed, false)]
	[InlineData(YnabSyncType.TransactionPush, YnabSyncStatus.Synced, true)]
	[InlineData(YnabSyncType.TransactionPush, YnabSyncStatus.Pending, false)]
	[InlineData(YnabSyncType.TransactionPush, YnabSyncStatus.Failed, true)]
	public async Task ExistingOwner_IsSeededBeforeEveryLocalChoice(YnabSyncType type, YnabSyncStatus status, bool reverse)
	{
		using Fixture fixture = new();
		Guid competitorReceipt = fixture.AddReceipt();
		Guid ownerReceipt = fixture.AddReceipt();
		TransactionEntity owner = fixture.Transactions[ownerReceipt][0];
		fixture.Bind(owner, type, status);
		List<Guid> ids = reverse ? [ownerReceipt, competitorReceipt] : [competitorReceipt, ownerReceipt];
		var results = await fixture.Service.SyncMemosBulkAsync(ids, CancellationToken.None);
		results.Single(result => result.ReceiptId == competitorReceipt).Outcome.Should().Be(YnabMemoSyncOutcome.Ambiguous);
		results.Single(result => result.ReceiptId == ownerReceipt).Outcome.Should().Be(type == YnabSyncType.MemoUpdate && status == YnabSyncStatus.Synced ? YnabMemoSyncOutcome.AlreadySynced : YnabMemoSyncOutcome.Synced);
		fixture.Records.Keys.Should().OnlyContain(key => key.TransactionId == owner.Id);
		fixture.Http.Patches.Should().HaveCount(type == YnabSyncType.MemoUpdate && status == YnabSyncStatus.Synced ? 0 : 1);
	}

	[Theory]
	[InlineData(YnabSyncType.MemoUpdate, "budget-2", "remote-payment")]
	[InlineData(YnabSyncType.TransactionPush, "budget-2", "remote-payment")]
	[InlineData(YnabSyncType.MemoUpdate, "budget-1", "different-payment")]
	[InlineData(YnabSyncType.TransactionPush, "budget-1", "different-payment")]
	public async Task ExistingDifferentBinding_IsNeverAutomaticallyReplaced(YnabSyncType type, string budget, string target)
	{
		using Fixture fixture = new();
		Guid receipt = fixture.AddReceipt();
		fixture.Bind(fixture.Transactions[receipt][0], type, YnabSyncStatus.Pending, budget, target);
		YnabSyncRecordDto original = fixture.Records.Values.Single();
		var results = await fixture.Service.SyncMemosByReceiptAsync(receipt, CancellationToken.None);
		results.Should().ContainSingle().Which.Outcome.Should().Be(YnabMemoSyncOutcome.Failed);
		fixture.Records.Values.Should().ContainSingle().Which.Should().Be(original);
		fixture.Http.Patches.Should().BeEmpty();
	}

	[Fact]
	public async Task BulkCapturesOneBudgetMappingAndDateSnapshot_AndDeduplicatesInputs()
	{
		using Fixture fixture = new();
		Guid first = fixture.AddReceipt();
		Guid second = fixture.AddReceipt();
		// Duplicate repository rows must not create an extra contender or result.
		fixture.Transactions[first].Add(fixture.Transactions[first][0]);
		var results = await fixture.Service.SyncMemosBulkAsync([first, second, first, second], CancellationToken.None);
		results.Should().HaveCount(2).And.OnlyContain(result => result.Outcome == YnabMemoSyncOutcome.Ambiguous);
		fixture.BudgetReads.Should().Be(1);
		fixture.MappingReads.Should().Be(1);
		fixture.Http.Reads.Should().Be(1);
		fixture.Http.Patches.Should().BeEmpty();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ExistingOwner_RetainsReservationAfterMemoShortcutOrPatchFailure(bool failPatch)
	{
		using Fixture fixture = new();
		Guid ownerReceipt = fixture.AddReceipt();
		Guid competitorReceipt = fixture.AddReceipt();
		TransactionEntity owner = fixture.Transactions[ownerReceipt][0];
		fixture.Bind(owner, YnabSyncType.TransactionPush, YnabSyncStatus.Synced);
		fixture.FailPatch = failPatch;
		fixture.RemoteMemo = failPatch ? null : $"Receipt: /receipts/{ownerReceipt}";
		var results = await fixture.Service.SyncMemosBulkAsync([ownerReceipt, competitorReceipt], CancellationToken.None);
		results.Single(result => result.ReceiptId == ownerReceipt).Outcome.Should().Be(failPatch ? YnabMemoSyncOutcome.Failed : YnabMemoSyncOutcome.AlreadySynced);
		results.Single(result => result.ReceiptId == competitorReceipt).Outcome.Should().Be(YnabMemoSyncOutcome.Ambiguous);
		fixture.Records.Keys.Should().OnlyContain(key => key.TransactionId == owner.Id);
		fixture.Http.Patches.Should().HaveCount(failPatch ? 1 : 0);
	}

	[Fact]
	public async Task DuplicateLocalRows_DoNotPreventUniqueMatching_AndReconciledIsStillSkipped()
	{
		using Fixture fixture = new();
		Guid receipt = fixture.AddReceipt();
		fixture.Transactions[receipt].Add(fixture.Transactions[receipt][0]);
		var results = await fixture.Service.SyncMemosBulkAsync([receipt, receipt], CancellationToken.None);
		results.Should().ContainSingle().Which.Outcome.Should().Be(YnabMemoSyncOutcome.Synced);
		fixture.Http.Patches.Should().ContainSingle();
		using Fixture reconciled = new();
		Guid other = reconciled.AddReceipt();
		reconciled.ClearedStatus = "reconciled";
		var skipped = await reconciled.Service.SyncMemosByReceiptAsync(other, CancellationToken.None);
		skipped.Should().ContainSingle().Which.Outcome.Should().Be(YnabMemoSyncOutcome.ReconciledSkipped);
		reconciled.Http.Patches.Should().BeEmpty();
	}

	[Fact]
	public async Task AlreadySyncedSameBudget_RemainsIdempotentWithoutCurrentMapping()
	{
		using Fixture fixture = new();
		Guid receipt = fixture.AddReceipt();
		fixture.Bind(fixture.Transactions[receipt][0], YnabSyncType.MemoUpdate, YnabSyncStatus.Synced);
		fixture.Mappings.Clear();
		var results = await fixture.Service.SyncMemosByReceiptAsync(receipt, CancellationToken.None);
		results.Should().ContainSingle().Which.Outcome.Should().Be(YnabMemoSyncOutcome.AlreadySynced);
		fixture.Http.Patches.Should().BeEmpty();
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	public async Task BlankLocalPayee_RequiresExplicitResolution(string location)
	{
		using Fixture fixture = new();
		Guid receipt = fixture.AddReceipt();
		fixture.Receipts[receipt].Location = location;
		var results = await fixture.Service.SyncMemosByReceiptAsync(receipt, CancellationToken.None);
		results.Should().ContainSingle().Which.Outcome.Should().Be(YnabMemoSyncOutcome.Ambiguous);
		fixture.Http.Patches.Should().BeEmpty();
	}

	[Fact]
	public async Task DistinctPaymentsOnCardsSharingAnAccount_CanBothSync()
	{
		using Fixture fixture = new();
		Guid receipt = fixture.AddReceipt();
		TransactionEntity second = fixture.AddPayment(receipt);
		second.Amount = 20;
		fixture.ExtraTransactions.Add(new("second-payment", second.Date, -20000, null, "cleared", true, fixture.RemoteAccount, null, fixture.RemotePayee));
		// The lookup is an operation snapshot even if its backing source changes during HTTP I/O.
		fixture.OnRead = fixture.Mappings.Clear;
		var results = await fixture.Service.SyncMemosByReceiptAsync(receipt, CancellationToken.None);
		results.Should().HaveCount(2).And.OnlyContain(result => result.Outcome == YnabMemoSyncOutcome.Synced);
		fixture.Http.Patches.Should().BeEquivalentTo("/v1/budgets/budget-1/transactions/remote-payment", "/v1/budgets/budget-1/transactions/second-payment");
		fixture.Records.Values.Select(record => record.YnabTransactionId).Should().OnlyHaveUniqueItems();
	}

	[Fact]
	public async Task AmbiguousIdentity_IsNotPromotedByRemovingAnOwnedCandidate()
	{
		using Fixture fixture = new();
		Guid owner = fixture.AddReceipt();
		Guid competitor = fixture.AddReceipt();
		fixture.Bind(fixture.Transactions[owner][0], YnabSyncType.MemoUpdate, YnabSyncStatus.Synced);
		fixture.ExtraTransactions.Add(new("equally-plausible", new(2025, 1, 1), -10000, null, "cleared", true, fixture.RemoteAccount, null, fixture.RemotePayee));
		var results = await fixture.Service.SyncMemosBulkAsync([owner, competitor], CancellationToken.None);
		YnabMemoSyncResult unresolved = results.Single(result => result.ReceiptId == competitor);
		unresolved.Outcome.Should().Be(YnabMemoSyncOutcome.Ambiguous);
		unresolved.AmbiguousCandidates.Should().HaveCount(2);
		fixture.Http.Patches.Should().BeEmpty();
		fixture.Records.Values.Should().ContainSingle();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ExplicitResolution_PreservesChoiceButCannotOverwriteForeignBudgetRecord(bool foreignBudget)
	{
		using Fixture fixture = new();
		Guid receipt = fixture.AddReceipt();
		TransactionEntity payment = fixture.Transactions[receipt][0];
		fixture.RemotePayee = "Unrelated payee";
		fixture.RemoteAccount = "explicitly-selected-account";
		if (foreignBudget)
		{
			fixture.Bind(payment, YnabSyncType.MemoUpdate, YnabSyncStatus.Pending, "budget-2");
		}

		var result = await fixture.Service.ResolveMemoSyncAsync(payment.Id, "remote-payment", CancellationToken.None);
		result.Outcome.Should().Be(foreignBudget ? YnabMemoSyncOutcome.Failed : YnabMemoSyncOutcome.Synced);
		fixture.Http.Patches.Should().HaveCount(foreignBudget ? 0 : 1);
		if (foreignBudget)
		{
			fixture.Records.Values.Should().ContainSingle().Which.YnabBudgetId.Should().Be("budget-2");
		}
	}

	private sealed class Fixture : IDisposable
	{
		public Guid AccountId { get; } = Guid.NewGuid();
		public string RemoteAccount { get; set; } = "mapped-account";
		public string? RemotePayee { get; set; } = "Walmart";
		public string? RemoteMemo { get; set; }
		public string ClearedStatus { get; set; } = "cleared";
		public bool FailPatch { get; set; }
		public Action? OnRead { get; set; }
		public List<YnabTransaction> ExtraTransactions { get; } = [];
		public int BudgetReads { get; private set; }
		public int MappingReads { get; private set; }
		public List<YnabAccountMappingDto> Mappings { get; } = [];
		public Dictionary<Guid, ReceiptEntity> Receipts { get; } = [];
		public Dictionary<Guid, List<TransactionEntity>> Transactions { get; } = [];
		public Dictionary<(Guid TransactionId, YnabSyncType Type), YnabSyncRecordDto> Records { get; } = [];
		public Handler Http { get; }
		public YnabMemoSyncService Service { get; }
		private readonly ServiceProvider _provider;
		private readonly HttpClient _httpClient;
		private readonly MemoryCache _cache = new(new MemoryCacheOptions());

		public Fixture()
		{
			Http = new Handler(this);
			_httpClient = new(Http) { BaseAddress = new("https://ynab.test/v1/") };
			IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["YNAB_PAT"] = "synthetic-test-token" }).Build();
			YnabApiClient api = new(_httpClient, _cache, configuration, Mock.Of<IYnabRateLimitTracker>(), new YnabResponseContext(), NullLogger<YnabApiClient>.Instance);
			Mock<IYnabBudgetSelectionService> budget = new();
			budget.Setup(service => service.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => { BudgetReads++; return "budget-1"; });
			Mock<IYnabAccountMappingService> mappings = new();
			Mappings.Add(new(Guid.NewGuid(), AccountId, "mapped-account", "Mapped account", "budget-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
			mappings.Setup(service => service.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => { MappingReads++; return Mappings; });
			Mock<IReceiptRepository> receipts = new();
			receipts.Setup(repository => repository.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid id, CancellationToken _) => Receipts.GetValueOrDefault(id));
			Mock<ITransactionRepository> transactions = new();
			transactions.Setup(repository => repository.GetWithAccountByReceiptIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid id, CancellationToken _) => Transactions.GetValueOrDefault(id) ?? []);
			transactions.Setup(repository => repository.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid id, CancellationToken _) => Transactions.Values.SelectMany(list => list).FirstOrDefault(row => row.Id == id));
			Mock<IYnabSyncRecordService> records = new();
			records.Setup(service => service.GetByTransactionAndTypeAsync(It.IsAny<Guid>(), It.IsAny<YnabSyncType>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid id, YnabSyncType type, CancellationToken _) => Records.GetValueOrDefault((id, type)));
			records.Setup(service => service.CreateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<YnabSyncType>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid id, string selectedBudget, YnabSyncType type, CancellationToken _) => Records[(id, type)] = new(Guid.NewGuid(), id, null, selectedBudget, null, type, YnabSyncStatus.Pending, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
			records.Setup(service => service.UpdateStatusAsync(It.IsAny<Guid>(), It.IsAny<YnabSyncStatus>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).Callback((Guid id, YnabSyncStatus status, string? remoteId, string? error, CancellationToken _) =>
			{
				var entry = Records.Single(entry => entry.Value.Id == id);
				Records[entry.Key] = entry.Value with { SyncStatus = status, YnabTransactionId = remoteId ?? entry.Value.YnabTransactionId, LastError = error };
			}).Returns(Task.CompletedTask);
			ServiceCollection services = new();
			services.AddSingleton<IYnabApiClient>(api).AddSingleton(budget.Object).AddSingleton(mappings.Object).AddSingleton(records.Object).AddSingleton(receipts.Object).AddSingleton(transactions.Object).AddSingleton<ILogger<YnabMemoSyncService>>(NullLogger<YnabMemoSyncService>.Instance).AddTransient<YnabMemoSyncService>();
			_provider = services.BuildServiceProvider();
			Service = _provider.GetRequiredService<YnabMemoSyncService>();
		}

		public Guid AddReceipt()
		{
			Guid id = Guid.NewGuid();
			Receipts[id] = new() { Id = id, Location = "Walmart", Date = new(2025, 1, 1) };
			Transactions[id] = [];
			AddPayment(id);
			return id;
		}

		public TransactionEntity AddPayment(Guid receiptId)
		{
			Guid cardId = Guid.NewGuid();
			TransactionEntity payment = new() { Id = Guid.NewGuid(), ReceiptId = receiptId, CardId = cardId, Amount = 10, AmountCurrency = Currency.USD, Date = new(2025, 1, 1), Card = new() { Id = cardId, AccountId = AccountId, ParentAccount = new() { Id = AccountId, Name = "Local owner", IsActive = true } } };
			Transactions[receiptId].Add(payment);
			return payment;
		}

		public void Bind(TransactionEntity payment, YnabSyncType type, YnabSyncStatus status, string budget = "budget-1", string target = "remote-payment")
		{
			Records[(payment.Id, type)] = new(Guid.NewGuid(), payment.Id, target, budget, RemoteAccount, type, status, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
		}

		public void Dispose() { _provider.Dispose(); _httpClient.Dispose(); _cache.Dispose(); }
	}

	private sealed class Handler(Fixture fixture) : HttpMessageHandler
	{
		public List<string> Patches { get; } = [];
		public int Reads { get; private set; }
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.Method == HttpMethod.Get)
			{
				Reads++;
				fixture.OnRead?.Invoke();
				List<YnabTransaction> remote = [new("remote-payment", new(2025, 1, 1), -10000, fixture.RemoteMemo, fixture.ClearedStatus, true, fixture.RemoteAccount, null, fixture.RemotePayee), .. fixture.ExtraTransactions];
				var rows = remote.Select(row => new { id = row.Id, date = row.Date.ToString("yyyy-MM-dd"), amount = row.Amount, memo = row.Memo, cleared = row.ClearedStatus, approved = true, account_id = row.AccountId, payee_name = row.PayeeName, deleted = false }).ToList();
				return request.RequestUri!.AbsolutePath.EndsWith("/transactions/remote-payment", StringComparison.Ordinal)
					? new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = new { transaction = rows[0] } }) }
					: new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = new { transactions = rows, server_knowledge = 1 } }) };
			}
			if (request.Method == HttpMethod.Patch)
			{
				Patches.Add(request.RequestUri!.AbsolutePath);
				if (fixture.FailPatch)
				{
					return new(HttpStatusCode.BadRequest) { Content = new StringContent("Synthetic rejection") };
				}

				using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
				fixture.RemoteMemo = body.RootElement.GetProperty("transaction").GetProperty("memo").GetString();
				return new(HttpStatusCode.OK);
			}
			throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
		}
	}
}
