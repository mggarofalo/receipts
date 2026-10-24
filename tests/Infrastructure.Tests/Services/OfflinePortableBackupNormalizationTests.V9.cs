using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Infrastructure.Services;
using Xunit;

namespace Infrastructure.Tests.Services;

public sealed partial class OfflinePortableBackupNormalizationTests
{
	private async Task UpdateOutputChecksumAsync()
	{
		var lineage = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(Output + ".lineage.json"))!;
		lineage["OutputSha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Output)));
		await File.WriteAllTextAsync(Output + ".lineage.json", JsonSerializer.Serialize(lineage));
	}

	[Theory]
	[InlineData("catalog-name")]
	[InlineData("untouched-catalog")]
	[InlineData("untouched-assignment")]
	[InlineData("template")]
	[InlineData("match-score")]
	public async Task VerifyAndReapply_ConsistentChecksumsButTamperedNormalization_RejectsWithoutMutation(string corruption)
	{
		string source = await CreateAsync(8);
		var reviewed = await ManifestAsync(source, false);
		string manifest = await SaveAsync(reviewed);
		await service.ApplyAsync(source, manifest, Output);
		await EditAsync(Output, corruption switch
		{
			"catalog-name" => $"UPDATE normalized_descriptions SET canonical_name='Unapproved product' WHERE id='{Beef}'",
			"untouched-catalog" => $"UPDATE normalized_descriptions SET display_label='Unapproved label' WHERE id='{Turkey}'",
			"untouched-assignment" => $"UPDATE receipt_items SET normalized_description_id='{Beef}' WHERE id='{Id(5)}'",
			"template" => $"UPDATE item_templates SET normalized_description_id='{Turkey}'",
			_ => $"UPDATE receipt_items SET normalized_description_match_score='0.9' WHERE id='{Id(5)}'"
		});
		await UpdateOutputChecksumAsync();
		byte[] original = await File.ReadAllBytesAsync(source), corrupted = await File.ReadAllBytesAsync(Output);
		Func<Task> verify = () => service.VerifyAsync(source, manifest, Output);
		Func<Task> reapply = () => service.ApplyAsync(source, manifest, Output);
		await verify.Should().ThrowAsync<InvalidDataException>().WithMessage("*reviewed manifest*");
		await reapply.Should().ThrowAsync<InvalidDataException>().WithMessage("*reviewed manifest*");
		(await File.ReadAllBytesAsync(source)).Should().Equal(original);
		(await File.ReadAllBytesAsync(Output)).Should().Equal(corrupted);
		Directory.GetFiles(directory, "*.tmp*").Should().BeEmpty();
	}

	[Theory]
	[InlineData("key")]
	[InlineData("empty-decision")]
	[InlineData("actor")]
	[InlineData("hash")]
	[InlineData("missing-target")]
	[InlineData("inactive-target")]
	[InlineData("self")]
	[InlineData("cycle")]
	[InlineData("duplicate-decision")]
	[InlineData("unknown-field")]
	public async Task V9Consolidation_CorruptTypedAuthority_IsRejectedBySharedArchiveValidation(string corruption)
	{
		string source = await CreateAsync();
		string manifest = await SaveAsync(await ManifestAsync(source, true));
		await service.ApplyAsync(source, manifest, Output);
		await EditAsync(Output, corruption switch
		{
			"key" => $"UPDATE normalization_catalog SET id='{Id(999)}' WHERE kind='consolidation'",
			"empty-decision" => $"UPDATE normalization_catalog SET payload=json_set(payload,'$.DecisionId','{Guid.Empty}') WHERE kind='consolidation'",
			"actor" => "UPDATE normalization_catalog SET payload=json_set(payload,'$.ReviewedBy','invented-user') WHERE kind='consolidation'",
			"hash" => "UPDATE normalization_catalog SET payload=json_set(payload,'$.SourceSha256','invalid') WHERE kind='consolidation'",
			"missing-target" => $"UPDATE normalization_catalog SET payload=json_set(payload,'$.SurvivorId','{Id(999)}') WHERE kind='consolidation'",
			"inactive-target" => $"UPDATE normalized_descriptions SET status='PendingReview' WHERE id='{Beef}'",
			"self" => $"UPDATE normalization_catalog SET payload=json_set(payload,'$.SurvivorId','{Fragment}') WHERE kind='consolidation'",
			"cycle" => $"INSERT INTO normalization_catalog SELECT kind,'{Beef}',json_set(payload,'$.RetiredId','{Beef}','$.SurvivorId','{Fragment}','$.DecisionId','{Id(999)}') FROM normalization_catalog WHERE kind='consolidation'",
			"duplicate-decision" => $"INSERT INTO normalization_catalog SELECT kind,'{Id(999)}',json_set(payload,'$.RetiredId','{Id(999)}') FROM normalization_catalog WHERE kind='consolidation'",
			_ => "UPDATE normalization_catalog SET payload=json_set(payload,'$.InventedAuthority',1) WHERE kind='consolidation'"
		});
		await using var db = Open(Output);
		await db.OpenAsync();
		Func<Task> validate = () => PortableCatalogArchive.ValidateArchiveAsync(db, CancellationToken.None);
		if (corruption == "unknown-field") { await validate.Should().ThrowAsync<InvalidOperationException>(); }
		else { await validate.Should().ThrowAsync<InvalidDataException>(); }
	}

}
