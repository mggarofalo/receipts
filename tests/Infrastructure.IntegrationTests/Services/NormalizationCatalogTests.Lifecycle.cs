using Application.Interfaces.Services;
using Application.Normalization;
using FluentAssertions;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task ExistingManualSplit_StampsProtectedProvenanceForLaterCatalogCleanup()
	{
		await ResetAsync();
		var old = await CanonicalAsync("Automatic old name");
		var target = await CanonicalAsync("Proposed replacement");
		var ids = await ItemsAsync(Source, 1, "automatic", old.Id);
		using var provider = OfflineProvider();
		var manual = await new NormalizedDescriptionService(new EntryActorFactory(fixture.CreateOptions(), Actor), new UnavailableEmbeddingService(), new NormalizedDescriptionMapper(), new NormalizedDescriptionSettingsMapper())
			.SplitAsync(ids, "Human chosen identity", CancellationToken.None);
		var stored = (await ReadAsync(ids)).Single();
		stored.NormalizationOrigin.Should().Be("reviewed");
		stored.NormalizationReviewState.Should().Be(Domain.NormalizedDescriptions.NormalizationReviewState.Verified);
		stored.NormalizationAssignmentId.Should().NotBeNull();
		stored.NormalizedDescriptionId.Should().Be(manual.Description.Id);
		var preview = await Service().PreviewAsync(Actor, Request(target) with { IncludeLegacyUnknown = true }, CancellationToken.None);
		preview.Eligible.Should().Be(0);
		preview.Protected.Should().Be(1);
	}

	[Fact]
	public async Task ApprovedAlias_ResolvesFutureItemsOfflineOnlyWithinItsScope()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks 8 count");
		await ItemsAsync(Source, 1);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
		await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		var future = await ItemsAsync(Source, 2);
		var otherStore = await ItemsAsync(Source with { Location = "Other store" }, 1);
		var otherCode = await ItemsAsync(Source with { ItemCode = "OTHER" }, 1);
		(await ReadAsync(future)).Should().OnlyContain(x => x.NormalizedDescriptionId == target.Id && x.NormalizationReviewState == Domain.NormalizedDescriptions.NormalizationReviewState.Verified, "approved exact scope resolves in the original save");
		var facts = await FactsAsync(future);
		int audits = await AuditCountAsync(future);

		using var provider = OfflineProvider();
		var resolver = new NormalizedDescriptionResolutionService(
			provider.GetRequiredService<IServiceScopeFactory>(),
			provider.GetRequiredService<IDescriptionChangeSignal>(),
			NullLogger<NormalizedDescriptionResolutionService>.Instance);
		await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);
		await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);

		(await ReadAsync(future)).Should().OnlyContain(x => x.NormalizedDescriptionId == target.Id && x.NormalizationOrigin == "mapped");
		(await ReadAsync(otherStore.Concat(otherCode).ToArray())).Should().OnlyContain(x => x.NormalizedDescriptionId == null);
		(await FactsAsync(future)).Should().BeEquivalentTo(facts);
		(await AuditCountAsync(future)).Should().Be(audits, "approved mapping was applied atomically at save; background reconciliation must not repeat its audit");
	}

	[Fact]
	public async Task BoundedHistory_ResumesWithoutDuplicateAuditsAndCancellationRevokesFutureReuse()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks");
		var ids = await ItemsAsync(Source, 205);
		var service = Service();
		int audits = await AuditCountAsync(ids);
		var preview = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
		var first = await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		first.State.Should().Be("applying");
		first.Applied.Should().Be(100);
		first.Remaining.Should().Be(105);
		(await AuditCountAsync(ids)).Should().Be(audits + 100);

		var second = await service.ResumeAsync(Actor, first.Id, CancellationToken.None);
		second.Applied.Should().Be(200);
		second.Remaining.Should().Be(5);
		(await AuditCountAsync(ids)).Should().Be(audits + 200);
		Guid[] beforeCancelAuditIds;
		await using (var beforeCancel = fixture.CreateDbContext())
		{ beforeCancelAuditIds = await beforeCancel.AuditLogs.Select(x => x.Id).ToArrayAsync(); }
		var cancelled = await service.CancelAsync(Actor, second.Id, second.Revision, CancellationToken.None);
		cancelled.State.Should().Be("cancelled");
		cancelled.Applied.Should().Be(200);
		(await ReadAsync(ids)).Count(x => x.NormalizedDescriptionId == target.Id).Should().Be(200);
		var mappings = await service.ListMappingsAsync(Actor, Source, CancellationToken.None);
		mappings.Should().ContainSingle().Which.State.Should().Be("revoked");
		Func<Task> resume = () => service.ResumeAsync(Actor, second.Id, CancellationToken.None);
		(await resume.Should().ThrowAsync<NormalizationCatalogException>()).Which.StatusCode.Should().Be(409);
		(await AuditCountAsync(ids)).Should().Be(audits + 400, "each of 200 dependent assignments has a separate audited verification revocation");
		await using var verifyCancellation = fixture.CreateDbContext();
		var revocations = await verifyCancellation.AuditLogs.Where(x => !beforeCancelAuditIds.Contains(x.Id) && x.EntityType == "ReceiptItem").ToListAsync();
		revocations.Should().HaveCount(200);
		revocations.Should().OnlyContain(x => x.GetChanges().Any(change => change.FieldName == "NormalizationReviewState" && change.OldValue == "1" && change.NewValue == "0"));
		(await ReadAsync(ids)).Should().OnlyContain(x => x.NormalizationReviewState == Domain.NormalizedDescriptions.NormalizationReviewState.NeedsReview);
	}

	[Fact]
	public async Task Undo_BoundedChunksRestoreOriginalLinksWithoutReplayingAudits()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks");
		var previous = await CanonicalAsync("Old legacy name");
		var ids = await ItemsAsync(Source, 105, "legacy_unknown", previous.Id);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(target) with { IncludeLegacyUnknown = true }, CancellationToken.None);
		await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		var completed = await service.ResumeAsync(Actor, preview.Id, CancellationToken.None);
		completed.State.Should().Be("completed");
		(await ReadAsync(ids)).Should().OnlyContain(x => x.NormalizationReviewState == Domain.NormalizedDescriptions.NormalizationReviewState.Verified,
			"every explicitly approved catalog member must receive reviewed proof, including the final partial chunk");
		int audits = await AuditCountAsync(ids);
		var undo = await service.UndoAsync(Actor, completed.Id, completed.Revision, CancellationToken.None);
		undo.State.Should().Be("undoing");
		undo.Undone.Should().Be(100);
		var undone = await service.ResumeAsync(Actor, undo.Id, CancellationToken.None);
		undone.State.Should().Be("undone");
		undone.Undone.Should().Be(105);
		(await ReadAsync(ids)).Should().OnlyContain(x => x.NormalizedDescriptionId == previous.Id && x.NormalizationOrigin == "legacy_unknown");
		(await AuditCountAsync(ids)).Should().Be(audits + 210, "105 dependent proofs are revoked and 105 original assignments restored exactly once");
		await using (var verify = fixture.CreateDbContext())
		{
			var itemAudits = await verify.AuditLogs.Where(x => x.EntityType == "ReceiptItem" && ids.Select(id => id.ToString()).Contains(x.EntityId)).ToListAsync();
			itemAudits.Count(x => x.GetChanges().Any(change => change.FieldName == "NormalizationReviewState" && change.OldValue == "1" && change.NewValue == "0")).Should().Be(105);
			itemAudits.Count(x => x.GetChanges().Any(change => change.FieldName == "NormalizedDescriptionId" && change.OldValue == target.Id.ToString() && change.NewValue == previous.Id.ToString())).Should().Be(105);
		}
		await service.ResumeAsync(Actor, undone.Id, CancellationToken.None);
		(await AuditCountAsync(ids)).Should().Be(audits + 210, "105 dependent proofs are revoked and 105 original assignments restored exactly once");
	}

	private ServiceProvider OfflineProvider()
	{
		ServiceCollection services = new();
		services.AddSingleton<IEmbeddingService, UnavailableEmbeddingService>();
		services.AddSingleton<NormalizedDescriptionMapper>();
		services.AddSingleton<NormalizedDescriptionSettingsMapper>();
		services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture));
		services.AddScoped<INormalizedDescriptionService, NormalizedDescriptionService>();
		services.AddSingleton<IDescriptionChangeSignal, DescriptionChangeSignal>();
		return services.BuildServiceProvider();
	}

	private sealed class UnavailableEmbeddingService : IEmbeddingService
	{
		public bool IsConfigured => false;
		public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
			=> throw new InvalidOperationException("An approved exact alias must not require an embedding request.");
		public Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken)
			=> throw new InvalidOperationException("An approved exact alias must not require an embedding request.");
	}
}
