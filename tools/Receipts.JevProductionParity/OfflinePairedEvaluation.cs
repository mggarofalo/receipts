using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Classification;
using Infrastructure.Jev;
using Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Receipts.JevProductionParity;

public sealed record OfflineCase(string Id, CategorySuggestionRequest Request);
public sealed record ReplayExchange(string Body, int StatusCode = 200, string? RequestSha256 = null);
public sealed record OfflineAttempt(string CaseId, string Stage, string RequestSha256, string RequestJson, string Status);
public sealed record OfflineOutcome(string Id, string Status, CategorySuggestionResult? Baseline, CategorySuggestionResult? Treatment);
public sealed record OfflineReplay(IReadOnlyList<OfflineOutcome> Outcomes, IReadOnlyList<OfflineAttempt> Attempts,
	int TotalCases, int CompletedCases, int UnattemptedCases, bool Stopped, long ReservationNanodollars,
	long PriorCarryNanodollars, long CombinedReservationNanodollars);

/// <summary>Socket-free replay through the production assistance binding and parsers.</summary>
public static class OfflinePairedEvaluation
{
	private static readonly Guid Generation = new("11111111-1111-1111-1111-111111111111");

	public static void ValidateCases(IReadOnlyList<OfflineCase> cases, EvaluationPolicy policy, bool requireFullPacket = false)
	{
		ArgumentNullException.ThrowIfNull(cases);
		ArgumentNullException.ThrowIfNull(policy);
		if (cases.Count < 1 || cases.Count > policy.Cases || (requireFullPacket && cases.Count != policy.Cases) ||
			cases.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id) || !CategorySuggestionPolicy.IsValid(c.Request)) ||
			cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != cases.Count)
		{
			throw new ArgumentException("Invalid offline cohort.");
		}

		string taxonomy = JsonSerializer.Serialize(new { cases[0].Request.TaxonomyRevision, cases[0].Request.Categories }, ProductionParity.JsonOptions);
		if (cases.Any(c => JsonSerializer.Serialize(new { c.Request.TaxonomyRevision, c.Request.Categories }, ProductionParity.JsonOptions) != taxonomy ||
			JevSuggestionQuestions.Serialize(c.Request).Length > CategorySuggestionPolicy.MaximumPayloadBytes))
		{
			throw new ArgumentException("Offline cases require one ordered production taxonomy and valid payloads.");
		}
	}

	public static async Task<OfflineReplay> ReplayAsync(IReadOnlyList<OfflineCase> cases,
		IReadOnlyList<ReplayExchange> exchanges, EvaluationPolicy policy, CancellationToken ct = default)
	{
		ValidateCases(cases, policy);
		ArgumentNullException.ThrowIfNull(exchanges);
		if (exchanges.Count > policy.MaximumAttempts)
		{
			throw new ArgumentException("Too many replay exchanges.");
		}

		using ReplayHandler handler = new(exchanges, policy);
		using HttpClient client = new(handler);
		using JevSuggestionConcurrency concurrency = new();
		OfflineOptions options = new();
		JevCategoryAssistanceSelectedProvider provider = new(new(client, options, concurrency), options, new OfflineBinding());
		List<OfflineOutcome> outcomes = [];
		bool stopped = false;
		foreach (OfflineCase item in cases)
		{
			ct.ThrowIfCancellationRequested();
			if (stopped) { outcomes.Add(new(item.Id, "unattempted", null, null)); continue; }
			handler.CaseId = item.Id;
			handler.Stage = "baseline";
			CategorySuggestionResult baseline = await provider.SuggestAsync(item.Request, Generation, ct);
			if (baseline.Failure is not null)
			{
				outcomes.Add(new(item.Id, handler.Unknown ? "unknown" : "failed", baseline, null));
				stopped = true;
				continue;
			}
			CategorySuggestionResult treatment = baseline;
			if (baseline.Suggestion!.CategoryId is not null)
			{
				handler.Stage = "selected-path";
				treatment = await provider.VerifySelectedAsync(item.Request, baseline.Suggestion, Generation, ct);
			}
			string status = treatment.Failure is not null ? handler.Unknown ? "unknown" : "failed" : "completed";
			outcomes.Add(new(item.Id, status, baseline, treatment));
			stopped = status != "completed";
		}
		long reservation = checked(handler.Attempts.Count * EvaluationPolicy.PerAttemptNanodollars);
		return new(outcomes, handler.Attempts, cases.Count, outcomes.Count(o => o.Status == "completed"),
			outcomes.Count(o => o.Status == "unattempted"), stopped, reservation, policy.PriorCarryNanodollars,
			checked(policy.PriorCarryNanodollars + reservation));
	}

	public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

	private sealed class ReplayHandler(IReadOnlyList<ReplayExchange> exchanges, EvaluationPolicy policy) : HttpMessageHandler
	{
		public List<OfflineAttempt> Attempts { get; } = [];
		public string CaseId { get; set; } = "";
		public string Stage { get; set; } = "";
		public bool Unknown { get; private set; }
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			if (Attempts.Count >= policy.MaximumAttempts ||
				policy.PriorCarryNanodollars + ((Attempts.Count + 1) * EvaluationPolicy.PerAttemptNanodollars) > policy.CeilingNanodollars)
			{
				throw new InvalidOperationException("Offline attempt ceiling reached.");
			}

			byte[] payload = await request.Content!.ReadAsByteArrayAsync(ct);
			string hash = Hash(payload);
			int index = Attempts.Count;
			ReplayExchange? exchange = index < exchanges.Count ? exchanges[index] : null;
			Unknown = exchange is null || exchange.RequestSha256 is not null && exchange.RequestSha256 != hash;
			// Reserve before interpreting an exchange. Missing/mismatched outcomes retain their attempt.
			Attempts.Add(new(CaseId, Stage, hash, Encoding.UTF8.GetString(payload), Unknown ? "unknown" : "replayed"));
			if (Unknown)
			{
				throw new HttpRequestException("Offline replay exchange missing or unbound.");
			}

			return new((HttpStatusCode)exchange!.StatusCode) { Content = new StringContent(exchange.Body) };
		}
	}

	private sealed class OfflineOptions : IOptionsMonitor<JevOptions>
	{
		public JevOptions CurrentValue { get; } = new() { Enabled = true, InferenceEnabled = false, ApiKey = "offline-replay-placeholder" };
		public JevOptions Get(string? name) => CurrentValue;
		public IDisposable? OnChange(Action<JevOptions, string?> listener) => null;
	}
	private sealed class OfflineBinding : ICategoryAssistanceConnectionBinding
	{
		public Task<bool> IsCurrentAsync(CategoryAssistanceConnectionCoordinator.Configuration configuration, Guid generation, CancellationToken ct = default)
		{
			ct.ThrowIfCancellationRequested();
			return Task.FromResult(generation == Generation && configuration == JevCategoryAssistanceSelectedProvider.ConfigurationFor("offline-replay-placeholder"));
		}
	}
}
