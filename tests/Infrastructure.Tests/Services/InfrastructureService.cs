using Application.Interfaces;
using Application.Interfaces.Services;
using Common;
using FluentAssertions;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.Services;

public class InfrastructureServiceTests
{
	#region IsDatabaseConfigured

	[Fact]
	public void IsDatabaseConfigured_AspireConnectionStringPresent_ReturnsTrue()
	{
		// Arrange
		Dictionary<string, string?> config = new()
		{
			[$"ConnectionStrings:{ConfigurationVariables.AspireConnectionStringName}"] = "Host=db;Database=receiptsdb"
		};
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(config)
			.Build();

		// Act
		bool result = InfrastructureService.IsDatabaseConfigured(configuration);

		// Assert
		result.Should().BeTrue();
	}

	[Fact]
	public void IsDatabaseConfigured_AllPostgresVarsPresent_ReturnsTrue()
	{
		// Arrange
		IConfiguration configuration = BuildPostgresConfiguration();

		// Act
		bool result = InfrastructureService.IsDatabaseConfigured(configuration);

		// Assert
		result.Should().BeTrue();
	}

	[Fact]
	public void IsDatabaseConfigured_NoConfigAtAll_ReturnsFalse()
	{
		// Arrange
		Dictionary<string, string?> config = new();
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(config)
			.Build();

		// Act
		bool result = InfrastructureService.IsDatabaseConfigured(configuration);

		// Assert
		result.Should().BeFalse();
	}

	[Theory]
	[InlineData(ConfigurationVariables.PostgresHost)]
	[InlineData(ConfigurationVariables.PostgresPort)]
	[InlineData(ConfigurationVariables.PostgresUser)]
	[InlineData(ConfigurationVariables.PostgresPassword)]
	[InlineData(ConfigurationVariables.PostgresDb)]
	public void IsDatabaseConfigured_IndividualPostgresVarMissing_ReturnsFalse(string missingKey)
	{
		// Arrange — all vars present except the one being tested
		Dictionary<string, string?> config = new()
		{
			[ConfigurationVariables.PostgresHost] = "localhost",
			[ConfigurationVariables.PostgresPort] = "5432",
			[ConfigurationVariables.PostgresUser] = "user",
			[ConfigurationVariables.PostgresPassword] = "password",
			[ConfigurationVariables.PostgresDb] = "database"
		};
		config.Remove(missingKey);

		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(config)
			.Build();

		// Act
		bool result = InfrastructureService.IsDatabaseConfigured(configuration);

		// Assert
		result.Should().BeFalse();
	}

	[Theory]
	[InlineData(ConfigurationVariables.PostgresHost)]
	[InlineData(ConfigurationVariables.PostgresPort)]
	[InlineData(ConfigurationVariables.PostgresUser)]
	[InlineData(ConfigurationVariables.PostgresPassword)]
	[InlineData(ConfigurationVariables.PostgresDb)]
	public void IsDatabaseConfigured_IndividualPostgresVarEmpty_ReturnsFalse(string emptyKey)
	{
		// Arrange — all vars present but one is empty string
		Dictionary<string, string?> config = new()
		{
			[ConfigurationVariables.PostgresHost] = "localhost",
			[ConfigurationVariables.PostgresPort] = "5432",
			[ConfigurationVariables.PostgresUser] = "user",
			[ConfigurationVariables.PostgresPassword] = "password",
			[ConfigurationVariables.PostgresDb] = "database"
		};
		config[emptyKey] = "";

		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(config)
			.Build();

		// Act
		bool result = InfrastructureService.IsDatabaseConfigured(configuration);

		// Assert
		result.Should().BeFalse();
	}

	[Fact]
	public void IsDatabaseConfigured_AspireConnectionStringEmpty_FallsBackToPostgresVars()
	{
		// Arrange — Aspire string is empty but Postgres vars are present
		Dictionary<string, string?> config = new()
		{
			[$"ConnectionStrings:{ConfigurationVariables.AspireConnectionStringName}"] = "",
			[ConfigurationVariables.PostgresHost] = "localhost",
			[ConfigurationVariables.PostgresPort] = "5432",
			[ConfigurationVariables.PostgresUser] = "user",
			[ConfigurationVariables.PostgresPassword] = "password",
			[ConfigurationVariables.PostgresDb] = "database"
		};
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(config)
			.Build();

		// Act
		bool result = InfrastructureService.IsDatabaseConfigured(configuration);

		// Assert
		result.Should().BeTrue();
	}

	#endregion

	#region GetConnectionString

	[Fact]
	public void GetConnectionString_AspireConnectionStringPresent_ReturnsAspireString()
	{
		// Arrange
		const string expectedConnectionString = "Host=aspire-db;Database=receiptsdb;Username=admin;Password=secret";
		Dictionary<string, string?> config = new()
		{
			[$"ConnectionStrings:{ConfigurationVariables.AspireConnectionStringName}"] = expectedConnectionString
		};
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(config)
			.Build();

		// Act
		string result = InfrastructureService.GetConnectionString(configuration);

		// Assert
		result.Should().Be(expectedConnectionString);
	}

	[Fact]
	public void GetConnectionString_NoAspire_BuildsFromPostgresVars()
	{
		// Arrange
		IConfiguration configuration = BuildPostgresConfiguration();

		// Act
		string result = InfrastructureService.GetConnectionString(configuration);

		// Assert
		result.Should().Contain("Host=localhost");
		result.Should().Contain("Port=5432");
		result.Should().Contain("Username=user");
		result.Should().Contain("Password=password");
		result.Should().Contain("Database=testdb");
	}

