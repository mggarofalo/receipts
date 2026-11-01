using System.Text.Json;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Infrastructure.IntegrationTests.CatalogRecall;

/// <summary>
/// RECEIPTS-1089 catalog shortlist recall. A measurement, not a gate: run it with
/// <c>dotnet run scripts/run-prerequisite-tests.cs -- catalog-recall</c>. Only aggregates are written to
/// the test log; set <c>RECEIPTS_RECALL_OUTPUT</c> to a private directory for per-store and per-source detail.
/// </summary>
[Collection(CatalogRecallCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "CatalogRecall")]
public sealed class CatalogRecallTests(CatalogRecallModelFixture model, ITestOutputHelper output)
{
	private const string ConnectionVariable = "RECEIPTS_RECALL_CONNECTION";
	private const string OutputVariable = "RECEIPTS_RECALL_OUTPUT";

	[Fact]
	public async Task RealData_MeasuresApprovedMappingsAndTheLegacyLinkProxy()
	{
		string connection = Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } value ? value
			: throw new InvalidOperationException($"{ConnectionVariable} must name the database to measure.");
		NpgsqlDataSourceBuilder builder = new(connection);
		builder.UseVector();
		await using NpgsqlDataSource dataSource = builder.Build();
		DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseNpgsql(dataSource, npgsql =>
			{
				npgsql.UseVector();
				npgsql.UsePublicMigrationsHistory();
			}).Options;

