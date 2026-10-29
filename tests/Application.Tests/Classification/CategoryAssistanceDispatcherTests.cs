using Application.Classification;
using Application.Normalization;
using Moq;

namespace Application.Tests.Classification;

public class CategoryAssistanceDispatcherTests
{
	[Fact]
	public void QueuedDeferralBackoffIsFiniteAndCapped()
	{
		Assert.Equal(TimeSpan.FromMinutes(2), CategoryAssistanceQueuePolicy.QueuedClaimLease);
		Assert.Equal(TimeSpan.FromSeconds(30), CategoryAssistanceQueuePolicy.Backoff(1));
		Assert.Equal(TimeSpan.FromMinutes(10), CategoryAssistanceQueuePolicy.Backoff(6));
		Assert.Equal(CategoryAssistanceQueuePolicy.Backoff(6), CategoryAssistanceQueuePolicy.Backoff(99));
	}

	private readonly Mock<ICategoryAssistanceDispatchStore> store = new(MockBehavior.Strict);
	private readonly Mock<ICategoryAssistanceCapabilitySource> capabilities = new(MockBehavior.Strict);
	private readonly Mock<ICategoryAssistanceSelectedProvider> provider = new(MockBehavior.Strict);
	private readonly CategoryAssistanceQueuedWork work = Work();

	private CategoryAssistanceDispatcher Dispatcher() => new(store.Object, capabilities.Object,
		provider.Object, TimeProvider.System);

