using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Infrastructure;

/// <summary>
/// Design-time factory for EF Core migrations tooling (<c>dotnet ef migrations add</c>).
/// The <see cref="NpgsqlDataSource"/> is intentionally not disposed here — it must outlive
/// the returned <see cref="ApplicationDbContext"/>, and the process exits shortly after
/// the migration scaffold completes.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
	public ApplicationDbContext CreateDbContext(string[] args)
	{
		string connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
			?? throw new InvalidOperationException(
				"POSTGRES_CONNECTION_STRING environment variable is not set. "
				+ "Set it before running EF Core design-time commands.");

		NpgsqlDataSourceBuilder dataSourceBuilder = new(connectionString);
		dataSourceBuilder.UseVector();
		NpgsqlDataSource dataSource = dataSourceBuilder.Build();

		DbContextOptionsBuilder<ApplicationDbContext> builder = new();
		builder.UseNpgsql(dataSource, b =>
		{
			b.UseVector();
			b.UsePublicMigrationsHistory();
		});
		return new ApplicationDbContext(builder.Options);
	}
}