using Serilog.Sinks.OpenTelemetry;

namespace API.Services;

/// <summary>
/// Builds the Serilog OTLP sink options from the standard OTel exporter variables, so logs
/// reach the same collector, over the same protocol and with the same credentials, as the
/// traces and metrics the OTel SDK exports in <c>Receipts.ServiceDefaults</c>.
/// </summary>
/// <remarks>
/// This is the sink's only source of settings: <see cref="LoggingService"/> tells the sink
/// not to read the process environment itself. Owning the parsing lets a bad value fail
/// startup with a message that never echoes a header, since header values carry secrets.
/// </remarks>
internal static class OtlpLogSinkConfiguration
{
	internal const string EndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
	internal const string ProtocolKey = "OTEL_EXPORTER_OTLP_PROTOCOL";
	internal const string HeadersKey = "OTEL_EXPORTER_OTLP_HEADERS";
	internal const string ResourceAttributesKey = "OTEL_RESOURCE_ATTRIBUTES";
	internal const string ServiceNameKey = "OTEL_SERVICE_NAME";

	/// <summary>
	/// Returns the sink configuration, or <see langword="null"/> when no endpoint is set and
	/// logs should not be exported at all.
	/// </summary>
	internal static Action<BatchedOpenTelemetrySinkOptions>? Create(IConfiguration configuration, string defaultServiceName)
	{
		string? endpoint = configuration[EndpointKey];
		if (string.IsNullOrWhiteSpace(endpoint))
		{
			return null;
		}

		OtlpProtocol protocol = ParseProtocol(configuration[ProtocolKey]);
		List<KeyValuePair<string, string>> headers = ParseHeaders(configuration[HeadersKey]);
		List<KeyValuePair<string, string>> resourceAttributes = ParseResourceAttributes(configuration[ResourceAttributesKey]);
		string? serviceName = configuration[ServiceNameKey];

		return options =>
		{
			// Protocol first: for http/protobuf the sink derives /v1/logs from the base endpoint.
			options.Protocol = protocol;
			options.Endpoint = endpoint;

			foreach ((string key, string value) in headers)
			{
				options.Headers[key] = value;
			}

			foreach ((string key, string value) in resourceAttributes)
			{
				options.ResourceAttributes[key] = value;
			}

			// The OTel SDK's precedence: OTEL_SERVICE_NAME, then service.name in
			// OTEL_RESOURCE_ATTRIBUTES. Only then the app's own name.
			if (!string.IsNullOrWhiteSpace(serviceName))
			{
				options.ResourceAttributes["service.name"] = serviceName;
			}
			else if (!options.ResourceAttributes.ContainsKey("service.name"))
			{
				options.ResourceAttributes["service.name"] = defaultServiceName;
			}
		};
	}

	// Unset means gRPC, the OTel SDK's default, so both export paths agree.
	private static OtlpProtocol ParseProtocol(string? value) => value?.Trim() switch
	{
		null or "" or "grpc" => OtlpProtocol.Grpc,
		"http/protobuf" => OtlpProtocol.HttpProtobuf,
		_ => throw new InvalidOperationException(
			$"{ProtocolKey} '{value}' is not supported for log export. Use 'grpc' or 'http/protobuf'."),
	};

	// Headers carry credentials, so a malformed entry fails startup rather than sending
	// requests the collector will reject. The message names the position only.
	private static List<KeyValuePair<string, string>> ParseHeaders(string? value) =>
		ParseList(value, index => throw new InvalidOperationException(
			$"{HeadersKey} entry {index} is not in key=value form."));

	// Like the OTel SDK, ignore a malformed resource attribute rather than refuse to start.
	private static List<KeyValuePair<string, string>> ParseResourceAttributes(string? value) =>
		ParseList(value, _ => { });

	// The OTel list format: comma-separated key=value pairs, values percent-decoded.
	private static List<KeyValuePair<string, string>> ParseList(string? value, Action<int> onMalformed)
	{
		List<KeyValuePair<string, string>> pairs = [];
		if (string.IsNullOrWhiteSpace(value))
		{
			return pairs;
		}

		string[] entries = value.Split(',');
		for (int i = 0; i < entries.Length; i++)
		{
			string entry = entries[i];
			if (string.IsNullOrWhiteSpace(entry))
			{
				continue;
			}

			int separator = entry.IndexOf('=');
			string key = separator < 0 ? "" : entry[..separator].Trim();
			if (key.Length == 0)
			{
				onMalformed(i + 1);
				continue;
			}

			pairs.Add(new(key, Uri.UnescapeDataString(entry[(separator + 1)..].Trim())));
		}

		return pairs;
	}
}
