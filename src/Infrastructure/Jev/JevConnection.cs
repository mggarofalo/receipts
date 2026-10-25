using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Application.Classification;
using Microsoft.Extensions.Options;
namespace Infrastructure.Jev;

public sealed class JevConnection(HttpClient client, IOptionsMonitor<JevOptions> options) : IClassificationProviderConnection
{
	public ProviderConnectionStatus GetStatus() => Status(options.CurrentValue);
	private static ProviderConnectionStatus Status(JevOptions current) => !current.Enabled
		? new(ProviderConnectionState.Disabled, "disabled", [])
		: string.IsNullOrWhiteSpace(current.ApiKey) ? new(ProviderConnectionState.Missing, "missingKey", [])
		: new(ProviderConnectionState.ConfiguredUnverified, "notChecked", []);
	public async Task<ProviderConnectionStatus> CheckAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		JevOptions current = options.CurrentValue;
		ProviderConnectionStatus passive = Status(current);
		if (passive.State != ProviderConnectionState.ConfiguredUnverified)
		{
			return passive;
		}

		string key = current.ApiKey!;
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(10));
		ProviderConnectionStatus result;
		try
		{
			using HttpRequestMessage request = new(HttpMethod.Get, "https://api.typesafe.ai/v1/models");
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
			using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			{
				result = new(ProviderConnectionState.Invalid, "authenticationRejected", []);
			}
			else if (response.StatusCode == HttpStatusCode.TooManyRequests)
			{
				result = new(ProviderConnectionState.Unavailable, "rateLimited", []);
			}
			else if (!response.IsSuccessStatusCode)
			{
				result = new(ProviderConnectionState.Unavailable, "providerUnavailable", []);
			}
			else
			{
				await response.Content.LoadIntoBufferAsync(65536, timeout.Token);
				ModelCatalog? catalog = JsonSerializer.Deserialize<ModelCatalog>(await response.Content.ReadAsStringAsync(timeout.Token), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
				if (catalog?.Models is null || catalog.Models.Any(model => model is null || string.IsNullOrWhiteSpace(model.Name) || model.Name.Length > 200 || model.Name.Contains(key, StringComparison.Ordinal)))
				{
					result = new(ProviderConnectionState.Unavailable, "invalidResponse", []);
				}
				else
				{
					result = new(ProviderConnectionState.Connected, "authenticationVerified", catalog.Models.Select(model => model.Name!).Distinct().ToArray());
				}
			}
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{ result = new(ProviderConnectionState.Unavailable, "timeout", []); }
		catch (Exception exception) when (exception is HttpRequestException or JsonException or FormatException)
		{ result = new(ProviderConnectionState.Unavailable, "providerUnavailable", []); }
		JevOptions latest = options.CurrentValue;
		return latest.Enabled != current.Enabled || latest.ApiKey != key ? Status(latest) : result;
	}
	private sealed class ModelCatalog { public List<ModelMetadata>? Models { get; set; } }
	private sealed class ModelMetadata { public string? Name { get; set; } }
}
