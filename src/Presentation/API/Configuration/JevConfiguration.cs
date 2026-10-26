using Application.Classification;
using Infrastructure.Jev;
using Infrastructure.Services;

namespace API.Configuration;

public static class JevConfiguration
{
	public static IHttpClientBuilder AddJevServices(this IServiceCollection services)
	{
		services.AddOptions<JevOptions>()
			.Configure<IConfiguration>((options, configuration) =>
			{
				options.Enabled = configuration.GetValue("Jev:Enabled", true);
				options.InferenceEnabled = configuration.GetValue("Jev:InferenceEnabled", false);
				options.ApiKey = configuration["TYPESAFE_API_KEY"];
			});
#pragma warning disable EXTEXP0001 // Framework API explicitly removes inherited resilience for this one-shot check.
		services.AddSingleton<JevSuggestionConcurrency>();
		services.AddScoped<CategoryAssistanceConnectionCoordinator>();
		services.AddScoped<ICategoryAssistanceConnectionReconciler>(provider =>
				provider.GetRequiredService<CategoryAssistanceConnectionCoordinator>());
		services.AddScoped<ICategoryAssistanceConnectionBinding>(provider =>
				provider.GetRequiredService<CategoryAssistanceConnectionCoordinator>());
		services.AddScoped<ICategoryAssistanceDispatchStore, CategoryAssistanceDispatchStore>();
		services.AddScoped<ICategoryAssistanceReadStore, CategoryAssistanceReadStore>();
		services.AddScoped<ICategoryAssistanceDecisionStore, CategoryAssistanceDecisionStore>();
		services.AddScoped<ICategoryAssistanceReviewPreferenceStore, CategoryAssistanceReviewPreferenceStore>();
		services.AddScoped<CategoryAssistanceDispatcher>();
		services.AddScoped<JevCategoryAssistanceSelectedProvider>();
		services.AddScoped<ICategoryAssistanceSelectedProvider>(provider =>
				provider.GetRequiredService<JevCategoryAssistanceSelectedProvider>());
		services.AddScoped<ICategoryAssistanceCapabilitySource>(provider =>
				provider.GetRequiredService<JevCategoryAssistanceSelectedProvider>());
		services.AddHttpClient<JevSuggestionProvider>()
	.RemoveAllResilienceHandlers()
	.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
	.RemoveAllLoggers();
		services.AddScoped<ICategorySuggestionProvider>(provider =>
				provider.GetRequiredService<JevSuggestionProvider>());
		return services.AddHttpClient<IClassificationProviderConnection, JevConnection>()
			// An explicit user check is one bounded attempt, without inherited retries or circuits.
			.RemoveAllResilienceHandlers()
			.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
			.RemoveAllLoggers();
#pragma warning restore EXTEXP0001
	}
}
