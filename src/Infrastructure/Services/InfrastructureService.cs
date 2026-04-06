using System.Net;
using Application.Interfaces;
using Application.Interfaces.Services;
using Common;
using Infrastructure.Entities;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Ynab;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Npgsql;
using Polly;

namespace Infrastructure.Services;

public static class InfrastructureService
{
	public static bool IsDatabaseConfigured(IConfiguration configuration)
	{
		// Aspire-injected connection string takes precedence
		if (!string.IsNullOrEmpty(configuration[$"ConnectionStrings:{ConfigurationVariables.AspireConnectionStringName}"]))
		{
			return true;
		}

		// Fall back to individual POSTGRES_* environment variables (non-Aspire deployments)
		return !string.IsNullOrEmpty(configuration[ConfigurationVariables.PostgresHost])
			&& !string.IsNullOrEmpty(configuration[ConfigurationVariables.PostgresPort])
			&& !string.IsNullOrEmpty(configuration[ConfigurationVariables.PostgresUser])
			&& !string.IsNullOrEmpty(configuration[ConfigurationVariables.PostgresPassword])
			&& !string.IsNullOrEmpty(configuration[ConfigurationVariables.PostgresDb]);
	}

	public static string GetConnectionString(IConfiguration configuration)
	{
		// Aspire-injected connection string (set by WithReference(db) in AppHost)
		string? aspireConnectionString = configuration[$"ConnectionStrings:{ConfigurationVariables.AspireConnectionStringName}"];
		if (!string.IsNullOrEmpty(aspireConnectionString))
		{
			return aspireConnectionString;
		}

		// Build from individual POSTGRES_* environment variables
		Npgsql.NpgsqlConnectionStringBuilder builder = new()
		{
			Host = configuration[ConfigurationVariables.PostgresHost]!,
			Port = int.Parse(configuration[ConfigurationVariables.PostgresPort]!),
			Username = configuration[ConfigurationVariables.PostgresUser]!,
			Password = configuration[ConfigurationVariables.PostgresPassword]!,
			Database = configuration[ConfigurationVariables.PostgresDb]!
		};

		return builder.ConnectionString;
	}

