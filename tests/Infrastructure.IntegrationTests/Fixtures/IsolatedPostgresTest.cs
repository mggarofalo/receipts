namespace Infrastructure.IntegrationTests.Fixtures;

// xUnit creates a new test class instance for every case. The collection fixture
// supplies its server; this lifecycle supplies a fully migrated, private database.
public abstract class IsolatedPostgresTest(PostgresFixture server) : IAsyncLifetime
{
	protected PostgresFixture fixture = null!;

	public async Task InitializeAsync() => fixture = await server.CreateIsolatedAsync();

	protected async Task ReplaceDatabaseAsync()
	{
		PostgresFixture previous = fixture;
		fixture = await server.CreateIsolatedAsync();
		await previous.DisposeAsync();
	}

	public Task DisposeAsync() => fixture is null ? Task.CompletedTask : fixture.DisposeAsync();
}
