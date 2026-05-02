using System.ComponentModel.DataAnnotations;
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
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
			IDescriptionChangeSignal? signal = sp.GetService<IDescriptionChangeSignal>();
			return new ApplicationDbContext(options, accessor, signal);
		});

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
			.AddScoped<IImageValidationService, ImageValidationService>()
			.AddScoped<IPdfConversionService, PdfConversionService>()
			.AddScoped<IProposedTransactionResolver, ProposedTransactionResolver>()
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
			.AddSingleton<IYnabSplitCalculator, YnabSplitCalculator>();

		services.AddMemoryCache();

		// PDF and image-validation thresholds (RECEIPTS-638). Bound from `PdfConversion` and
		// `ImageValidation` configuration sections with DataAnnotations validation. Misconfigured
		// values fail fast at startup via ValidateOnStart so a typo'd appsettings entry can't
		// produce a runtime InvalidOperationException on first user upload.
		services.AddOptions<PdfConversionOptions>()
			.BindConfiguration(ConfigurationVariables.PdfConversionSection)
			.ValidateDataAnnotations()
			.ValidateOnStart();

		services.AddOptions<ImageValidationOptions>()
			.BindConfiguration(ConfigurationVariables.ImageValidationSection)
			.ValidateDataAnnotations()
			.ValidateOnStart();

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
		.AddResilienceHandler("ynab", AddRetryAndCircuitBreaker);

		RegisterReceiptExtractionService(services, configuration);

		// Singleton AI/ML services (local models — always available)
		services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();
		services.AddHostedService<EmbeddingGenerationService>();

		services.AddHostedService<AuthAuditCleanupService>();

		// TryAdd so callers (tests, specific deployments) can override with a FakeTimeProvider.
		services.TryAddSingleton(TimeProvider.System);

		services.AddSingleton<IDescriptionChangeSignal, DescriptionChangeSignal>();
		// Register the refresher as a singleton so both the hosted service and the
		// ItemSimilarityRefresherHealthCheck see the same instance (and therefore the
		// same consecutive-failure / last-success state).
		services.AddSingleton<ItemSimilarityEdgeRefresher>();
		services.AddHostedService(sp => sp.GetRequiredService<ItemSimilarityEdgeRefresher>());

		// Resolver for RECEIPTS-578 — scans unresolved ReceiptItems, groups by description,
		// and links each to a NormalizedDescription via NormalizedDescriptionService.
		services.AddHostedService<NormalizedDescriptionResolutionService>();

		services.AddHealthChecks()
			.AddCheck<ItemSimilarityRefresherHealthCheck>(
				"item_similarity_refresher",
				failureStatus: HealthStatus.Unhealthy,
				// Tagged "background" (not "ready") so stale edges don't gate traffic.
				tags: ["background"]);

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
	/// Resolves the Ollama base URL using the canonical fallback chain shared between the
	/// receipt-extraction HTTP client (this class) and the API startup smoke test
	/// (<c>API.Services.VlmOcrSmokeTest</c>): <c>Ocr:Vlm:OllamaUrl</c> → <c>Ollama:BaseUrl</c>
	/// (Aspire-injected) → <c>null</c>. The localhost fallback is applied separately by callers
	/// so e.g. <c>Program.cs</c> can detect the "neither key set" condition and emit a startup
	/// warning instead of silently smoke-testing the dev daemon (RECEIPTS-635).
	/// </summary>
	public static string? ResolveOllamaUrl(IConfiguration configuration)
	{
		string? configured = configuration[ConfigurationVariables.OcrVlmOllamaUrl];
		if (!string.IsNullOrWhiteSpace(configured))
		{
			return configured;
		}

		string? aspireInjected = configuration[ConfigurationVariables.OllamaBaseUrl];
		return string.IsNullOrWhiteSpace(aspireInjected) ? null : aspireInjected;
	}

	/// <summary>
	/// Registers the configured <see cref="IReceiptExtractionService"/> implementation
	/// (Ollama or Anthropic — RECEIPTS-652) with a resilience pipeline tailored to
	/// long-running VLM inferences. The provider is selected via
	/// <c>Ocr:Vlm:Provider</c>: <c>ollama</c> (default, current behavior) or
	/// <c>anthropic</c> (POC hosted-VLM path).
	/// <para>
	/// Both implementations explicitly
	/// <see cref="ResilienceHttpClientBuilderExtensions.RemoveAllResilienceHandlers"/>
	/// so the standard handler injected by <c>Receipts.ServiceDefaults</c> (30s per
	/// attempt / 90s total) does NOT stack on top of the per-provider pipeline. Real
	/// VLM inferences routinely exceed 30s; without removal the documented
	/// per-provider <c>TimeoutSeconds</c> budget would never apply. See RECEIPTS-630.
	/// </para>
	/// </summary>
	internal static void RegisterReceiptExtractionService(IServiceCollection services, IConfiguration configuration)
	{
		string provider = configuration[ConfigurationVariables.OcrVlmProvider] ?? "ollama";
		switch (provider.ToLowerInvariant())
		{
			case "ollama":
				services.AddVlmOcrClient(configuration);
				break;
			case "anthropic":
				services.AddAnthropicVlmClient(configuration);
				break;
			default:
				throw new InvalidOperationException(
					$"Unknown VLM provider '{provider}'. Set {ConfigurationVariables.OcrVlmProvider} to 'ollama' or 'anthropic'.");
		}
	}

	/// <summary>
	/// Binds <see cref="VlmOcrOptions"/> from the <c>Ocr:Vlm</c> configuration section using
	/// the standard options pattern (<see cref="OptionsBuilderExtensions.ValidateDataAnnotations{TOptions}"/>
	/// + <see cref="OptionsBuilderExtensions.ValidateOnStart{TOptions}"/>) and registers the
	/// Ollama-backed <see cref="IReceiptExtractionService"/> typed HTTP client with the
	/// production resilience pipeline. Misconfigured options (e.g. <c>TimeoutSeconds=0</c>)
	/// fail loudly at startup rather than producing a confusing runtime error on the first
	/// upload. The Ollama URL fallback chain runs in <see cref="OptionsBuilderExtensions.PostConfigure{TOptions}"/>
	/// so the chain stays observable through <see cref="IOptions{TOptions}"/> rather than
	/// hidden inside an ad-hoc registration helper. See RECEIPTS-638.
	/// </summary>
	public static IServiceCollection AddVlmOcrClient(this IServiceCollection services, IConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configuration);

		services.AddOptions<VlmOcrOptions>()
			.Bind(configuration.GetSection(ConfigurationVariables.OcrVlmSection))
			.PostConfigure(options =>
			{
				// RECEIPTS-640 + RECEIPTS-638: VlmOcrOptions.OllamaUrl is a non-nullable string
				// with a localhost default, so an IsNullOrWhiteSpace gate would short-circuit
				// when neither Ocr:Vlm:OllamaUrl nor a configured override is set — silently
				// bypassing the Aspire-injected Ollama:BaseUrl. Always run the resolver: it
				// enforces the priority chain (Ocr:Vlm:OllamaUrl → Ollama:BaseUrl → null), so
				// the bound DefaultOllamaUrl only wins when neither override is configured.
				string? resolved = ResolveOllamaUrl(configuration);
				if (!string.IsNullOrWhiteSpace(resolved))
				{
					options.OllamaUrl = resolved;
				}
			})
			.ValidateDataAnnotations()
			.ValidateOnStart();

		return AddVlmOcrHttpClient(services);
	}

	/// <summary>
	/// Registers the Ollama-backed <see cref="IReceiptExtractionService"/> typed HTTP client
	/// using a pre-bound <see cref="VlmOcrOptions"/> instance. Used by the <c>VlmEval</c> tool
	/// where CLI arguments mutate the bound options before registration; production code
	/// should use the <see cref="AddVlmOcrClient(IServiceCollection, IConfiguration)"/>
	/// overload instead so DataAnnotations validation runs at startup. See RECEIPTS-638.
	/// </summary>
	public static IServiceCollection AddVlmOcrClient(this IServiceCollection services, VlmOcrOptions options)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(options);

		// The IConfiguration overload runs DataAnnotations via ValidateOnStart, but
		// OptionsWrapper<T> bypasses that pipeline. Re-enforce the same constraints
		// here so the two overloads share the contract — otherwise a VlmEval consumer
		// that hands in a misconfigured instance would surface the failure as an
		// opaque Ollama 400 instead of a clear startup error.
		List<ValidationResult> validationResults = [];
		if (!Validator.TryValidateObject(
			options,
			new ValidationContext(options),
			validationResults,
			validateAllProperties: true))
		{
			string details = string.Join("; ", validationResults.Select(r => r.ErrorMessage));
			throw new ArgumentException(
				$"{nameof(VlmOcrOptions)} failed DataAnnotations validation: {details}",
				nameof(options));
		}

		// Register the instance as IOptions<VlmOcrOptions> so consumers (OllamaReceiptExtractionService,
		// the smoke test, VlmEval's EvalRunner) all use the standard options pattern.
		services.AddSingleton<IOptions<VlmOcrOptions>>(new OptionsWrapper<VlmOcrOptions>(options));

		return AddVlmOcrHttpClient(services);
	}

	private static IServiceCollection AddVlmOcrHttpClient(IServiceCollection services)
	{
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is in evaluation; required to opt out of the standard handler injected by ServiceDefaults.
		services.AddHttpClient<IReceiptExtractionService, OllamaReceiptExtractionService>((sp, client) =>
		{
			VlmOcrOptions options = sp.GetRequiredService<IOptions<VlmOcrOptions>>().Value;
			client.BaseAddress = new Uri(options.OllamaUrl.TrimEnd('/') + "/");
			// The resilience pipeline (per-attempt Timeout strategy) owns request budgeting.
			// HttpClient.Timeout must be Infinite so it doesn't fight the pipeline.
			client.Timeout = Timeout.InfiniteTimeSpan;
		})
			.RemoveAllResilienceHandlers()
			.AddResilienceHandler("vlm-ocr", (builder, context) =>
			{
				VlmOcrOptions options = context.ServiceProvider.GetRequiredService<IOptions<VlmOcrOptions>>().Value;
				ConfigureVlmOcrResilience(builder, options);
			});
#pragma warning restore EXTEXP0001

		return services;
	}

	/// <summary>
	/// Resilience pipeline for VLM OCR HTTP calls. Order matters: retry wraps the
	/// per-attempt timeout so each retry receives a fresh <see cref="VlmOcrOptions.TimeoutSeconds"/>
	/// budget (Polly executes strategies in registration order — first added is outermost).
	/// </summary>
	internal static void ConfigureVlmOcrResilience(
		ResiliencePipelineBuilder<HttpResponseMessage> builder,
		VlmOcrOptions options)
	{
		AddRetryAndCircuitBreaker(builder);
		// Per-attempt timeout sits INSIDE the retry — each retry resets the clock.
		builder.AddTimeout(TimeSpan.FromSeconds(options.TimeoutSeconds));
	}

	/// <summary>
	/// Binds <see cref="AnthropicOptions"/> from the <c>Anthropic</c> configuration section
	/// using the standard options pattern (<see cref="OptionsBuilderExtensions.ValidateDataAnnotations{TOptions}"/>
	/// + <see cref="OptionsBuilderExtensions.ValidateOnStart{TOptions}"/>) and registers the
	/// Anthropic-backed <see cref="IReceiptExtractionService"/> typed HTTP client with the
	/// production resilience pipeline. Misconfigured options (e.g. missing <c>ApiKey</c>)
	/// fail loudly at startup rather than producing a confusing 401 on the first upload.
	/// See RECEIPTS-652.
	/// </summary>
	public static IServiceCollection AddAnthropicVlmClient(this IServiceCollection services, IConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configuration);

		services.AddOptions<AnthropicOptions>()
			.Bind(configuration.GetSection(ConfigurationVariables.AnthropicSection))
			.ValidateDataAnnotations()
			.ValidateOnStart();

		return AddAnthropicVlmHttpClient(services);
	}

	/// <summary>
	/// Registers the Anthropic-backed <see cref="IReceiptExtractionService"/> typed HTTP
	/// client using a pre-bound <see cref="AnthropicOptions"/> instance. Used by the
	/// <c>VlmEval</c> tool where CLI args may mutate the bound options before registration.
	/// Production code paths should prefer the <see cref="AddAnthropicVlmClient(IServiceCollection, IConfiguration)"/>
	/// overload so DataAnnotations validation runs at startup. Mirrors the Ollama
	/// instance overload for symmetry (RECEIPTS-638 + RECEIPTS-652).
	/// </summary>
	public static IServiceCollection AddAnthropicVlmClient(this IServiceCollection services, AnthropicOptions options)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(options);

		List<ValidationResult> validationResults = [];
		if (!Validator.TryValidateObject(
			options,
			new ValidationContext(options),
			validationResults,
			validateAllProperties: true))
		{
			string details = string.Join("; ", validationResults.Select(r => r.ErrorMessage));
			throw new ArgumentException(
				$"{nameof(AnthropicOptions)} failed DataAnnotations validation: {details}",
				nameof(options));
		}

		services.AddSingleton<IOptions<AnthropicOptions>>(new OptionsWrapper<AnthropicOptions>(options));
		return AddAnthropicVlmHttpClient(services);
	}

	private static IServiceCollection AddAnthropicVlmHttpClient(IServiceCollection services)
	{
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is in evaluation; required to opt out of the standard handler injected by ServiceDefaults.
		services.AddHttpClient<IReceiptExtractionService, AnthropicReceiptExtractionService>((sp, client) =>
		{
			AnthropicOptions options = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;
			client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
			client.DefaultRequestHeaders.Remove("x-api-key");
			client.DefaultRequestHeaders.Add("x-api-key", options.ApiKey);
			client.DefaultRequestHeaders.Remove("anthropic-version");
			client.DefaultRequestHeaders.Add("anthropic-version", options.ApiVersion);
			// The resilience pipeline (per-attempt Timeout strategy) owns request budgeting.
			// HttpClient.Timeout must be Infinite so it doesn't fight the pipeline.
			client.Timeout = Timeout.InfiniteTimeSpan;
		})
			.RemoveAllResilienceHandlers()
			.AddResilienceHandler("anthropic-vlm", (builder, context) =>
			{
				AnthropicOptions options = context.ServiceProvider.GetRequiredService<IOptions<AnthropicOptions>>().Value;
				ConfigureAnthropicVlmResilience(builder, options);
			});
#pragma warning restore EXTEXP0001

		return services;
	}

	/// <summary>
	/// Resilience pipeline for the Anthropic Messages API. Same shape as the Ollama
	/// pipeline (retry wraps the per-attempt timeout so each retry resets the clock),
	/// but parameterized by <see cref="AnthropicOptions.TimeoutSeconds"/>. See
	/// RECEIPTS-630 for the rationale on opt-out + outer retry / inner timeout ordering.
	/// </summary>
	internal static void ConfigureAnthropicVlmResilience(
		ResiliencePipelineBuilder<HttpResponseMessage> builder,
		AnthropicOptions options)
	{
		AddRetryAndCircuitBreaker(builder);
		builder.AddTimeout(TimeSpan.FromSeconds(options.TimeoutSeconds));
	}

	/// <summary>
	/// Shared retry + circuit-breaker policy used by the YNAB and VLM-OCR HTTP clients:
	/// 3 retries with exponential backoff + jitter (honoring <c>Retry-After</c> when the
	/// server provides it), and a circuit breaker on sustained failure.
	/// <para>
	/// The retry predicate (RECEIPTS-654) handles ONLY transient failures: network
	/// exceptions (<see cref="HttpRequestException"/>), client-side cancellation/timeout
	/// (<see cref="TaskCanceledException"/>), and the documented retryable HTTP codes
	/// (408, 429, 500, 502, 503, 504, 529). Permanent client errors (400, 401, 403, 404,
	/// 422) bypass the retry — repeating a request that's structurally invalid only
	/// doubles latency and burns the retry budget. This was a regression discovered when
	/// an Anthropic 400 (image-size limit) was retried unnecessarily; see RECEIPTS-654
	/// for the trace.
	/// </para>
	/// </summary>
	internal static void AddRetryAndCircuitBreaker(ResiliencePipelineBuilder<HttpResponseMessage> builder)
	{
		builder.AddRetry(new HttpRetryStrategyOptions
		{
			MaxRetryAttempts = 3,
			BackoffType = DelayBackoffType.Exponential,
			UseJitter = true,
			ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
				.Handle<HttpRequestException>()
				// TaskCanceledException covers HttpClient request timeouts (the SocketsHttpHandler
				// implementation surfaces them this way). Caller-initiated cancellations also
				// land here, but those carry the user's CancellationToken — Polly only retries
				// when the linked token is not cancelled, so user-cancels short-circuit
				// correctly without extra checks here.
				.Handle<TaskCanceledException>()
				.HandleResult(r => IsRetryableStatusCode(r.StatusCode)),
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
	}

	/// <summary>
	/// True when <paramref name="statusCode"/> represents a transient failure that warrants
	/// a retry per Anthropic's documented retry guidance and standard HTTP semantics
	/// (RECEIPTS-654). The 5xx set explicitly includes 529 — Anthropic's "API overloaded"
	/// response — which is documented as transient even though it is not in
	/// <see cref="System.Net.HttpStatusCode"/>. All other 4xx codes (400, 401, 403, 404,
	/// 422 in particular) are permanent client errors and must NOT be retried — repeating
	/// an invalid request only burns the retry budget and adds latency.
	/// </summary>
	internal static bool IsRetryableStatusCode(System.Net.HttpStatusCode statusCode)
	{
		return statusCode == System.Net.HttpStatusCode.RequestTimeout            // 408
			|| statusCode == System.Net.HttpStatusCode.TooManyRequests           // 429
			|| statusCode == System.Net.HttpStatusCode.InternalServerError       // 500
			|| statusCode == System.Net.HttpStatusCode.BadGateway                // 502
			|| statusCode == System.Net.HttpStatusCode.ServiceUnavailable        // 503
			|| statusCode == System.Net.HttpStatusCode.GatewayTimeout            // 504
			|| (int)statusCode == 529;                                            // Anthropic overloaded
	}
}
