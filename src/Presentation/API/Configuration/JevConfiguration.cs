using Application.Classification;
using Infrastructure.Jev;

namespace API.Configuration;

public static class JevConfiguration
{
	public static IHttpClientBuilder AddJevServices(this IServiceCollection services)
	{
		services.AddOptions<JevOptions>()
			.Configure<IConfiguration>((options, configuration) =>
			{
				options.Enabled = configuration.GetValue("Jev:Enabled", true);
				options.ApiKey = configuration["TYPESAFE_API_KEY"];
			});
#pragma warning disable EXTEXP0001 // Framework API explicitly removes inherited resilience for this one-shot check.
		return services.AddHttpClient<IClassificationProviderConnection, JevConnection>()
			// An explicit user check is one bounded attempt, without inherited retries or circuits.
			.RemoveAllResilienceHandlers()
			.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
			.RemoveAllLoggers();
#pragma warning restore EXTEXP0001
	}
}
