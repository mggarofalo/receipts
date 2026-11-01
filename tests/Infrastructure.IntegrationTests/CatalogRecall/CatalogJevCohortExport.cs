using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.NormalizationAi;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Infrastructure.IntegrationTests.CatalogRecall;

/// <summary>
/// RECEIPTS-1091: exports the real-data strata of the Jev catalog-matching cohort, read-only, to one private file.
/// Never part of a lane: run it on its own with
/// <c>dotnet test tests/Infrastructure.IntegrationTests --filter Prerequisite=CatalogJevCohort</c>, with
/// <c>RECEIPTS_RECALL_CONNECTION</c> naming the database and <c>RECEIPTS_JEV_COHORT_OUTPUT</c> a new private file.
/// The file holds receipt text and must never be committed; the test log gets counts only.
/// </summary>
[Collection(CatalogRecallCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "CatalogJevCohort")]
public sealed class CatalogJevCohortExport(CatalogRecallModelFixture model, ITestOutputHelper output)
{
	// The preregistered sample (docs/normalization-evaluation/catalog-jev-1091-preregistration.md).
	public const string Seed = "receipts-1091-cohort-v1";
	public const int InShortlist = 150;
	public const int NotInShortlist = 30;
	private const string Proxy = "proxy";
	private const string Missing = "not-in-shortlist";

	[Fact]
	public async Task ExportsTheLegacyLinkStrata()
	{
		string connection = Environment.GetEnvironmentVariable("RECEIPTS_RECALL_CONNECTION") is { Length: > 0 } value ? value
			: throw new InvalidOperationException("RECEIPTS_RECALL_CONNECTION must name the database to read.");
		string path = Environment.GetEnvironmentVariable("RECEIPTS_JEV_COHORT_OUTPUT") is { Length: > 0 } target ? target
			: throw new InvalidOperationException("RECEIPTS_JEV_COHORT_OUTPUT must name a new private file.");
		// The export holds receipt text: refuse any path inside the checkout, so it cannot be committed.
		string? repository = AppContext.BaseDirectory;
		while (repository is not null && !File.Exists(Path.Combine(repository, "Receipts.slnx"))) { repository = Path.GetDirectoryName(repository); }
		if (repository is not null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(repository), StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("RECEIPTS_JEV_COHORT_OUTPUT must be outside the repository.");
		}

		NpgsqlDataSourceBuilder builder = new(connection);
		builder.UseVector();
		await using NpgsqlDataSource dataSource = builder.Build();
		DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseNpgsql(dataSource, npgsql =>
			{
				npgsql.UseVector();
				npgsql.UsePublicMigrationsHistory();
			}).Options;
		RecordingEmbeddingService embedding = new(model.Service);
		CatalogRecallHarness harness = new(() => new ApplicationDbContext(options), embedding);
		CatalogRecallCorpus corpus = await harness.DescribeCorpusAsync(CancellationToken.None);
		CatalogRecallCaseSet set = await harness.LoadAsync(CatalogRecallGroundTruth.LegacyLink, CancellationToken.None);

		// Exact names are solved by the exact channel and say nothing about judgment; sample the rest in a seeded order.
		CatalogRecallCase[] ordered = [.. set.Cases
			.Where(c => !CatalogRecallStrata.IsExactName(c.Source.Description, c.TargetCanonicalName, c.TargetDisplayLabel))
			.OrderBy(c => SampleKey(c), StringComparer.Ordinal)];
		List<CohortCase> cases = [];
		int skippedTargetMissing = 0, skippedEmptyAfterRemoval = 0;
		foreach (CatalogRecallCase item in ordered)
		{
			int proxy = cases.Count(c => c.Stratum == Proxy);
			if (proxy == InShortlist && cases.Count - proxy == NotInShortlist) { break; }

			string stratum = proxy < InShortlist ? Proxy : Missing;
			await using ApplicationDbContext context = new(options);
			var shortlist = await NormalizationCandidateRetrieval.RetrieveAsync(context, item.Source, embedding, CancellationToken.None);
			int rank = shortlist.ToList().FindIndex(c => c.Id == item.TargetId) + 1;
			if (rank == 0) { skippedTargetMissing++; continue; }

			var kept = stratum == Proxy ? shortlist : [.. shortlist.Where(c => c.Id != item.TargetId)];
			if (kept.Count == 0) { skippedEmptyAfterRemoval++; continue; }

			Guid[] ids = [.. kept.Select(c => c.Id)];
			// Every approved example, uncapped: the request builder chooses which are shown (catalog-matching-questions-v1).
			var mappings = await context.NormalizationSourceMappings.AsNoTracking()
				.Where(m => m.State == "active" && !m.Exclude && m.TargetId != null && ids.Contains(m.TargetId.Value))
				.Select(m => new { TargetId = m.TargetId!.Value, m.Description, m.Location, m.ItemCode }).ToListAsync();
			cases.Add(new($"{stratum}-{cases.Count(c => c.Stratum == stratum) + 1:000}", stratum, item.Source.Description,
				string.IsNullOrWhiteSpace(item.Source.Location) ? null : item.Source.Location,
				[.. kept.Select(c => new CatalogMatchCandidate(c.Id, c.DisplayLabel ?? c.CanonicalName,
					[.. mappings.Where(m => m.TargetId == c.Id).Select(m => new NormalizationApprovedExample(m.Description,
						m.Location == item.Source.Location && m.ItemCode == item.Source.ItemCode))]))],
				stratum == Proxy ? item.TargetId : null, rank,
				CatalogRecallStrata.IsReadable(item.Source.Description, item.TargetDisplayName)));
		}

		FileStreamOptions create = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write };
		if (!OperatingSystem.IsWindows()) { create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }

		await using (FileStream file = new(path, create))
		{
			await JsonSerializer.SerializeAsync(file, new
			{
				kind = "catalog-jev-cohort-v1",
				seed = Seed,
				exportedUtc = DateTimeOffset.UtcNow,
				corpus,
				legacyLinkSources = set.Cases.Count,
				notExactNameSources = ordered.Length,
				skippedTargetMissing,
				skippedEmptyAfterRemoval,
				cases
			}, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
		}

		output.WriteLine($"Exported {cases.Count} cases from {ordered.Length} not-exact-name legacy-link sources " +
			$"(skipped {skippedTargetMissing} target missing, {skippedEmptyAfterRemoval} empty after removal). " +
			$"Coverage {corpus.EmbeddedAtFingerprint}/{corpus.ActiveTargets}.");
		Assert.Equal(InShortlist + NotInShortlist, cases.Count);
	}

	/// <summary>
	/// One exported case. A proxy case expects its legacy-linked name; a not-in-shortlist case had that name removed
	/// from its real shortlist, so it expects none. TargetRank is where retrieval put the linked name.
	/// </summary>
	private sealed record CohortCase(string Id, string Stratum, string Description, string? Store,
		IReadOnlyList<CatalogMatchCandidate> Candidates, Guid? ExpectedCandidateId, int TargetRank, bool Readable);

	private static string SampleKey(CatalogRecallCase item) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
		$"{Seed}\0{item.Source.Location}\0{item.Source.Description}\0{item.Source.ItemCode}")));
}
