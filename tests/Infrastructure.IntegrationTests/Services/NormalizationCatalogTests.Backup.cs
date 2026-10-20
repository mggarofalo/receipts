using Application.Normalization;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task PortableBackup_PreservesReviewedMappingsFactsAndEvidenceButCannotReplayOldUndo()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef Franks 8 count");
		var ids = await ItemsAsync(Source, 2);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
		var approved = await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		var before = await FactsAsync(ids);
		var mappingBefore = (await service.ListMappingsAsync(Actor, Source, CancellationToken.None)).Single();
		string path = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		try
		{
			await using (var stream = File.OpenRead(path))
			{
				await new BackupImportService(new Factory(fixture), NullLogger<BackupImportService>.Instance).ImportFromSqliteAsync(stream, CancellationToken.None);
			}
			(await FactsAsync(ids)).Should().BeEquivalentTo(before);
			(await ReadAsync(ids)).Should().OnlyContain(x => x.NormalizationOrigin == "mapped" && x.NormalizedDescriptionId == target.Id && x.NormalizationMappingId == mappingBefore.Id);
			var restored = (await service.GetOperationAsync(Actor, approved.Id, CancellationToken.None))!;
			restored.State.Should().Be("restored");
			restored.Applied.Should().Be(2);
			var mapping = (await service.ListMappingsAsync(Actor, Source, CancellationToken.None)).Single();
			mapping.Source.Should().Be(Source);
			mapping.TargetId.Should().Be(target.Id);
			mapping.State.Should().Be("active");
			int audits = await AuditCountAsync(ids);
			Func<Task> undo = () => service.UndoAsync(Actor, restored.Id, restored.Revision, CancellationToken.None);
			(await undo.Should().ThrowAsync<NormalizationCatalogException>()).Which.Code.Should().Be("operation_state");
			(await service.ListMappingsAsync(Actor, Source, CancellationToken.None)).Single().State.Should().Be("active");
			(await AuditCountAsync(ids)).Should().Be(audits);
			await using var verify = fixture.CreateDbContext();
			(await verify.NormalizationCatalogMembers.CountAsync(x => x.OperationId == approved.Id)).Should().Be(2);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public async Task RevokedMapping_ExplicitCorrectionIncludesFutureAssignedItemsButProtectsLaterManualCuration()
	{
		await ResetAsync();
		var oldTarget = await CanonicalAsync("Old beef identity");
		var newTarget = await CanonicalAsync("Correct beef identity");
		await ItemsAsync(Source, 1);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(oldTarget), CancellationToken.None);
		await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		var mapping = (await service.ListMappingsAsync(Actor, Source, CancellationToken.None)).Single();
		var future = await ItemsAsync(Source, 2);
		using (var provider = OfflineProvider())
		using (var resolver = new NormalizedDescriptionResolutionService(provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(), provider.GetRequiredService<Application.Interfaces.Services.IDescriptionChangeSignal>(), NullLogger<NormalizedDescriptionResolutionService>.Instance))
		{ await resolver.ProcessPendingResolutionsAsync(CancellationToken.None); }
		await using (var edit = fixture.CreateDbContext())
		{
			var manual = await edit.ReceiptItems.SingleAsync(x => x.Id == future[0]);
			manual.NormalizationOrigin = "manual";
			manual.NormalizationAssignmentId = Guid.NewGuid();
			await edit.SaveChangesAsync();
		}
		await service.RevokeAsync(Actor, mapping.Id, mapping.Revision, CancellationToken.None);
		var correction = await service.PreviewAsync(Actor, Request(newTarget), CancellationToken.None);
		correction.Eligible.Should().Be(2);
		correction.Protected.Should().Be(1);
		await service.ApproveAsync(Actor, correction.Id, correction.Revision, CancellationToken.None);
		var rows = await ReadAsync(future);
		rows.Single(x => x.Id == future[0]).NormalizedDescriptionId.Should().Be(oldTarget.Id);
		rows.Single(x => x.Id == future[1]).NormalizedDescriptionId.Should().Be(newTarget.Id);
	}
}
