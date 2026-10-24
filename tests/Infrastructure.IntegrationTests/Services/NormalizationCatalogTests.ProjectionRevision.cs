using Application.Interfaces.Services;
using Application.Normalization;
using Domain;
using Domain.Core;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task CanonicalProjection_RebuildPreservesBusinessRevisionEvidenceAndOutstandingChoices()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Projection rebuild product");
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(
			ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source),
			target.Id, target.NormalizationRevision, RememberSource: true))], [], CancellationToken.None);
		var id = saved.Items.Single().Id;
		var before = (await ReadAsync([id])).Single();
		var choice = EntryItem(new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(),
			NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision));
		var service = Service();
		var label = await service.PreviewLabelAsync(Actor, target.Id, "Readable projection product", CancellationToken.None);
		await using var snapshot = fixture.CreateDbContext();
		var mapping = await snapshot.NormalizationSourceMappings.AsNoTracking().SingleAsync();
		var work = await snapshot.NormalizationReviewWork.AsNoTracking().ToListAsync();
		int audits = await snapshot.AuditLogs.CountAsync();
		var vectors = new Mock<IEmbeddingService>();
		vectors.SetupGet(x => x.IsConfigured).Returns(true);
		vectors.Setup(x => x.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((List<string> texts, CancellationToken _) => texts.Select(_ =>
			{
				var values = new float[OnnxEmbeddingService.EmbeddingDimension];
				values[0] = 1;
				return values;
			}).ToList());
		var services = new ServiceCollection();
		services.AddSingleton(vectors.Object);
		services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture));
		using var provider = services.BuildServiceProvider();
		using var worker = new EmbeddingGenerationService(provider.GetRequiredService<IServiceScopeFactory>(),
			NullLogger<EmbeddingGenerationService>.Instance, publisher.Object);

		(await worker.ProcessPendingEmbeddingsAsync(CancellationToken.None)).Should().BeGreaterThan(0);

		await using (var verify = fixture.CreateDbContext())
		{
			var canonical = await verify.NormalizedDescriptions.AsNoTracking().SingleAsync(x => x.Id == target.Id);
			canonical.Embedding!.ToArray()[0].Should().Be(1);
			canonical.EmbeddingModelVersion.Should().Be(OnnxEmbeddingService.EmbeddingSpaceFingerprint);
			canonical.NormalizationRevision.Should().Be(target.NormalizationRevision);
			(await verify.ItemEmbeddings.SingleAsync(x => x.EntityType == "ReceiptItem" && x.EntityId == id))
				.ModelVersion.Should().Be(OnnxEmbeddingService.EmbeddingSpaceFingerprint);
			(await ReadAsync([id])).Single().Should().BeEquivalentTo(before,
				options => options.Excluding(item => item.NormalizedDescription));
			(await verify.NormalizationSourceMappings.AsNoTracking().SingleAsync()).Should().BeEquivalentTo(mapping);
			(await verify.NormalizationReviewWork.AsNoTracking().ToListAsync()).Should().BeEquivalentTo(work);
			(await verify.AuditLogs.CountAsync()).Should().Be(audits);
		}
		// Approve before adding another linked item, so the captured impact count still matches.
		await service.ApproveLabelAsync(Actor, target.Id,
			new(label.ProposedDisplayLabel, label.TargetRevision, label.AffectedItemCount), CancellationToken.None);
		// The label is itself a business edit, so use a second rebuild to exercise the receipt choice independently.
		await using (var resetLabel = fixture.CreateDbContext())
		{
			var canonical = await resetLabel.NormalizedDescriptions.SingleAsync(x => x.Id == target.Id);
			choice.NormalizationIntent = choice.NormalizationIntent! with { ExpectedTargetRevision = canonical.NormalizationRevision };
			await resetLabel.NormalizedDescriptions.Where(x => x.Id == target.Id).ExecuteUpdateAsync(update =>
				update.SetProperty(x => x.EmbeddingModelVersion, "obsolete"));
		}
		await worker.ProcessPendingEmbeddingsAsync(CancellationToken.None);
		var accepted = await EntryWriter().CreateAsync(EntryReceipt(), [], [choice], [], CancellationToken.None);
		(await ReadAsync([accepted.Items.Single().Id])).Single().NormalizationReviewState.Should().Be(NormalizationReviewState.Verified);
	}

	[Theory]
	[InlineData("CanonicalName")]
	[InlineData("DisplayLabel")]
	[InlineData("Status")]
	[InlineData("NearestNeighbourSimilarity")]
	[InlineData("CreatedAt")]
	[InlineData("aba")]
	public async Task CanonicalProjection_BusinessEditsStillRejectCapturedChoicesWithoutPartialWrites(string field)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Original business identity");
		var label = await Service().PreviewLabelAsync(Actor, target.Id, "Captured label", CancellationToken.None);
		await using (var edit = fixture.CreateDbContext())
		{
			// Include a projection write in every case to prove the exception cannot hide business changes.
			var canonical = await edit.NormalizedDescriptions.SingleAsync(x => x.Id == target.Id);
			canonical.EmbeddingModelVersion = "different-model";
			switch (field)
			{
				case "CanonicalName": canonical.CanonicalName = "Changed identity"; break;
				case "DisplayLabel": canonical.DisplayLabel = "Later label"; break;
				case "Status": canonical.Status = NormalizedDescriptionStatus.PendingReview; break;
				case "NearestNeighbourSimilarity": canonical.NearestNeighbourSimilarity = .5; break;
				case "CreatedAt": canonical.CreatedAt = canonical.CreatedAt.AddDays(1); break;
				case "aba": canonical.CanonicalName = "Temporary identity"; break;
			}
			await edit.SaveChangesAsync();
			if (field == "aba")
			{
				canonical.CanonicalName = target.CanonicalName;
				await edit.SaveChangesAsync();
			}
			await edit.Entry(canonical).ReloadAsync();
			canonical.NormalizationRevision.Should().NotBe(target.NormalizationRevision);
		}
		await using var before = fixture.CreateDbContext();
		int audits = await before.AuditLogs.CountAsync();
		Func<Task> approve = () => Service().ApproveLabelAsync(Actor, target.Id,
			new(label.ProposedDisplayLabel, label.TargetRevision, label.AffectedItemCount), CancellationToken.None);
		(await approve.Should().ThrowAsync<NormalizationCatalogException>()).Which.StatusCode.Should().Be(409);
		Func<Task> save = () => EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(new(
			ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), NormalizationCatalogPolicy.Hash(Source),
			target.Id, target.NormalizationRevision, RememberSource: true))], [], CancellationToken.None);
		(await save.Should().ThrowAsync<NormalizationCatalogException>()).Which.StatusCode.Should().Be(409);
		await using var verify = fixture.CreateDbContext();
		(await verify.Receipts.CountAsync()).Should().Be(0);
		(await verify.ReceiptItems.CountAsync()).Should().Be(0);
		(await verify.NormalizationSourceMappings.CountAsync()).Should().Be(0);
		(await verify.NormalizationReviewWork.CountAsync()).Should().Be(0);
		(await verify.AuditLogs.CountAsync()).Should().Be(audits);
	}

	[Fact]
	public async Task CanonicalProjection_FinancialItemEditStillRejectsStaleReviewWithoutPartialWrites()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Financial edit control");
		var saved = await EntryWriter().CreateAsync(EntryReceipt(), [], [EntryItem(null)], [], CancellationToken.None);
		var original = (await ReadAsync([saved.Items.Single().Id])).Single();
		await using (var edit = fixture.CreateDbContext())
		{
			var item = await edit.ReceiptItems.SingleAsync();
			item.TotalAmount = 12;
			await edit.SaveChangesAsync();
		}
		var before = (await ReadAsync([original.Id])).Single();
		before.NormalizationRevision.Should().NotBe(original.NormalizationRevision);
		int audits = await AuditCountAsync([original.Id]);
		var stale = new ReceiptItem(original.Id, Source.ItemCode, Source.Description, 2, new Money(3.125m), new Money(6.25m), "Food", null)
		{
			NormalizationIntent = new(ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(),
				NormalizationCatalogPolicy.Hash(Source), target.Id, target.NormalizationRevision,
				ExpectedItemRevision: original.NormalizationRevision, RememberSource: true),
		};
		var service = new ReceiptItemService(new ReceiptItemRepository(new EntryActorFactory(fixture.CreateOptions(), Actor)), new ReceiptItemMapper());
		Func<Task> save = () => service.UpdateAsync([stale], saved.Receipt.Id, CancellationToken.None);
		(await save.Should().ThrowAsync<NormalizationCatalogException>()).Which.StatusCode.Should().Be(409);
		(await ReadAsync([original.Id])).Single().Should().BeEquivalentTo(before);
		(await AuditCountAsync([original.Id])).Should().Be(audits);
		await using var verify = fixture.CreateDbContext();
		(await verify.NormalizationSourceMappings.CountAsync()).Should().Be(0);
	}
}
