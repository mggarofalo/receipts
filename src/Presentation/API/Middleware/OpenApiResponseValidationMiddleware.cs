using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using NJsonSchema;

namespace API.Middleware;

/// <summary>
/// Development-only middleware that validates API response bodies against the OpenAPI spec.
/// Catches serialization mismatches (casing, dates, enums), missing required fields,
/// and other divergences between spec and implementation.
/// </summary>
public class OpenApiResponseValidationMiddleware
{
	private readonly RequestDelegate _next;
	private readonly ILogger<OpenApiResponseValidationMiddleware> _logger;
	private readonly bool _throwOnFailure;
	private readonly ConcurrentDictionary<string, JsonSchema> _schemaCache = new();
	private readonly JsonDocument? _specDocument;

	public OpenApiResponseValidationMiddleware(
		RequestDelegate next,
		ILogger<OpenApiResponseValidationMiddleware> logger,
		IWebHostEnvironment env,
		bool throwOnFailure = false)
	{
		_next = next;
		_logger = logger;
		_throwOnFailure = throwOnFailure;

		string specPath = Path.Combine(env.ContentRootPath, "..", "..", "..", "openapi", "generated", "API.json");
		if (File.Exists(specPath))
		{
			string json = File.ReadAllText(specPath);
			_specDocument = JsonDocument.Parse(json);

			if (_specDocument.RootElement.TryGetProperty("paths", out JsonElement paths))
			{
				int pathCount = paths.EnumerateObject().Count();
				_logger.LogInformation(
					"OpenAPI response validation middleware loaded spec with {PathCount} paths",
					pathCount);
			}
		}
		else
		{
			_logger.LogWarning("OpenAPI spec not found at {Path}. Response validation disabled", specPath);
		}
	}

	public async Task InvokeAsync(HttpContext context)
	{
		if (_specDocument is null)
		{
			await _next(context);
			return;
		}

		// Skip non-API requests and OpenAPI/health endpoints
		string path = context.Request.Path.Value ?? "";
		if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith("/api/openapi", StringComparison.OrdinalIgnoreCase))
		{
			await _next(context);
			return;
		}

		// Buffer the response body so we can read it after the handler writes it
		Stream originalBody = context.Response.Body;
		using MemoryStream bufferedBody = new();
		context.Response.Body = bufferedBody;

