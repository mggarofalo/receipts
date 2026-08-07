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
					b.UsePublicMigrationsHistory();
				});
				options.ConfigureWarnings(w => w.Log(
					(RelationalEventId.PendingModelChangesWarning, LogLevel.Warning)));
			});
		}
		else
		{
			services.AddDbContextFactory<ApplicationDbContext>(options =>
			{
				options.UseNpgsql(b => b.UsePublicMigrationsHistory());
				options.ConfigureWarnings(w => w.Log(
					(RelationalEventId.PendingModelChangesWarning, LogLevel.Warning)));
			});
		}

		// Fallback ICurrentUserAccessor for when no HTTP context is available (tests, background services).
		// The API layer registers the real implementation (also a singleton) before this, so TryAdd is a
		// no-op in production. Registered as a SINGLETON (RECEIPTS-753) so the singleton IDbContextFactory
		// can resolve it from the root provider without a captive-dependency violation. NullCurrentUserAccessor
		// is stateless, and the real CurrentUserAccessor reads IHttpContextAccessor lazily on each property
		// access, so a singleton lifetime still observes the current request correctly.
		services.TryAddSingleton<ICurrentUserAccessor, NullCurrentUserAccessor>();

		services
			.AddIdentityCore<ApplicationUser>(options =>
			{
				// Account-lockout policy (RECEIPTS auth-hardening). AllowedForNewUsers makes CreateAsync
				// stamp LockoutEnabled=true so the failed-login counter can actually engage; the login
				// endpoint increments it via AccessFailedAsync and locks the account for the timespan
				// below once MaxFailedAccessAttempts consecutive failures are reached.
				options.Lockout.AllowedForNewUsers = true;
				options.Lockout.MaxFailedAccessAttempts = 5;
				options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
			})
			.AddRoles<IdentityRole>()
			.AddEntityFrameworkStores<ApplicationDbContext>();

		// Identity's EF stores resolve ApplicationDbContext as a scoped service. Route that scoped context
		// through the factory so it is built via the same 3-param constructor path as every repository
		// (ICurrentUserAccessor + IDescriptionChangeSignal injected). The factory now carries the
		// [ActivatorUtilitiesConstructor] on the 3-param ctor and both dependencies are singletons, so this
		// is correct and has no captive dependency. AddEntityFrameworkStores also registers a scoped context,
		// so this MUST come after it to win (RECEIPTS-753).
		services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());

		services
			.AddScoped<IReceiptService, ReceiptService>()
			.AddScoped<IAccountService, AccountService>()
			.AddScoped<IAccountMergeService, AccountMergeService>()
			.AddScoped<ICardService, CardService>()
			.AddScoped<ICategoryService, CategoryService>()
			.AddScoped<ISubcategoryService, SubcategoryService>()
			.AddScoped<ITransactionService, TransactionService>()
			.AddScoped<IAdjustmentService, AdjustmentService>()
			.AddScoped<IReceiptItemService, ReceiptItemService>()
			.AddScoped<ICompleteReceiptService, CompleteReceiptService>()
			.AddScoped<IBackupImportService, BackupImportService>()
			.AddScoped<IItemTemplateService, ItemTemplateService>()
			.AddScoped<IItemTemplateSimilarityService, ItemTemplateSimilarityService>()
			.AddScoped<IItemTemplateHistoryCandidateService, ItemTemplateHistoryCandidateService>()
			.AddScoped<INormalizedDescriptionService, NormalizedDescriptionService>()
			.AddScoped<IReceiptRepository, ReceiptRepository>()
			.AddScoped<IAccountRepository, AccountRepository>()
			.AddScoped<ICardRepository, CardRepository>()
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
			.AddScoped<IPdfConversionService, PdfConversionService>()
			.AddScoped<IYnabSyncRecordRepository, YnabSyncRecordRepository>()
			.AddScoped<IYnabBudgetSelectionRepository, YnabBudgetSelectionRepository>()
			.AddScoped<IYnabAccountMappingRepository, YnabAccountMappingRepository>()
			.AddScoped<IYnabCategoryMappingRepository, YnabCategoryMappingRepository>()
			.AddScoped<IYnabBudgetSelectionService, YnabBudgetSelectionService>()
			.AddScoped<IYnabSyncRecordService, YnabSyncRecordService>()
			.AddScoped<IYnabAccountMappingService, YnabAccountMappingService>()
			.AddScoped<IYnabCategoryMappingService, YnabCategoryMappingService>()
			.AddScoped<IYnabMemoSyncService, YnabMemoSyncService>()
			.AddScoped<IYnabServerKnowledgeRepository, YnabServerKnowledgeRepository>()
			.AddScoped<IYnabResponseContext, YnabResponseContext>()
			.AddSingleton<IYnabSplitCalculator, YnabSplitCalculator>();

		// Append-only YNAB sync-event log (RECEIPTS-737). TimeProvider is optional in DI, so fall
		// back to the system clock like YnabRateLimitTracker does.
		services.AddScoped<IYnabSyncEventService>(sp => new YnabSyncEventService(
			sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
			sp.GetRequiredService<ICurrentUserAccessor>(),
			sp.GetService<TimeProvider>() ?? TimeProvider.System));

		services.AddMemoryCache();

		YnabClientOptions ynabOptions = new();
		services.AddSingleton(ynabOptions);
		services.AddSingleton<IYnabRateLimitTracker>(sp =>
			new YnabRateLimitTracker(
				sp.GetRequiredService<YnabClientOptions>(),
				sp.GetService<TimeProvider>() ?? TimeProvider.System));
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
					.Handle<HttpRequestException>()
					.HandleResult(r => r.StatusCode is System.Net.HttpStatusCode.TooManyRequests
						or System.Net.HttpStatusCode.ServiceUnavailable
						or System.Net.HttpStatusCode.GatewayTimeout),
				DelayGenerator = args =>
				{
					if (args.Outcome.Result?.Headers.RetryAfter?.Delta is TimeSpan delta)
					{
						return ValueTask.FromResult<TimeSpan?>(delta);
					}

					return ValueTask.FromResult<TimeSpan?>(null); // fall back to exponential backoff
				},
			});
			builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
			{
				SamplingDuration = TimeSpan.FromSeconds(30),
				FailureRatio = 0.5,
				MinimumThroughput = 5,
				BreakDuration = TimeSpan.FromSeconds(60),
			});
		});

		// Singleton AI/ML services. The ONNX model is provisioned onto a volume at runtime
		// rather than shipped in the image (RECEIPTS-929), so this resolves whether or not
		// the model files are present yet — see OnnxEmbeddingService.IsConfigured.
		services.Configure<EmbeddingModelOptions>(configuration.GetSection(EmbeddingModelOptions.SectionName));
		services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();

		// TryAdd so callers (tests, specific deployments) can override with a FakeTimeProvider.
		services.TryAddSingleton(TimeProvider.System);

		services.AddSingleton<IDescriptionChangeSignal, DescriptionChangeSignal>();

		services
			.AddSingleton<AccountMapper>()
			.AddSingleton<CardMapper>()
			.AddSingleton<CategoryMapper>()
			.AddSingleton<SubcategoryMapper>()
			.AddSingleton<ReceiptMapper>()
			.AddSingleton<ReceiptItemMapper>()
			.AddSingleton<TransactionMapper>()
			.AddSingleton<AdjustmentMapper>()
			.AddSingleton<ItemTemplateMapper>()
			.AddSingleton<NormalizedDescriptionMapper>()
			.AddSingleton<NormalizedDescriptionSettingsMapper>();

		return services;
	}

	/// <summary>
	/// Registers the long-running background workers. Kept separate from
	/// <see cref="RegisterInfrastructureServices"/> so that short-lived CLI tools (DbSeeder,
	/// DbExporter) get the service graph without starting workers they have no use for —
	/// notably the embedding pipeline, which would otherwise load a 1.34 GB model during a
	/// migration or a seed run (RECEIPTS-929). Call this from long-running hosts only.
	/// </summary>
	public static IServiceCollection AddInfrastructureBackgroundServices(this IServiceCollection services)
	{
		// Long download with no natural retry semantics of its own; the service handles its
		// own backoff, so keep the handler out of the way and let it manage the deadline.
		services.AddHttpClient(EmbeddingModelProvisioningService.HttpClientName, client =>
		{
			client.Timeout = Timeout.InfiniteTimeSpan;
		});

		services.AddHostedService<EmbeddingModelProvisioningService>();
		services.AddHostedService<EmbeddingGenerationService>();
		services.AddHostedService<AuthAuditCleanupService>();

		// Resolver for RECEIPTS-578 — scans unresolved ReceiptItems, groups by description,
		// and links each to a NormalizedDescription via NormalizedDescriptionService.
		services.AddHostedService<NormalizedDescriptionResolutionService>();

		return services;
	}
}