	[Fact]
	public void GetConnectionString_AspireConnectionStringEmpty_BuildsFromPostgresVars()
	{
		// Arrange — Aspire string is empty, falls back to Postgres vars
		Dictionary<string, string?> config = new()
		{
			[$"ConnectionStrings:{ConfigurationVariables.AspireConnectionStringName}"] = "",
			[ConfigurationVariables.PostgresHost] = "fallback-host",
			[ConfigurationVariables.PostgresPort] = "5433",
			[ConfigurationVariables.PostgresUser] = "fallback-user",
			[ConfigurationVariables.PostgresPassword] = "fallback-pass",
			[ConfigurationVariables.PostgresDb] = "fallback-db"
		};
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(config)
			.Build();

		// Act
		string result = InfrastructureService.GetConnectionString(configuration);

		// Assert
		result.Should().Contain("Host=fallback-host");
		result.Should().Contain("Port=5433");
		result.Should().Contain("Username=fallback-user");
		result.Should().Contain("Password=fallback-pass");
		result.Should().Contain("Database=fallback-db");
	}

	#endregion

	#region RegisterInfrastructureServices

	[Fact]
	public void RegisterInfrastructureServices_DatabaseConfigured_RegistersRequiredServices()
	{
		// Arrange
		ServiceCollection services = new();
		services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
		IConfiguration configuration = BuildPostgresConfiguration();

		// Act
		services.RegisterInfrastructureServices(configuration);
		ServiceProvider serviceProvider = services.BuildServiceProvider();

		// Assert
		serviceProvider.GetService<IDbContextFactory<ApplicationDbContext>>().Should().NotBeNull();
		serviceProvider.GetService<IReceiptService>().Should().NotBeNull();
		serviceProvider.GetService<ICardService>().Should().NotBeNull();
		serviceProvider.GetService<ITransactionService>().Should().NotBeNull();
		serviceProvider.GetService<IReceiptItemService>().Should().NotBeNull();
		serviceProvider.GetService<IDatabaseMigratorService>().Should().NotBeNull();
		serviceProvider.GetService<CardMapper>().Should().NotBeNull();
		serviceProvider.GetService<ReceiptMapper>().Should().NotBeNull();
		serviceProvider.GetService<ReceiptItemMapper>().Should().NotBeNull();
		serviceProvider.GetService<TransactionMapper>().Should().NotBeNull();
	}

	[Fact]
	public void RegisterInfrastructureServices_DatabaseNotConfigured_RegistersFallbackServices()
	{
		// Arrange — no database config at all, triggers the unconfigured (else) branch
		ServiceCollection services = new();
		services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
		Dictionary<string, string?> config = new();
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(config)
			.Build();

		// Act
		services.RegisterInfrastructureServices(configuration);
		ServiceProvider serviceProvider = services.BuildServiceProvider();

		// Assert — DbContextFactory is still registered (via the unconfigured Npgsql path)
		serviceProvider.GetService<IDbContextFactory<ApplicationDbContext>>().Should().NotBeNull();
		// All services and mappers are still registered regardless of DB config
		serviceProvider.GetService<IReceiptService>().Should().NotBeNull();
		serviceProvider.GetService<ICardService>().Should().NotBeNull();
		serviceProvider.GetService<IDatabaseMigratorService>().Should().NotBeNull();
		serviceProvider.GetService<CardMapper>().Should().NotBeNull();
		serviceProvider.GetService<ReceiptMapper>().Should().NotBeNull();
	}

	// RECEIPTS-830: the history table must carry an explicit `public` schema. Left unqualified, EF's
	// unconditional CREATE TABLE IF NOT EXISTS resolves to current_schema(), which is the `receipts`
	// schema whenever the connecting role is also named `receipts` (PostgreSQL's default search_path is
	// "$user", public). That creates an empty shadow history table and replays every migration against a
	// populated database.
	[Fact]
	public void RegisterInfrastructureServices_DatabaseConfigured_PinsMigrationsHistoryToPublicSchema()
	{
		// Arrange
		ServiceCollection services = new();
		services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
		IConfiguration configuration = BuildPostgresConfiguration();

		// Act
		services.RegisterInfrastructureServices(configuration);
		ServiceProvider serviceProvider = services.BuildServiceProvider();
		DbContextOptions<ApplicationDbContext> options =
			serviceProvider.GetRequiredService<DbContextOptions<ApplicationDbContext>>();

		// Assert
		RelationalOptionsExtension extension = RelationalOptionsExtension.Extract(options);
		extension.MigrationsHistoryTableSchema.Should().Be("public");
		extension.MigrationsHistoryTableName.Should().Be(HistoryRepository.DefaultTableName);
	}

	[Fact]
	public void RegisterInfrastructureServices_ReturnsServiceCollection()
	{
		// Arrange
		ServiceCollection services = new();
		services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
		IConfiguration configuration = BuildPostgresConfiguration();

		// Act
		IServiceCollection result = services.RegisterInfrastructureServices(configuration);

		// Assert
		result.Should().BeSameAs(services);
	}

	#endregion

	#region Helpers

	private static IConfiguration BuildPostgresConfiguration()
	{
		Dictionary<string, string?> config = new()
		{
			[ConfigurationVariables.PostgresHost] = "localhost",
			[ConfigurationVariables.PostgresPort] = "5432",
			[ConfigurationVariables.PostgresUser] = "user",
			[ConfigurationVariables.PostgresPassword] = "password",
			[ConfigurationVariables.PostgresDb] = "testdb"
		};
		return new ConfigurationBuilder()
			.AddInMemoryCollection(config)
			.Build();
	}

	#endregion
}