	public static IServiceCollection RegisterInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
	{
		if (IsDatabaseConfigured(configuration))
		{
			services.AddSingleton<NpgsqlDataSource>(sp =>
			{
				NpgsqlDataSourceBuilder dataSourceBuilder = new(GetConnectionString(configuration));
				dataSourceBuilder.UseVector();
				return dataSourceBuilder.Build();
			});

			services.AddDbContextFactory<ApplicationDbContext>((sp, options) =>
			{
				NpgsqlDataSource dataSource = sp.GetRequiredService<NpgsqlDataSource>();
				options.UseNpgsql(dataSource, b =>
				{
					string? assemblyName = typeof(ApplicationDbContext).Assembly.FullName;
					b.MigrationsAssembly(assemblyName);
					b.UseVector();
				});
				options.ConfigureWarnings(w => w.Log(
					(RelationalEventId.PendingModelChangesWarning, LogLevel.Warning)));
			});
		}
		else
		{
			services.AddDbContextFactory<ApplicationDbContext>(options =>
			{
				options.UseNpgsql();
				options.ConfigureWarnings(w => w.Log(
					(RelationalEventId.PendingModelChangesWarning, LogLevel.Warning)));
			});
		}

		// Fallback ICurrentUserAccessor for when no HTTP context is available (tests, background services).
		// The API layer registers the real implementation before this, so TryAdd is a no-op in production.
		services.TryAddScoped<ICurrentUserAccessor, NullCurrentUserAccessor>();

		services
			.AddIdentityCore<ApplicationUser>()
			.AddRoles<IdentityRole>()
			.AddEntityFrameworkStores<ApplicationDbContext>();

		// Override the factory's scoped ApplicationDbContext registration to use 2-param constructor.
		// AddDbContextFactory auto-registers a scoped context that delegates to the singleton factory,
		// which uses root provider and can't resolve scoped ICurrentUserAccessor.
		// AddEntityFrameworkStores also re-registers the scoped context, so this MUST come after it.
		services.AddScoped(sp =>
		{
			DbContextOptions<ApplicationDbContext> options =
				sp.GetRequiredService<DbContextOptions<ApplicationDbContext>>();
			ICurrentUserAccessor accessor = sp.GetRequiredService<ICurrentUserAccessor>();
			return new ApplicationDbContext(options, accessor);
		});

		services
			.AddScoped<IReceiptService, ReceiptService>()
			.AddScoped<IAccountService, AccountService>()
			.AddScoped<ICategoryService, CategoryService>()
			.AddScoped<ISubcategoryService, SubcategoryService>()
			.AddScoped<ITransactionService, TransactionService>()
			.AddScoped<IAdjustmentService, AdjustmentService>()
			.AddScoped<IReceiptItemService, ReceiptItemService>()
			.AddScoped<ICompleteReceiptService, CompleteReceiptService>()
			.AddScoped<IBackupImportService, BackupImportService>()
			.AddScoped<IItemTemplateService, ItemTemplateService>()
			.AddScoped<IItemTemplateSimilarityService, ItemTemplateSimilarityService>()
			.AddScoped<IReceiptRepository, ReceiptRepository>()
			.AddScoped<IAccountRepository, AccountRepository>()
			.AddScoped<ICategoryRepository, CategoryRepository>()
			.AddScoped<ISubcategoryRepository, SubcategoryRepository>()
			.AddScoped<ITransactionRepository, TransactionRepository>()
			.AddScoped<IAdjustmentRepository, AdjustmentRepository>()
			.AddScoped<IReceiptItemRepository, ReceiptItemRepository>()
			.AddScoped<IItemTemplateRepository, ItemTemplateRepository>()
			.AddScoped<IDatabaseMigratorService, DatabaseMigratorService>()
			.AddScoped<ITokenService, TokenService>()
			.AddScoped<IApiKeyService, ApiKeyService>()
			.AddScoped<IAuditService, AuditService>()
			.AddScoped<IAuthAuditService, AuthAuditService>()
			.AddScoped<IUserService, UserService>()
			.AddScoped<ITrashService, TrashService>()
			.AddScoped<IDashboardService, DashboardService>()
			.AddScoped<IReportService, ReportService>()
			.AddScoped<IBackupService, BackupService>()
			.AddScoped<IImageStorageService, LocalImageStorageService>()
			.AddScoped<IImageProcessingService, ImageProcessingService>()
			.AddScoped<IYnabSyncRecordRepository, YnabSyncRecordRepository>()
			.AddScoped<IYnabBudgetSelectionRepository, YnabBudgetSelectionRepository>()
			.AddScoped<IYnabAccountMappingRepository, YnabAccountMappingRepository>()
			.AddScoped<IYnabCategoryMappingRepository, YnabCategoryMappingRepository>()
			.AddScoped<IYnabBudgetSelectionService, YnabBudgetSelectionService>()
			.AddScoped<IYnabSyncRecordService, YnabSyncRecordService>()
			.AddScoped<IYnabAccountMappingService, YnabAccountMappingService>()
			.AddScoped<IYnabCategoryMappingService, YnabCategoryMappingService>()
			.AddScoped<IYnabMemoSyncService, YnabMemoSyncService>()
			.AddSingleton<IYnabSplitCalculator, YnabSplitCalculator>();

		services.AddMemoryCache();

		YnabClientOptions ynabOptions = new();
		services.AddHttpClient<IYnabApiClient, YnabApiClient>(client =>
		{
			client.BaseAddress = new Uri(ynabOptions.BaseUrl.TrimEnd('/') + "/");
		})
		.AddResilienceHandler("ynab", builder =>
		{
			builder.AddRetry(new HttpRetryStrategyOptions
			{
				MaxRetryAttempts = 3,
				BackoffType = DelayBackoffType.Exponential,
				UseJitter = true,
				ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
					.HandleResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
					.Handle<HttpRequestException>(),
			});
			builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
			{
				SamplingDuration = TimeSpan.FromSeconds(30),
				FailureRatio = 0.5,
				MinimumThroughput = 5,
				BreakDuration = TimeSpan.FromSeconds(60),
			});
		});

		// Singleton AI/ML services (local models — always available)
		services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();
		RegisterOcrEngine(services, configuration);
		services.AddHostedService<EmbeddingGenerationService>();

		services.AddHostedService<AuthAuditCleanupService>();

		services
			.AddSingleton<AccountMapper>()
			.AddSingleton<CategoryMapper>()
			.AddSingleton<SubcategoryMapper>()
			.AddSingleton<ReceiptMapper>()
			.AddSingleton<ReceiptItemMapper>()
			.AddSingleton<TransactionMapper>()
			.AddSingleton<AdjustmentMapper>()
			.AddSingleton<ItemTemplateMapper>();

		return services;
	}

	/// <summary>
	/// Registers the configured OCR engine as a singleton <see cref="IOcrEngine"/>.
	/// Reads <c>Ocr:Engine</c> from configuration — valid values are "Tesseract" (default) and "PaddleOCR".
	/// </summary>
	internal static void RegisterOcrEngine(IServiceCollection services, IConfiguration configuration)
	{
		string engineName = configuration[ConfigurationVariables.OcrEngine] ?? "Tesseract";

		if (string.Equals(engineName, "PaddleOCR", StringComparison.OrdinalIgnoreCase))
		{
			services.AddSingleton<IOcrEngine, PaddleOcrEngine>();
		}
		else
		{
			services.AddSingleton<IOcrEngine, TesseractOcrEngine>();
		}
	}
}
