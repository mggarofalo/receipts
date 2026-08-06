using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Http;

/// <summary>
/// Builds the API's error responses as RFC 9457 problem documents.
///
/// Controllers used to answer with <c>TypedResults.BadRequest("some reason")</c>, which
/// serialises to a bare JSON <em>string</em> rather than an object. Every consumer then
/// has to special-case that shape: the React client does it in
/// <c>errorNormalizationMiddleware</c>, and any second consumer would have to reimplement
/// the same repair. Defining the error shape on the server instead is RECEIPTS-886.
///
/// These return <see cref="BadRequest{T}"/>/<see cref="Conflict{T}"/>/<see cref="NotFound{T}"/>
/// rather than <c>ProblemHttpResult</c> on purpose. The status code stays in the return
/// type, so it stays visible in the endpoint's <c>Results&lt;…&gt;</c> signature and the
/// OpenAPI document generator keeps emitting one schema per status — a
/// <c>ProblemHttpResult</c> erases that distinction and collapses several statuses into one.
/// </summary>
public static class ApiProblem
{
	// RFC 9110 section anchors, the same URIs ASP.NET's own ProblemDetailsFactory uses.
	private const string BadRequestType = "https://tools.ietf.org/html/rfc9110#section-15.5.1";
	private const string NotFoundType = "https://tools.ietf.org/html/rfc9110#section-15.5.5";
	private const string ConflictType = "https://tools.ietf.org/html/rfc9110#section-15.5.10";

	/// <summary>400 with the reason in <c>detail</c>.</summary>
	public static BadRequest<ProblemDetails> BadRequest(string detail) =>
		TypedResults.BadRequest(Build(StatusCodes.Status400BadRequest, "Bad Request", BadRequestType, detail));

	/// <summary>
	/// 400 for a batch of rejection reasons. ASP.NET Identity hands back several at once
	/// ("Passwords must have at least one digit", "…one uppercase character").
	///
	/// These previously serialised as a bare JSON <em>array</em>, which is worse than the
	/// bare-string case: the client's normaliser spreads it into <c>{0: "…", 1: "…"}</c>,
	/// finds no <c>detail</c> and no <c>title</c>, and falls back to a generic status
	/// message — so the user was told their request failed and never told why. Each entry
	/// is already a complete sentence, so joining them reads correctly.
	/// </summary>
	public static BadRequest<ProblemDetails> BadRequest(IEnumerable<string> details) =>
		BadRequest(string.Join(" ", details));

	/// <summary>404 with the reason in <c>detail</c>. Use the bodiless <c>TypedResults.NotFound()</c> when there is nothing useful to say.</summary>
	public static NotFound<ProblemDetails> NotFound(string detail) =>
		TypedResults.NotFound(Build(StatusCodes.Status404NotFound, "Not Found", NotFoundType, detail));

	/// <summary>409 with the reason in <c>detail</c>.</summary>
	public static Conflict<ProblemDetails> Conflict(string detail) =>
		TypedResults.Conflict(Build(StatusCodes.Status409Conflict, "Conflict", ConflictType, detail));

	/// <summary>
	/// 409 carrying machine-readable context alongside the prose. Callers that need a
	/// consumer to branch on something more than text — a count, a set of ids — put it in
	/// <paramref name="extensions"/> rather than encoding it into <c>detail</c>.
	/// </summary>
	public static Conflict<ProblemDetails> Conflict(string detail, IDictionary<string, object?> extensions)
	{
		ProblemDetails problem = Build(StatusCodes.Status409Conflict, "Conflict", ConflictType, detail);
		foreach (KeyValuePair<string, object?> entry in extensions)
		{
			problem.Extensions[entry.Key] = entry.Value;
		}

		return TypedResults.Conflict(problem);
	}

	private static ProblemDetails Build(int status, string title, string type, string detail) =>
		new()
		{
			Type = type,
			Title = title,
			Status = status,
			Detail = detail,
		};
}
