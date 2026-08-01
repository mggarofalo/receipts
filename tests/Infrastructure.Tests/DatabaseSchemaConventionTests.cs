using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Infrastructure.Tests;

/// <summary>
/// Repo-wide guards for the failure mode behind RECEIPTS-830: PostgreSQL's default <c>search_path</c> is
/// <c>"$user", public</c>, so a schema whose name matches the deployed database role silently changes
/// <c>current_schema()</c> for every connection — and EF's unqualified
/// <c>CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory"</c> then lands an empty history table in the wrong
/// schema, replaying every migration against a populated database.
///
/// These are unit tests on purpose: the pre-commit hook and CI both run the non-integration suite, so they
/// gate every commit and every PR. A pre-commit hook alone would be local-only and skipped by --no-verify.
/// </summary>
public class DatabaseSchemaConventionTests
{
	/// <summary>
	/// Schemas already known to collide with the deployed role, whose risk is mitigated by pinning the
	/// migrations-history table to <c>public</c>. Renaming <c>receipts</c> would be a large data migration
	/// for no further safety, so it is recorded here rather than fixed.
	/// </summary>
	private static readonly string[] KnownMitigatedCollisions = ["receipts"];

	[Fact]
	public void ModelSchemas_NamedAfterTheDeployedDatabaseRole_AreOnlyTheKnownMitigatedOnes()
	{
		// Arrange
		string[] deployedRoles = DeployedPostgresRoles();
		deployedRoles.Should().NotBeEmpty("docker-compose.yml should declare POSTGRES_USER");

		// Act
		string[] collisions = [.. ModelSchemas()
			.Where(schema => deployedRoles.Contains(schema, StringComparer.OrdinalIgnoreCase))
			.Order(StringComparer.Ordinal)];

		// Assert
		collisions.Should().BeEquivalentTo(
			KnownMitigatedCollisions,
			"""
			a schema named after the deployed PostgreSQL role silently changes current_schema() for every
			connection, because the default search_path is "$user", public.

			If this failed because you ADDED a schema: rename it so it does not match POSTGRES_USER in
			docker-compose.yml. The breakage does not show up on the deploy that adds the schema — it shows
			up on the NEXT container start, as a crash loop (RECEIPTS-830).

			If you have genuinely mitigated a new collision, add it to KnownMitigatedCollisions with a note.
			""");
	}

	[Fact]
	public void EveryUseNpgsqlCallSite_PinsTheMigrationsHistoryTable()
	{
		// Arrange
		string repositoryRoot = RepositoryRoot();
		string[] sourceFiles =
		[
			.. Directory.EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*.cs", SearchOption.AllDirectories),
			.. Directory.EnumerateFiles(Path.Combine(repositoryRoot, "tests"), "*.cs", SearchOption.AllDirectories)
		];
		sourceFiles.Should().NotBeEmpty("the repository scan should find C# sources");

		// Act
		List<string> unpinned = [];
		foreach (string file in sourceFiles.Where(IsScannable))
		{
			string source = File.ReadAllText(file);
			foreach (int callIndex in CallSiteIndexes(source, "UseNpgsql("))
			{
				string arguments = BalancedParenthesesSpan(source, callIndex + "UseNpgsql".Length);
				if (!arguments.Contains("UsePublicMigrationsHistory"))
				{
					int line = source.Take(callIndex).Count(c => c == '\n') + 1;
					unpinned.Add($"{Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/')}:{line}");
				}
			}
		}

		// Assert
		unpinned.Should().BeEmpty(
			"""
			every UseNpgsql call site must call b.UsePublicMigrationsHistory() so the migrations-history
			table is schema-qualified. Left unqualified, EF resolves it through search_path and can create a
			second, empty history table in whichever schema current_schema() happens to be — which then reads
			back as "no migrations applied" and replays every migration against a populated database
			(RECEIPTS-830).
			""");
	}

	#region Helpers

	/// <summary>Every non-null schema mapped by the EF model. Builds the model only — it does not connect.</summary>
	private static IEnumerable<string> ModelSchemas()
	{
		DbContextOptionsBuilder<ApplicationDbContext> builder = new();
		builder.UseNpgsql(
			"Host=model-build-only;Database=model-build-only",
			b =>
			{
				b.UseVector();
				b.UsePublicMigrationsHistory();
			});

		using ApplicationDbContext context = new(builder.Options);
		return context.Model
			.GetEntityTypes()
			.Select(IReadOnlyEntityType (entityType) => entityType)
			.Select(entityType => entityType.GetSchema())
			.Where(schema => !string.IsNullOrEmpty(schema))
			.Select(schema => schema!)
			.Distinct(StringComparer.Ordinal);
	}

	/// <summary>Role names the deployed stack connects as, read from <c>docker-compose.yml</c>.</summary>
	private static string[] DeployedPostgresRoles()
	{
		string composePath = Path.Combine(RepositoryRoot(), "docker-compose.yml");
		string compose = File.ReadAllText(composePath);

		return [.. Regex.Matches(compose, @"POSTGRES_USER=(?<role>\S+)")
			.Select(match => match.Groups["role"].Value)
			.Distinct(StringComparer.OrdinalIgnoreCase)];
	}

	/// <summary>
	/// Excludes build output and this file — the scanner's own search token is a string literal that would
	/// otherwise match itself.
	/// </summary>
	private static bool IsScannable(string file)
		=> !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
			&& !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
			&& Path.GetFileName(file) != $"{nameof(DatabaseSchemaConventionTests)}.cs";

	private static IEnumerable<int> CallSiteIndexes(string source, string token)
	{
		for (int index = source.IndexOf(token, StringComparison.Ordinal); index >= 0;
			index = source.IndexOf(token, index + token.Length, StringComparison.Ordinal))
		{
			yield return index;
		}
	}

	/// <summary>
	/// The text between the parentheses starting at <paramref name="openParenIndex"/>. Parentheses inside
	/// string literals are not special-cased — no call site in this repository has any, and a guard test
	/// does not warrant a C# parser.
	/// </summary>
	private static string BalancedParenthesesSpan(string source, int openParenIndex)
	{
		int depth = 0;
		for (int index = openParenIndex; index < source.Length; index++)
		{
			if (source[index] == '(')
			{
				depth++;
			}
			else if (source[index] == ')')
			{
				depth--;
				if (depth == 0)
				{
					return source[openParenIndex..(index + 1)];
				}
			}
		}

		return source[openParenIndex..];
	}

	/// <summary>Walks up from the test assembly to the directory holding <c>Receipts.slnx</c>.</summary>
	private static string RepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Receipts.slnx")))
		{
			directory = directory.Parent;
		}

		directory.Should().NotBeNull("the tests must run from within the repository so the sources can be scanned");
		return directory!.FullName;
	}

	#endregion
}