	private static CategoryAssistanceQueuedWork Work()
	{
		NormalizationSourceKey key = NormalizationCatalogPolicy.Normalize(new("shop", "bread", null));
		string hash = NormalizationCatalogPolicy.Hash(key);
		Guid taxonomy = Guid.NewGuid();
		CategoryAssistanceSource source = new(key, hash, Guid.NewGuid());
		CategoryAssistanceFence fence = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), hash,
			source.Revision, taxonomy, null);
		return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
			Guid.NewGuid(), Guid.NewGuid(), source, fence,
			"jev", CategorySuggestionPolicy.Model, CategorySuggestionPolicy.Version,
			new("bread", taxonomy.ToString("D"), [new("food", "Food", "Food products", null)]));
	}

	private CategoryAssistanceCapability Ready() => new("jev", CategoryAssistanceCapabilityState.Ready,
		[CategorySuggestionPolicy.Model]);

	private void Queue(CategoryAssistanceQueuedWork? queued = null)
	{
		store.Setup(x => x.ClaimQueuedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(queued ?? work);
		provider.SetupGet(x => x.Provider).Returns("jev");
		provider.SetupGet(x => x.Model).Returns(CategorySuggestionPolicy.Model);
		provider.Setup(x => x.FitsPayload(It.IsAny<CategorySuggestionRequest>())).Returns(true);
	}

	[Fact]
	public async Task NoQueuedWorkDoesNotAskProviderOrSpend()
	{
		store.Setup(x => x.ClaimQueuedAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync((CategoryAssistanceQueuedWork?)null);
		Assert.False(await Dispatcher().DispatchOneAsync());
		store.Verify(x => x.TryStartDispatchAsync(It.IsAny<CategoryAssistanceQueuedWork>(),
			It.IsAny<CategoryAssistanceCapability>(), It.IsAny<DateTimeOffset>(),
			It.IsAny<CancellationToken>()), Times.Never);
		provider.Verify(x => x.SuggestAsync(It.IsAny<CategorySuggestionRequest>(), It.IsAny<Guid>(),
			It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task InvalidDurableRequestIsRejectedBeforeCapabilityOrProvider()
	{
		CategoryAssistanceQueuedWork invalid = work with
		{ Source = work.Source with { Hash = "hash-collision-or-corruption" } };
		Queue(invalid);
		store.Setup(x => x.RejectQueuedAsync(invalid, SuggestionFailure.InvalidInput,
			It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		Assert.True(await Dispatcher().DispatchOneAsync());
		store.Verify(x => x.RejectQueuedAsync(invalid, SuggestionFailure.InvalidInput,
			It.IsAny<CancellationToken>()), Times.Once);
		provider.Verify(x => x.SuggestAsync(It.IsAny<CategorySuggestionRequest>(), It.IsAny<Guid>(),
			It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task MalformedSourceIsRejectedUnderItsQueuedClaim()
	{
		CategoryAssistanceQueuedWork invalid = work with
		{ Source = work.Source with { Key = new("shop", " ", null) } };
		Queue(invalid);
		store.Setup(x => x.RejectQueuedAsync(invalid, SuggestionFailure.InvalidInput,
			It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		Assert.True(await Dispatcher().DispatchOneAsync());
		store.Verify(x => x.RejectQueuedAsync(invalid, SuggestionFailure.InvalidInput,
			It.IsAny<CancellationToken>()), Times.Once);
		provider.Verify(x => x.SuggestAsync(It.IsAny<CategorySuggestionRequest>(), It.IsAny<Guid>(),
			It.IsAny<CancellationToken>()), Times.Never);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task MissingReceiptOrItemRevisionIsRejected(bool receipt)
	{
		CategoryAssistanceQueuedWork invalid = receipt
			? work with { ReceiptRevision = Guid.Empty }
			: work with { ItemRevision = Guid.Empty };
		Queue(invalid);
		store.Setup(x => x.RejectQueuedAsync(invalid, SuggestionFailure.InvalidInput,
			It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		Assert.True(await Dispatcher().DispatchOneAsync());
		store.Verify(x => x.TryStartDispatchAsync(It.IsAny<CategoryAssistanceQueuedWork>(),
			It.IsAny<CategoryAssistanceCapability>(), It.IsAny<DateTimeOffset>(),
			It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task SelectedTransportModelMustMatchCapturedModelBeforeDebit()
	{
		Queue();
		provider.SetupGet(x => x.Model).Returns("other-model");
		store.Setup(x => x.RejectQueuedAsync(work, SuggestionFailure.InvalidInput,
			It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		Assert.True(await Dispatcher().DispatchOneAsync());
		store.Verify(x => x.TryStartDispatchAsync(It.IsAny<CategoryAssistanceQueuedWork>(),
			It.IsAny<CategoryAssistanceCapability>(), It.IsAny<DateTimeOffset>(),
			It.IsAny<CancellationToken>()), Times.Never);
		provider.Verify(x => x.FitsPayload(It.IsAny<CategorySuggestionRequest>()), Times.Never);
	}

	[Fact]
	public async Task OversizeSerializedPayloadIsRejectedBeforeDebit()
	{
		Queue();
		provider.Setup(x => x.FitsPayload(work.Request)).Returns(false);
		store.Setup(x => x.RejectQueuedAsync(work, SuggestionFailure.PayloadTooLarge,
			It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		Assert.True(await Dispatcher().DispatchOneAsync());
		store.Verify(x => x.TryStartDispatchAsync(It.IsAny<CategoryAssistanceQueuedWork>(),
			It.IsAny<CategoryAssistanceCapability>(), It.IsAny<DateTimeOffset>(),
			It.IsAny<CancellationToken>()), Times.Never);
		provider.Verify(x => x.SuggestAsync(It.IsAny<CategorySuggestionRequest>(), It.IsAny<Guid>(),
			It.IsAny<CancellationToken>()), Times.Never);
	}

	[Theory]
	[InlineData(CategoryAssistanceCapabilityState.Unknown)]
	[InlineData(CategoryAssistanceCapabilityState.Disabled)]
	[InlineData(CategoryAssistanceCapabilityState.Unavailable)]
	public async Task NonReadyCapabilityNeverStartsOrCallsProvider(CategoryAssistanceCapabilityState state)
	{
		Queue();
		capabilities.Setup(x => x.GetAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(Ready() with { State = state });
		store.Setup(x => x.DeferQueuedAsync(work, CategoryAssistanceQueuedDeferral.CapabilityUnavailable,
			It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		Assert.True(await Dispatcher().DispatchOneAsync());
		store.Verify(x => x.TryStartDispatchAsync(It.IsAny<CategoryAssistanceQueuedWork>(),
			It.IsAny<CategoryAssistanceCapability>(), It.IsAny<DateTimeOffset>(),
			It.IsAny<CancellationToken>()), Times.Never);
		provider.Verify(x => x.SuggestAsync(It.IsAny<CategorySuggestionRequest>(), It.IsAny<Guid>(),
			It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task RejectedAtomicStartNeverCallsProvider()
	{
		Queue();
		CategoryAssistanceCapability ready = Ready();
		capabilities.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ready);
		provider.SetupGet(x => x.IsAvailable).Returns(true);
		store.Setup(x => x.TryStartDispatchAsync(work, ready, It.IsAny<DateTimeOffset>(),
			It.IsAny<CancellationToken>())).ReturnsAsync((CategoryAssistanceDispatchClaim?)null);
		store.Setup(x => x.DeferQueuedAsync(work, CategoryAssistanceQueuedDeferral.StartRejected,
			It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

		Assert.True(await Dispatcher().DispatchOneAsync());
		provider.Verify(x => x.SuggestAsync(It.IsAny<CategorySuggestionRequest>(), It.IsAny<Guid>(),
			It.IsAny<CancellationToken>()), Times.Never);
		store.Verify(x => x.DeferQueuedAsync(work, CategoryAssistanceQueuedDeferral.StartRejected,
			It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task CancellationAfterDurableStartDoesNotEnterProvider()
	{
		using CancellationTokenSource cancellation = new();
		CategoryAssistanceDispatchClaim claim = ArrangeStartedClaim();
		store.Setup(x => x.TryStartDispatchAsync(work, It.IsAny<CategoryAssistanceCapability>(), It.IsAny<DateTimeOffset>(),
			cancellation.Token)).Returns(() =>
		{
			cancellation.Cancel();
			return Task.FromResult<CategoryAssistanceDispatchClaim?>(claim);
		});

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			Dispatcher().DispatchOneAsync(cancellation.Token));
		provider.Verify(x => x.SuggestAsync(It.IsAny<CategorySuggestionRequest>(), It.IsAny<Guid>(),
			It.IsAny<CancellationToken>()), Times.Never);
		store.Verify(x => x.CompleteAsync(It.IsAny<CategoryAssistanceDispatchClaim>(),
			It.IsAny<CategoryAssistanceCompletion>(), It.IsAny<CategoryAssistanceCapability>(),
			It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task CurrentDurableClaimDispatchesOnceAndPersistsValidatedSuggestion()
	{
		CategoryAssistanceDispatchClaim claim = ArrangeStartedClaim();
		CategorySuggestion suggestion = Suggestion();
		CategorySuggestion verified = suggestion with { SelectedPathSupport = 0.7 };
		store.Setup(x => x.TryStartSecondDispatchAsync(claim, It.IsAny<CategoryAssistanceCapability>(), 10, 5, It.IsAny<CancellationToken>())).ReturnsAsync(true);
		provider.Setup(x => x.VerifySelectedAsync(work.Request, suggestion, work.Fence.ConnectionGeneration, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new CategorySuggestionResult(verified, null));
		provider.Setup(x => x.SuggestAsync(work.Request, work.Fence.ConnectionGeneration, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new CategorySuggestionResult(suggestion, null));
		CategoryAssistanceCompletion? saved = null;
		store.Setup(x => x.CompleteAsync(claim, It.IsAny<CategoryAssistanceCompletion>(),
			It.Is<CategoryAssistanceCapability>(c => c.Provider == "jev"),
			It.IsAny<CancellationToken>()))
			.Callback<CategoryAssistanceDispatchClaim, CategoryAssistanceCompletion,
				CategoryAssistanceCapability, CancellationToken>((_, result, _, _) => saved = result)
			.Returns(Task.CompletedTask);

		Assert.True(await Dispatcher().DispatchOneAsync());
		Assert.Equal(CategoryAssistanceAttemptOutcome.Suggested, saved?.Outcome);
		Assert.Same(verified, saved?.Suggestion);
		provider.Verify(x => x.SuggestAsync(work.Request, work.Fence.ConnectionGeneration, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task ProviderSuggestionOutsideCapturedTaxonomyBecomesFailure()
	{
		CategoryAssistanceDispatchClaim claim = ArrangeStartedClaim();
		provider.Setup(x => x.SuggestAsync(work.Request, work.Fence.ConnectionGeneration, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new CategorySuggestionResult(Suggestion() with { CategoryId = "invented" }, null));
		CategoryAssistanceCompletion? saved = null;
		store.Setup(x => x.CompleteAsync(claim, It.IsAny<CategoryAssistanceCompletion>(),
			It.Is<CategoryAssistanceCapability>(c => c.Provider == "jev"),
			It.IsAny<CancellationToken>()))
			.Callback<CategoryAssistanceDispatchClaim, CategoryAssistanceCompletion,
				CategoryAssistanceCapability, CancellationToken>((_, result, _, _) => saved = result)
			.Returns(Task.CompletedTask);

		await Dispatcher().DispatchOneAsync();
		Assert.Equal(CategoryAssistanceAttemptOutcome.Failed, saved?.Outcome);
		Assert.Equal(SuggestionFailure.InvalidResponse, saved?.Failure);
		Assert.Null(saved?.Suggestion);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task MalformedProviderResultBecomesInvalidResponse(bool nullPath)
	{
		CategoryAssistanceDispatchClaim claim = ArrangeStartedClaim();
		CategorySuggestion suggestion = nullPath
			? Suggestion() with { Path = null! }
			: Suggestion() with
			{
				Provenance = Suggestion().Provenance with { InputTokens = 9007199254740992 }
			};
		provider.Setup(x => x.SuggestAsync(work.Request, work.Fence.ConnectionGeneration,
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new CategorySuggestionResult(suggestion, null));
		CategoryAssistanceCompletion? saved = null;
		store.Setup(x => x.CompleteAsync(claim, It.IsAny<CategoryAssistanceCompletion>(),
			It.IsAny<CategoryAssistanceCapability>(), It.IsAny<CancellationToken>()))
			.Callback<CategoryAssistanceDispatchClaim, CategoryAssistanceCompletion,
				CategoryAssistanceCapability, CancellationToken>((_, result, _, _) => saved = result)
			.Returns(Task.CompletedTask);

		await Dispatcher().DispatchOneAsync();
		Assert.Equal(CategoryAssistanceAttemptOutcome.Failed, saved?.Outcome);
		Assert.Equal(SuggestionFailure.InvalidResponse, saved?.Failure);
		Assert.Null(saved?.Suggestion);
	}

	[Fact]
	public async Task TransportExceptionIsTerminalFailureWithoutSecondProviderCall()
	{
		CategoryAssistanceDispatchClaim claim = ArrangeStartedClaim();
		provider.Setup(x => x.SuggestAsync(work.Request, work.Fence.ConnectionGeneration, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new IOException("transport failed"));
		store.Setup(x => x.CompleteAsync(claim,
			It.Is<CategoryAssistanceCompletion>(x => x.Outcome == CategoryAssistanceAttemptOutcome.Failed
				&& x.Failure == SuggestionFailure.Unavailable),
			It.Is<CategoryAssistanceCapability>(c => c.Provider == "jev"), It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		await Dispatcher().DispatchOneAsync();
		provider.Verify(x => x.SuggestAsync(work.Request, work.Fence.ConnectionGeneration, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DenialOrSecondFailureNeverPersistsBaseline(bool authorized)
	{
		var claim = ArrangeStartedClaim();
		var baseline = Suggestion();
		provider.Setup(x => x.SuggestAsync(work.Request, work.Fence.ConnectionGeneration, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new CategorySuggestionResult(baseline, null));
		store.Setup(x => x.TryStartSecondDispatchAsync(claim, It.IsAny<CategoryAssistanceCapability>(), 10, 5, It.IsAny<CancellationToken>()))
			.ReturnsAsync(authorized);
		if (authorized)
		{
			provider.Setup(x => x.VerifySelectedAsync(work.Request, baseline, work.Fence.ConnectionGeneration, It.IsAny<CancellationToken>()))
				.ReturnsAsync(CategorySuggestionResult.Failed(SuggestionFailure.Unavailable));
		}

		CategoryAssistanceCompletion? saved = null;
		store.Setup(x => x.CompleteAsync(claim, It.IsAny<CategoryAssistanceCompletion>(), It.IsAny<CategoryAssistanceCapability>(), It.IsAny<CancellationToken>()))
			.Callback<CategoryAssistanceDispatchClaim, CategoryAssistanceCompletion, CategoryAssistanceCapability, CancellationToken>((_, c, _, _) => saved = c)
			.Returns(Task.CompletedTask);
		await Dispatcher().DispatchOneAsync();
		Assert.Equal(CategoryAssistanceAttemptOutcome.Failed, saved!.Outcome);
		Assert.Null(saved.Suggestion);
		provider.Verify(x => x.VerifySelectedAsync(work.Request, baseline, work.Fence.ConnectionGeneration, It.IsAny<CancellationToken>()),
			authorized ? Times.Once : Times.Never);
	}

	private CategoryAssistanceDispatchClaim ArrangeStartedClaim()
	{
		Queue();
		CategoryAssistanceCapability ready = Ready();
		capabilities.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ready);
		provider.SetupGet(x => x.IsAvailable).Returns(true);
		CategoryAssistanceDispatchClaim claim = new(work, Guid.NewGuid());
		store.Setup(x => x.TryStartDispatchAsync(work, ready, It.IsAny<DateTimeOffset>(),
			It.IsAny<CancellationToken>())).ReturnsAsync(claim);
		return claim;
	}

	private CategorySuggestion Suggestion() => new("food", ["food"], null, 0.9, 1, 0.8,
		new("jev", CategorySuggestionPolicy.Model, CategorySuggestionPolicy.Version,
			work.Request.TaxonomyRevision, 10, 5));
}

