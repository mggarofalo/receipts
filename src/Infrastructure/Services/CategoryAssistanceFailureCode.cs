using System.Text.Json;
using Application.Classification;

namespace Infrastructure.Services;

internal static class CategoryAssistanceFailureCode
{
	// Only known enum values leave persistence. Never expose provider text or raw payloads.
	public static string? Read(string? resultJson)
	{
		if (string.IsNullOrEmpty(resultJson))
		{
			return null;
		}

		try
		{
			var completion = JsonSerializer.Deserialize<CategoryAssistanceCompletion>(resultJson);
			return completion?.Outcome == CategoryAssistanceAttemptOutcome.Failed &&
				   completion.Failure is { } failure && Enum.IsDefined(failure)
				? failure.ToString()
				: null;
		}
		catch (JsonException)
		{
			// Earlier queued rejections stored the enum directly.
			try
			{
				var failure = JsonSerializer.Deserialize<SuggestionFailure>(resultJson);
				return Enum.IsDefined(failure) ? failure.ToString() : null;
			}
			catch (JsonException)
			{
				return null;
			}
		}
	}
}