		CatalogRecallHarness harness = new(() => new ApplicationDbContext(options), new RecordingEmbeddingService(model.Service));
		foreach (CatalogRecallGroundTruth groundTruth in Enum.GetValues<CatalogRecallGroundTruth>())
		{
			(CatalogRecallReport report, _) = await MeasureAsync(harness, groundTruth, "real-data");
			report.Corpus.ActiveTargets.Should().BePositive("the measured database must hold a catalog");
		}
	}

	[Fact]
	public async Task SyntheticFixture_MeasuresApprovedMappingsWithTheirOwnEvidenceHidden()
	{
		PostgresFixture server = new();
		await server.InitializeAsync();
		try
		{
			await SeedSyntheticAsync(server);
			CatalogRecallHarness harness = new(server.CreateDbContext, new RecordingEmbeddingService(model.Service));
			(CatalogRecallReport report, IReadOnlyList<CatalogRecallMeasurement> measurements) =
				await MeasureAsync(harness, CatalogRecallGroundTruth.ApprovedMapping, "synthetic");

			report.Sources.Should().Be(SyntheticFixture.RootElement.GetProperty("mappings").GetArrayLength());
			report.CoveragePercent.Should().Be(100);
			report.Catalog.Strata[CatalogRecallStrata.ExactName].RecallAt1.Should().Be(1, "exact names are solved by the exact channel");
			measurements.Where(item => item.Catalog.Strata.Contains(CatalogRecallStrata.NovelDescription))
				.Should().NotContain(item => item.Catalog.PoolChannels.Contains("approved_example"),
					"leave-one-out hides a source's own mapping");
			measurements.Single(item => item.Case.Source.Description == "GRND BEEF 80/20" && item.Case.Source.Location == "Synthetic Market A")
				.Catalog.Channels.Should().Contain("approved_example", "another store's mapping for the same description stays visible");
		}
		finally
		{
			await server.DisposeAsync();
		}
	}

	private async Task<(CatalogRecallReport Report, IReadOnlyList<CatalogRecallMeasurement> Measurements)> MeasureAsync(CatalogRecallHarness harness, CatalogRecallGroundTruth groundTruth, string corpusName)
	{
		CatalogRecallCorpus corpus = await harness.DescribeCorpusAsync(CancellationToken.None);
		CatalogRecallCaseSet set = await harness.LoadAsync(groundTruth, CancellationToken.None);
		IReadOnlyList<CatalogRecallMeasurement> measurements = await harness.RunAsync(set, CancellationToken.None);
		DateTimeOffset measuredAt = DateTimeOffset.UtcNow;
		CatalogRecallReport aggregate = CatalogRecallReport.Aggregate(set, corpus, measurements, measuredAt);
		output.WriteLine($"## {corpusName}");
		output.WriteLine(aggregate.ToMarkdown());
		if (Environment.GetEnvironmentVariable(OutputVariable) is { Length: > 0 } directory)
		{
			WritePrivate(directory, $"{corpusName}.{aggregate.GroundTruth}", aggregate,
				CatalogRecallReport.Private(set, corpus, measurements, measuredAt), measurements);
		}
		return (aggregate, measurements);
	}

	/// <summary>Per-store numbers and receipt text: owner-only files in a directory the caller names.</summary>
	private static void WritePrivate(string directory, string name, CatalogRecallReport aggregate, CatalogRecallReport detail,
		IReadOnlyList<CatalogRecallMeasurement> measurements)
	{
		// Created owner-only; an existing directory keeps the mode its owner chose.
		if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); }
		else { Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
		string stamp = aggregate.MeasuredAt.ToString("yyyyMMdd'T'HHmmss'Z'");
		void Write(string suffix, string content)
		{
			FileStreamOptions create = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write };
			if (!OperatingSystem.IsWindows()) { create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
			using StreamWriter writer = new(Path.Combine(directory, $"{stamp}.{name}.{suffix}"), create);
			writer.Write(content);
		}
		Write("aggregate.md", aggregate.ToMarkdown());
		Write("aggregate.json", aggregate.ToJson());
		Write("private.md", detail.ToMarkdown());
		Write("private.json", detail.ToJson());
		Write("private.cases.json", JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
	}

	private static readonly JsonDocument SyntheticFixture = JsonDocument.Parse(File.ReadAllText(FindFixture()));

	private async Task SeedSyntheticAsync(PostgresFixture server)
	{
		JsonElement root = SyntheticFixture.RootElement;
		await using ApplicationDbContext db = server.CreateDbContext();
		Dictionary<string, NormalizedDescriptionEntity> targets = [];
		foreach (JsonElement row in root.GetProperty("catalog").EnumerateArray())
		{
			string name = row.GetProperty("name").GetString()!;
			targets[name] = new()
			{
				Id = Guid.NewGuid(),
				CanonicalName = name,
				DisplayLabel = row.TryGetProperty("displayLabel", out JsonElement label) ? label.GetString() : null,
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
				// Production embeds the canonical name, and a display label beside it (EmbeddingGenerationService).
				Embedding = new Vector(await model.Service.GenerateEmbeddingAsync(name, CancellationToken.None)),
				EmbeddingModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
			};
		}
		db.NormalizedDescriptions.AddRange(targets.Values);
		foreach (NormalizedDescriptionEntity target in targets.Values.Where(target => target.DisplayLabel is not null))
		{
			db.ItemEmbeddings.Add(new ItemEmbeddingEntity
			{
				Id = Guid.NewGuid(),
				EntityType = CatalogSemanticNeighbours.LabelEntityType,
				EntityId = target.Id,
				EntityText = target.DisplayLabel!,
				Embedding = new Vector(await model.Service.GenerateEmbeddingAsync(target.DisplayLabel!, CancellationToken.None)),
				ModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
				CreatedAt = DateTimeOffset.UtcNow,
			});
		}
		foreach (JsonElement row in root.GetProperty("mappings").EnumerateArray())
		{
			NormalizationSourceKey source = NormalizationCatalogPolicy.Normalize(new(row.GetProperty("store").GetString()!,
				row.GetProperty("description").GetString()!, row.GetProperty("itemCode").GetString()));
			db.NormalizationSourceMappings.Add(new NormalizationSourceMappingEntity
			{
				Id = Guid.NewGuid(),
				SourceHash = NormalizationCatalogPolicy.Hash(source),
				Location = source.Location,
				Description = source.Description,
				ItemCode = source.ItemCode,
				TargetId = targets[row.GetProperty("target").GetString()!].Id,
				ApprovedBy = "synthetic-fixture",
				ApprovedAt = DateTimeOffset.UtcNow,
			});
		}
		await db.SaveChangesAsync();
	}

	private static string FindFixture()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tests", "Fixtures", "catalog-recall.synthetic.json")))
		{
			directory = directory.Parent;
		}
		return directory is null
			? throw new FileNotFoundException("Synthetic catalog recall fixture not found.")
			: Path.Combine(directory.FullName, "tests", "Fixtures", "catalog-recall.synthetic.json");
	}
}
