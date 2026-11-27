namespace Infrastructure.IntegrationTests.Fixtures;

// Historical replay starts from a database at the baseline, never a downgraded one: newer
// migrations may deliberately refuse downgrade to preserve immutable history.
internal sealed class MigrationDatabase(PostgresFixture database) : IAsyncDisposable
{
	public static async Task<MigrationDatabase> CreateAsync(PostgresFixture fixture, string baseline) =>
		new(await fixture.CreateIsolatedAsync(baseline));

	public ValueTask<Npgsql.NpgsqlConnection> OpenConnectionAsync() => database.OpenConnectionAsync();

	public ApplicationDbContext CreateDbContext() => database.CreateDbContext();

	public async ValueTask DisposeAsync() => await database.DisposeAsync();
}
