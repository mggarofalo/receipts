namespace Infrastructure.IntegrationTests.Fixtures;

// Process-wide measurements such as GC heap size count every test running in the process.
// xUnit runs a collection that disables parallelization on its own, after the parallel ones,
// so a budget measured here belongs to the test that measures it (RECEIPTS-1109).
[CollectionDefinition(Name, DisableParallelization = true)]
public class ScaleCollection : ICollectionFixture<PostgresFixture>
{
	public const string Name = "Scale";
}
