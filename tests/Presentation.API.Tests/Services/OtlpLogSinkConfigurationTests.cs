using API.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace Presentation.API.Tests.Services;

public class OtlpLogSinkConfigurationTests
{
	private const string DefaultServiceName = "Receipts.API";
	private const string Endpoint = "https://otel.example.com";

	private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
	{
		return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
	}

	private static BatchedOpenTelemetrySinkOptions Apply(Dictionary<string, string?> values)
	{
		Action<BatchedOpenTelemetrySinkOptions>? configure =
			OtlpLogSinkConfiguration.Create(BuildConfiguration(values), DefaultServiceName);
		configure.Should().NotBeNull();

		BatchedOpenTelemetrySinkOptions options = new();
		configure!(options);
		return options;
	}

	private static Dictionary<string, string?> WithEndpoint(params (string Key, string? Value)[] extra)
	{
		Dictionary<string, string?> values = new()
		{
			[OtlpLogSinkConfiguration.EndpointKey] = Endpoint,
		};
		foreach ((string key, string? value) in extra)
		{
			values[key] = value;
		}

		return values;
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Create_WithoutEndpoint_ReturnsNull(string? endpoint)
	{
		IConfiguration configuration = BuildConfiguration(new()
		{
			[OtlpLogSinkConfiguration.EndpointKey] = endpoint,
			[OtlpLogSinkConfiguration.HeadersKey] = "Authorization=Basic abc",
		});

		OtlpLogSinkConfiguration.Create(configuration, DefaultServiceName).Should().BeNull();
	}

	[Theory]
	[InlineData(null)]
	[InlineData("grpc")]
	public void Create_WithGrpcOrUnsetProtocol_UsesGrpcAndEndpointUnchanged(string? protocol)
	{
		BatchedOpenTelemetrySinkOptions options = Apply(WithEndpoint((OtlpLogSinkConfiguration.ProtocolKey, protocol)));

		options.Protocol.Should().Be(OtlpProtocol.Grpc);
		options.LogsEndpoint.Should().Be(Endpoint);
	}

	[Theory]
	[InlineData("https://otel.example.com", "https://otel.example.com/v1/logs")]
	[InlineData("http://otel-collector:4318", "http://otel-collector:4318/v1/logs")]
	public void Create_WithHttpProtobuf_DerivesLogsEndpointFromBase(string endpoint, string expectedLogsEndpoint)
	{
		BatchedOpenTelemetrySinkOptions options = Apply(new()
		{
			[OtlpLogSinkConfiguration.EndpointKey] = endpoint,
			[OtlpLogSinkConfiguration.ProtocolKey] = "http/protobuf",
		});

		options.Protocol.Should().Be(OtlpProtocol.HttpProtobuf);
		options.LogsEndpoint.Should().Be(expectedLogsEndpoint);
	}

	[Fact]
	public void Create_WithUnsupportedProtocol_Throws()
	{
		IConfiguration configuration = BuildConfiguration(WithEndpoint((OtlpLogSinkConfiguration.ProtocolKey, "http/json")));

		Action act = () => OtlpLogSinkConfiguration.Create(configuration, DefaultServiceName);

		act.Should().Throw<InvalidOperationException>()
			.WithMessage($"*{OtlpLogSinkConfiguration.ProtocolKey}*");
	}

	[Fact]
	public void Create_WithBase64Header_SplitsOnFirstEqualsAndKeepsPadding()
	{
		BatchedOpenTelemetrySinkOptions options = Apply(WithEndpoint(
			(OtlpLogSinkConfiguration.HeadersKey, "Authorization=Basic YXBwOnNlY3JldA==")));

		options.Headers.Should().ContainKey("Authorization")
			.WhoseValue.Should().Be("Basic YXBwOnNlY3JldA==");
	}

	[Fact]
	public void Create_WithSeveralHeaders_TrimsSkipsEmptyEntriesAndPercentDecodes()
	{
		BatchedOpenTelemetrySinkOptions options = Apply(WithEndpoint(
			(OtlpLogSinkConfiguration.HeadersKey, " Authorization = Basic%20abc , X-Scope-OrgID=tenant-1 ,,")));

		options.Headers.Should().BeEquivalentTo(new Dictionary<string, string>
		{
			["Authorization"] = "Basic abc",
			["X-Scope-OrgID"] = "tenant-1",
		});
	}

	[Theory]
	[InlineData("Authorization=Basic ok,TopSecretToken-9f3a")]
	[InlineData("Authorization=Basic ok,=TopSecretToken-9f3a")]
	public void Create_WithMalformedHeaderEntry_ThrowsWithoutEchoingSecrets(string headers)
	{
		IConfiguration configuration = BuildConfiguration(WithEndpoint((OtlpLogSinkConfiguration.HeadersKey, headers)));

		Action act = () => OtlpLogSinkConfiguration.Create(configuration, DefaultServiceName);

		InvalidOperationException exception = act.Should().Throw<InvalidOperationException>().Which;
		exception.Message.Should().Contain(OtlpLogSinkConfiguration.HeadersKey);
		exception.Message.Should().NotContain("TopSecretToken-9f3a");
		exception.Message.Should().NotContain("Basic ok");
	}

	[Theory]
	[InlineData(null, DefaultServiceName)]
	[InlineData("receipts-api-prod", "receipts-api-prod")]
	public void Create_SetsServiceName_FromOtelServiceNameOrDefault(string? configured, string expected)
	{
		BatchedOpenTelemetrySinkOptions options = Apply(WithEndpoint((OtlpLogSinkConfiguration.ServiceNameKey, configured)));

		options.ResourceAttributes.Should().ContainKey("service.name")
			.WhoseValue.Should().Be(expected);
	}

	[Fact]
	public void Create_WithServiceNameInResourceAttributesOnly_UsesResourceAttribute()
	{
		BatchedOpenTelemetrySinkOptions options = Apply(WithEndpoint(
			(OtlpLogSinkConfiguration.ResourceAttributesKey, "service.name=from-attrs")));

		options.ResourceAttributes.Should().ContainKey("service.name")
			.WhoseValue.Should().Be("from-attrs");
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void Create_WithBlankOtelServiceName_FallsBackToResourceAttribute(string configured)
	{
		BatchedOpenTelemetrySinkOptions options = Apply(WithEndpoint(
			(OtlpLogSinkConfiguration.ServiceNameKey, configured),
			(OtlpLogSinkConfiguration.ResourceAttributesKey, "service.name=from-attrs")));

		options.ResourceAttributes.Should().ContainKey("service.name")
			.WhoseValue.Should().Be("from-attrs");
	}

	[Fact]
	public void Create_WithServiceNameInBothSources_PrefersOtelServiceName()
	{
		BatchedOpenTelemetrySinkOptions options = Apply(WithEndpoint(
			(OtlpLogSinkConfiguration.ServiceNameKey, "receipts-api-prod"),
			(OtlpLogSinkConfiguration.ResourceAttributesKey, "service.name=from-attrs")));

		options.ResourceAttributes.Should().ContainKey("service.name")
			.WhoseValue.Should().Be("receipts-api-prod");
	}

	[Theory]
	[InlineData("deployment.environment=production,no-separator,service.version=1.2.3")]
	[InlineData("deployment.environment=production,=orphan-value,service.version=1.2.3")]
	[InlineData("deployment.environment=production, =orphan-value,service.version=1.2.3")]
	public void Create_WithMalformedResourceAttributeEntry_SkipsItAndKeepsValidEntries(string attributes)
	{
		BatchedOpenTelemetrySinkOptions options = Apply(WithEndpoint(
			(OtlpLogSinkConfiguration.ResourceAttributesKey, attributes)));

		options.ResourceAttributes.Should().BeEquivalentTo(new Dictionary<string, object>
		{
			["deployment.environment"] = "production",
			["service.version"] = "1.2.3",
			["service.name"] = DefaultServiceName,
		});
	}

	[Fact]
	public void Create_CopiesResourceAttributes()
	{
		BatchedOpenTelemetrySinkOptions options = Apply(WithEndpoint(
			(OtlpLogSinkConfiguration.ResourceAttributesKey, "deployment.environment=production,service.version=1.2.3")));

		options.ResourceAttributes.Should().Contain("deployment.environment", "production");
		options.ResourceAttributes.Should().Contain("service.version", "1.2.3");
	}

	[Fact]
	public void AddLoggingService_WithHttpProtobufExporterConfigured_DoesNotThrow()
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			[OtlpLogSinkConfiguration.EndpointKey] = "http://otel-collector.invalid:4318",
			[OtlpLogSinkConfiguration.ProtocolKey] = "http/protobuf",
			[OtlpLogSinkConfiguration.HeadersKey] = "Authorization=Basic YXBwOnNlY3JldA==",
		});

		try
		{
			Action act = () => builder.AddLoggingService();

			act.Should().NotThrow();
		}
		finally
		{
			// AddLoggingService assigns the process-wide Log.Logger, which here owns a live
			// batching OTLP sink. Disposing it stops that sink; nothing else owns the logger.
			Log.CloseAndFlush();
		}
	}
}
