using System.Data.Common;
using Application.Interfaces.Services;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Pgvector;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ReceiptCuration_SplitRejectsSelectionChangedDuringProviderWorkWithoutCreatingIdentity(bool parentLocationAba)
	{
		await ResetAsync();
		var original = await CanonicalAsync("Original identity");
		var ids = await ItemsAsync(Source, 2, "manual", original.Id);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var vector = new float[OnnxEmbeddingService.EmbeddingDimension];
		vector[0] = 1;
		var embeddings = new Mock<IEmbeddingService>();
		embeddings.SetupGet(x => x.IsConfigured).Returns(true);
		embeddings.Setup(x => x.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.Returns(async (string _, CancellationToken ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return vector; });
		var service = new NormalizedDescriptionService(new EntryActorFactory(fixture.CreateOptions(), Actor), embeddings.Object, new NormalizedDescriptionMapper(), new NormalizedDescriptionSettingsMapper());
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var split = service.SplitAsync(ids, "Reviewed new identity", deadline.Token);
		try
		{
			await entered.Task.WaitAsync(deadline.Token);
			await using var edit = new EntryActorFactory(fixture.CreateOptions(), Actor).CreateDbContext();
			var changing = await edit.ReceiptItems.SingleAsync(x => x.Id == ids[1], deadline.Token);
			if (parentLocationAba)
			{
				var beforeRevision = changing.NormalizationRevision;
				await edit.Receipts.Where(x => x.Id == changing.ReceiptId).ExecuteUpdateAsync(update => update.SetProperty(x => x.Location, "Other store"), deadline.Token);
				await edit.Receipts.Where(x => x.Id == changing.ReceiptId).ExecuteUpdateAsync(update => update.SetProperty(x => x.Location, Source.Location), deadline.Token);
				await edit.Entry(changing).ReloadAsync(deadline.Token);
				changing.NormalizationRevision.Should().NotBe(beforeRevision, "parent source invalidation must rotate the child observation even when the location is restored");
			}
			else
			{
				changing.Description = "Corrected observation";
				await edit.SaveChangesAsync(deadline.Token);
			}
		}
		finally { release.TrySetResult(); }

		Func<Task> finish = async () => await split;
		(await finish.Should().ThrowAsync<Application.Normalization.NormalizationCatalogException>()).Which.StatusCode.Should().Be(409);

		var retained = await ReadAsync(ids);
		retained.Should().OnlyContain(x => x.NormalizedDescriptionId == original.Id && x.NormalizationReviewState == NormalizationReviewState.NeedsReview);
		retained.Single(x => x.Id == ids[1]).Description.Should().Be(parentLocationAba ? Source.Description : "Corrected observation");
		await using var db = fixture.CreateDbContext();
		(await db.NormalizedDescriptions.CountAsync()).Should().Be(1);
	}

	[Fact]
	public async Task ReceiptCuration_MergeRefreshesMembershipAfterInferenceAndTransfersOnlyActualReview()
	{
		await ResetAsync();
		var keep = await CanonicalAsync("Surviving identity");
		var discard = await CanonicalAsync("Discarded identity");
		var other = await CanonicalAsync("Separate identity");
		var vector = new float[OnnxEmbeddingService.EmbeddingDimension];
		vector[0] = 1;
		await using (var setup = fixture.CreateDbContext())
		{
			(await setup.NormalizedDescriptions.SingleAsync(x => x.Id == keep.Id)).Embedding = new Vector(vector);
			await setup.SaveChangesAsync();
		}
		var reviewed = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), Application.Normalization.NormalizationCatalogPolicy.Hash(Source), discard.Id, discard.NormalizationRevision))], [], CancellationToken.None);
		var movedId = (await ItemsAsync(Source, 1, "manual", discard.Id)).Single();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var embeddings = new Mock<IEmbeddingService>();
		embeddings.SetupGet(x => x.IsConfigured).Returns(true);
		embeddings.Setup(x => x.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.Returns(async (string _, CancellationToken ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return vector; });
		var service = new NormalizedDescriptionService(new EntryActorFactory(fixture.CreateOptions(), Actor), embeddings.Object, new NormalizedDescriptionMapper(), new NormalizedDescriptionSettingsMapper());
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var merge = service.MergeAsync(keep.Id, discard.Id, deadline.Token);
		Guid lateId;
		try
		{
			await entered.Task.WaitAsync(deadline.Token);
			await using (var edit = new EntryActorFactory(fixture.CreateOptions(), Actor).CreateDbContext())
			{
				(await edit.ReceiptItems.SingleAsync(x => x.Id == movedId, deadline.Token)).NormalizedDescriptionId = other.Id;
				await edit.SaveChangesAsync(deadline.Token);
			}
			lateId = (await ItemsAsync(Source with { Description = "New observation after scoring" }, 1, "manual", discard.Id)).Single();
		}
		finally { release.TrySetResult(); }

		(await merge).Should().Be(2);

		var rows = await ReadAsync([reviewed.Items.Single().Id, movedId, lateId]);
		var approved = rows.Single(x => x.Id == reviewed.Items.Single().Id);
		approved.NormalizedDescriptionId.Should().Be(keep.Id);
		approved.NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
		approved.ReviewedTargetId.Should().Be(keep.Id);
		rows.Single(x => x.Id == movedId).NormalizedDescriptionId.Should().Be(other.Id);
		var late = rows.Single(x => x.Id == lateId);
		late.NormalizedDescriptionId.Should().Be(keep.Id);
		late.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview);
		late.NormalizedDescriptionMatchScore.Should().BeNull("new membership was never scored against the surviving target");
		await using var db = fixture.CreateDbContext();
		(await db.NormalizedDescriptions.AnyAsync(x => x.Id == discard.Id)).Should().BeFalse();
	}

	[Fact]
	public async Task ReceiptCuration_SplitAssignmentFailureCannotLeaveCreatedIdentityOrChangedEvidence()
	{
		await ResetAsync();
		var original = await CanonicalAsync("Original identity");
		var ids = await ItemsAsync(Source, 2, "manual", original.Id);
		var facts = await FactsAsync(ids);
		var audits = await AuditCountAsync(ids);
		var options = new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(new FailReceiptAssignmentWrite()).Options;
		var service = new NormalizedDescriptionService(new EntryActorFactory(options, Actor), new UnavailableEmbeddingService(), new NormalizedDescriptionMapper(), new NormalizedDescriptionSettingsMapper());

		Func<Task> split = () => service.SplitAsync(ids, "New reviewed split identity", CancellationToken.None);

		await split.Should().ThrowAsync<DbUpdateException>().Where(error => error.InnerException != null && error.InnerException.Message == "Injected receipt assignment failure");
		(await FactsAsync(ids)).Should().BeEquivalentTo(facts);
		(await ReadAsync(ids)).Should().OnlyContain(x => x.NormalizedDescriptionId == original.Id && x.NormalizationReviewState == NormalizationReviewState.NeedsReview);
		(await AuditCountAsync(ids)).Should().Be(audits);
		await using var db = fixture.CreateDbContext();
		(await db.NormalizedDescriptions.CountAsync()).Should().Be(1);
	}

	private sealed class FailReceiptAssignmentWrite : DbCommandInterceptor
	{
		public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
			InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("UPDATE receipts.\"ReceiptItems\"", StringComparison.Ordinal)
				|| command.CommandText.Contains("UPDATE \"receipts\".\"ReceiptItems\"", StringComparison.Ordinal))
			{
				throw new InvalidOperationException("Injected receipt assignment failure");
			}
			return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
		}
	}
}