		try
		{
			await _next(context);

			if (context.Response.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true
				&& context.Response.StatusCode >= 200
				&& context.Response.StatusCode < 300)
			{
				bufferedBody.Seek(0, SeekOrigin.Begin);
				using StreamReader reader = new(bufferedBody, Encoding.UTF8, leaveOpen: true);
				string responseJson = await reader.ReadToEndAsync();

				if (!string.IsNullOrWhiteSpace(responseJson))
				{
					await ValidateResponseAsync(context, responseJson);
				}
			}
		}
		finally
		{
			// Copy the buffered response back to the original stream
			bufferedBody.Seek(0, SeekOrigin.Begin);
			await bufferedBody.CopyToAsync(originalBody);
			context.Response.Body = originalBody;
		}
	}

	private async Task ValidateResponseAsync(HttpContext context, string responseJson)
	{
		string method = context.Request.Method.ToLowerInvariant();
		string statusCode = context.Response.StatusCode.ToString();
		string requestPath = context.Request.Path.Value ?? "";

		// Find the matching response schema JSON from the spec
		string cacheKey = $"{method}:{requestPath}:{statusCode}";
		JsonElement? schemaElement = FindResponseSchemaElement(requestPath, method, statusCode);
		if (schemaElement is null)
		{
			return; // No schema defined — skip
		}

		try
		{
			JsonSchema schema = await GetOrCreateSchemaAsync(schemaElement.Value, cacheKey);
			ICollection<NJsonSchema.Validation.ValidationError> errors = schema.Validate(responseJson);

			if (errors.Count > 0)
			{
				string endpoint = $"{method.ToUpperInvariant()} {requestPath}";
				string errorDetails = string.Join("; ", errors.Select(e => $"{e.Path}: {e.Kind}"));

				_logger.LogWarning(
					"OpenAPI response validation failed for {Endpoint} ({StatusCode}): {Errors}",
					endpoint, statusCode, errorDetails);

				if (_throwOnFailure)
				{
					throw new InvalidOperationException(
						$"OpenAPI response validation failed for {endpoint}: {errorDetails}");
				}
			}
		}
		catch (Exception ex) when (ex is not InvalidOperationException)
		{
			_logger.LogDebug(ex, "Error during OpenAPI response validation for {Path}", requestPath);
		}
	}

	private JsonElement? FindResponseSchemaElement(string requestPath, string method, string statusCode)
	{
		if (_specDocument is null
			|| !_specDocument.RootElement.TryGetProperty("paths", out JsonElement paths))
		{
			return null;
		}

		// Find the matching path template. Prefer exact matches over parameterized
		// to avoid /api/items/{id} matching /api/items/distinct-categories.
		JsonProperty? exactMatch = null;
		JsonProperty? parameterizedMatch = null;

		foreach (JsonProperty pathEntry in paths.EnumerateObject())
		{
			if (!PathMatchesTemplate(requestPath, pathEntry.Name))
			{
				continue;
			}

			if (pathEntry.Name.Contains('{'))
			{
				parameterizedMatch ??= pathEntry;
			}
			else
			{
				exactMatch = pathEntry;
				break; // Exact match is always preferred
			}
		}

		JsonProperty? bestMatch = exactMatch ?? parameterizedMatch;
		if (bestMatch is null)
		{
			return null;
		}

		JsonProperty match = bestMatch.Value;

		if (!match.Value.TryGetProperty(method, out JsonElement operation))
		{
			return null;
		}

		if (!operation.TryGetProperty("responses", out JsonElement responses))
		{
			return null;
		}

		// Try exact status code, then "default"
		JsonElement responseElement;
		if (!responses.TryGetProperty(statusCode, out responseElement)
			&& !responses.TryGetProperty("default", out responseElement))
		{
			return null;
		}

		if (responseElement.TryGetProperty("content", out JsonElement content)
			&& content.TryGetProperty("application/json", out JsonElement mediaType)
			&& mediaType.TryGetProperty("schema", out JsonElement schema))
		{
			return schema;
		}

		return null;
	}

	private static bool PathMatchesTemplate(string requestPath, string template)
	{
		string[] requestSegments = requestPath.Trim('/').Split('/');
		string[] templateSegments = template.Trim('/').Split('/');

		if (requestSegments.Length != templateSegments.Length)
		{
			return false;
		}

		for (int i = 0; i < templateSegments.Length; i++)
		{
			string templateSeg = templateSegments[i];
			if (templateSeg.StartsWith('{') && templateSeg.EndsWith('}'))
			{
				continue;
			}

			if (!string.Equals(requestSegments[i], templateSeg, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}

		return true;
	}

	private async Task<JsonSchema> GetOrCreateSchemaAsync(JsonElement schemaElement, string cacheKey)
	{
		if (_schemaCache.TryGetValue(cacheKey, out JsonSchema? cached))
		{
			return cached;
		}

		// Resolve $ref and build a self-contained JSON Schema string
		string schemaJson = BuildResolvedSchemaJson(schemaElement);
		JsonSchema schema = await JsonSchema.FromJsonAsync(schemaJson);
		return _schemaCache.GetOrAdd(cacheKey, schema);
	}

	private string BuildResolvedSchemaJson(JsonElement element)
	{
		using MemoryStream ms = new();
		using (Utf8JsonWriter writer = new(ms))
		{
			WriteResolved(element, writer, []);
		}

		return Encoding.UTF8.GetString(ms.ToArray());
	}

	/// <summary>
	/// Writes the resolved JSON schema directly to a Utf8JsonWriter, avoiding
	/// intermediate JsonDocument allocations. The <paramref name="resolutionStack"/>
	/// tracks only the current ancestor chain so the same $ref can appear in
	/// sibling branches without being incorrectly treated as circular.
	/// </summary>
	private void WriteResolved(JsonElement element, Utf8JsonWriter writer, HashSet<string> resolutionStack)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			if (element.TryGetProperty("$ref", out JsonElement refElement))
			{
				string refPath = refElement.GetString() ?? "";
				if (refPath.StartsWith("#/") && !resolutionStack.Contains(refPath))
				{
					JsonElement? resolved = NavigateJsonPointer(refPath);
					if (resolved.HasValue)
					{
						resolutionStack.Add(refPath);
						WriteResolved(resolved.Value, writer, resolutionStack);
						resolutionStack.Remove(refPath);
						return;
					}
				}

				// Circular ref or unresolvable — write empty object
				writer.WriteStartObject();
				writer.WriteEndObject();
				return;
			}

			writer.WriteStartObject();
			foreach (JsonProperty prop in element.EnumerateObject())
			{
				writer.WritePropertyName(prop.Name);
				WriteResolved(prop.Value, writer, resolutionStack);
			}
			writer.WriteEndObject();
			return;
		}

		if (element.ValueKind == JsonValueKind.Array)
		{
			writer.WriteStartArray();
			foreach (JsonElement item in element.EnumerateArray())
			{
				WriteResolved(item, writer, resolutionStack);
			}
			writer.WriteEndArray();
			return;
		}

		element.WriteTo(writer);
	}

	private JsonElement? NavigateJsonPointer(string pointer)
	{
		if (_specDocument is null || !pointer.StartsWith("#/"))
		{
			return null;
		}

		string[] segments = pointer[2..].Split('/');
		JsonElement current = _specDocument.RootElement;

		foreach (string segment in segments)
		{
			// Unescape JSON Pointer encoding
			string unescaped = segment.Replace("~1", "/").Replace("~0", "~");

			if (current.ValueKind != JsonValueKind.Object
				|| !current.TryGetProperty(unescaped, out JsonElement next))
			{
				return null;
			}

			current = next;
		}

		return current;
	}
}

/// <summary>
/// Configuration options for OpenAPI response validation.
/// </summary>
public class OpenApiResponseValidationOptions
{
	/// <summary>
	/// When true, validation failures throw an exception instead of just logging.
	/// Default: false (log warnings only).
	/// </summary>
	public bool ThrowOnFailure { get; set; }
}

public static class OpenApiResponseValidationExtensions
{
	/// <summary>
	/// Adds OpenAPI response validation middleware (development mode only).
	/// Validates that API responses match the OpenAPI spec schemas.
	/// </summary>
	public static WebApplication UseOpenApiResponseValidation(this WebApplication app, Action<OpenApiResponseValidationOptions>? configure = null)
	{
		if (!app.Environment.IsDevelopment())
		{
			return app;
		}

		OpenApiResponseValidationOptions options = new();
		configure?.Invoke(options);

		app.UseMiddleware<OpenApiResponseValidationMiddleware>(options.ThrowOnFailure);
		return app;
	}
}
