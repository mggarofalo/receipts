using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Infrastructure;

/// <summary>
/// Shared Npgsql option wiring for EF's migrations-history table.
/// </summary>
public static class NpgsqlMigrationsHistoryExtensions
{
	/// <summary>
	/// The schema that holds <c>__EFMigrationsHistory</c> (and <c>__SeedHistory</c>). Domain tables live in
	/// bounded-context schemas as of RECEIPTS-746; the migration metadata deliberately stays in <c>public</c>.
	/// </summary>
	public const string MigrationsHistorySchema = "public";

	/// <summary>
	/// Pins the migrations-history table to an explicit <c>public</c> schema instead of letting it fall to
	/// whatever <c>current_schema()</c> happens to be.
	/// </summary>
	/// <remarks>
	/// RECEIPTS-830: without an explicit schema, EF emits the history table unqualified. Reads and writes
	/// tolerate that (an unqualified name falls through <c>search_path</c> to <c>public</c>), but
	/// <c>CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory"</c> — which EF Core 9+ issues unconditionally
	/// before taking the migration lock — resolves to the *first* schema on the path and its existence check
	/// is schema-local. PostgreSQL's default <c>search_path</c> is <c>"$user", public</c>, the deployed role is
	/// named <c>receipts</c>, and RECEIPTS-746 created a schema named <c>receipts</c> — so <c>"$user"</c>
	/// started resolving and EF created an empty second history table in the <c>receipts</c> schema. That
	/// shadow table then read back as "no migrations applied", replaying every migration from scratch against
	/// a fully populated database. Qualifying the table makes every history statement schema-explicit and
	/// immune to <c>search_path</c>.
	/// </remarks>
	public static NpgsqlDbContextOptionsBuilder UsePublicMigrationsHistory(this NpgsqlDbContextOptionsBuilder builder)
		=> builder.MigrationsHistoryTable(HistoryRepository.DefaultTableName, MigrationsHistorySchema);
}
