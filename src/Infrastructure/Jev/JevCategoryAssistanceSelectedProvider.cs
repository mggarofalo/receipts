using Application.Classification;
using Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Infrastructure.Jev;

/// <summary>Opt-in assistance binding for the same Jev request path used by direct suggestions.</summary>
public sealed class JevCategoryAssistanceSelectedProvider(
	JevSuggestionProvider provider,
	IOptionsMonitor<JevOptions> options,
	ICategoryAssistanceConnectionBinding connection)
	: ICategoryAssistanceSelectedProvider, ICategoryAssistanceCapabilitySource
{
	private static readonly Uri Server = new("https://api.typesafe.ai/");
	private const string TransportOptions = "systemone-json-v4-no-redirect-no-retry";

	public string Provider => "jev";
	public string Model => CategorySuggestionPolicy.Model;
	public bool IsAvailable => Available(options.CurrentValue);

	public bool FitsPayload(CategorySuggestionRequest request)
	{
		return request is not null && JevTwoStageQuestions.Fits(request);
	}

	public Task<CategoryAssistanceCapability> GetAsync(CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		bool available = IsAvailable;
		return Task.FromResult(new CategoryAssistanceCapability(Provider,
			available ? CategoryAssistanceCapabilityState.Ready : CategoryAssistanceCapabilityState.Unavailable,
			available ? [Model] : []));
	}

	public Task<CategorySuggestionResult> SuggestAsync(CategorySuggestionRequest request,
		Guid connectionGeneration, CancellationToken ct)
	{
		if (connectionGeneration == Guid.Empty)
		{
			return Task.FromResult(CategorySuggestionResult.Failed(SuggestionFailure.Disabled));
		}

		return provider.SuggestBoundAsync(request, async (key, token) =>
		{
			// The provider captures this key for Authorization. Only its exact persisted
			// binding may authorize the send; reconciliation belongs to runtime setup.
			JevOptions current = options.CurrentValue;
			if (!Available(current) || current.ApiKey != key)
			{
				return false;
			}

			return await connection.IsCurrentAsync(ConfigurationFor(key), connectionGeneration, token);
		}, ct);
	}

	public Task<CategorySuggestionResult> RefineAsync(CategorySuggestionRequest request, CategorySuggestion baseline,
		Guid connectionGeneration, CancellationToken ct)
	{
		if (connectionGeneration == Guid.Empty)
		{
			return Task.FromResult(CategorySuggestionResult.Failed(SuggestionFailure.Disabled));
		}

		return provider.RefineBoundAsync(request, baseline, async (key, token) =>
		{
			JevOptions current = options.CurrentValue;
			return Available(current) && current.ApiKey == key &&
				await connection.IsCurrentAsync(ConfigurationFor(key), connectionGeneration, token);
		}, ct);
	}

	public static CategoryAssistanceConnectionCoordinator.Configuration ConfigurationFor(string key) =>
		new("jev", CategorySuggestionPolicy.Model, key, Server, TransportOptions);

	private static bool Available(JevOptions value) => value.Enabled
	&& !string.IsNullOrWhiteSpace(value.ApiKey);
}
