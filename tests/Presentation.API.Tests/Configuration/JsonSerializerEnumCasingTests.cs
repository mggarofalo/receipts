using System.Text.Json;
using API.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using DomainConfidenceLevel = global::Application.Models.Ocr.ConfidenceLevel;
using DtoConfidenceLevel = global::API.Generated.Dtos.ConfidenceLevel;
using ProposedReceiptResponse = global::API.Generated.Dtos.ProposedReceiptResponse;

namespace Presentation.API.Tests.Configuration;

/// <summary>
/// Regression test for RECEIPTS-534 and RECEIPTS-660: verifies that the API's
/// configured JsonSerializerOptions serialize enum values as camelCase, both for
/// domain enums (RECEIPTS-534) and for the NSwag-generated DTO enums (RECEIPTS-660).
///
/// RECEIPTS-660 background: NSwag emits a per-property
/// <c>[JsonConverter(typeof(JsonStringEnumConverter&lt;TEnum&gt;))]</c> attribute on
/// every enum-typed DTO property. That attribute wins over the global converter
/// and falls back to C# enum names ("High"/"Low"/"Medium"/"None"), which violates
/// the OpenAPI contract that declares lowercase enum values. The fix lives in
/// <c>tools/DtoSplitter</c>: a SyntaxRewriter strips those per-property attributes
/// from the NSwag output before it is split into individual files. These tests
/// guard the contract end-to-end.
/// </summary>
public class JsonSerializerEnumCasingTests
{
	private static JsonSerializerOptions GetConfiguredJsonOptions()
	{
		ServiceCollection services = new();
		services.AddApplicationServices(new ConfigurationBuilder().Build());
		ServiceProvider provider = services.BuildServiceProvider();
		JsonOptions mvcJsonOptions = provider.GetRequiredService<IOptions<JsonOptions>>().Value;
		return mvcJsonOptions.JsonSerializerOptions;
	}

	[Fact]
	public void JsonStringEnumConverter_SerializesDomainEnumValuesAsCamelCase()
	{
		JsonSerializerOptions options = GetConfiguredJsonOptions();

		string highJson = JsonSerializer.Serialize(DomainConfidenceLevel.High, options);
		string mediumJson = JsonSerializer.Serialize(DomainConfidenceLevel.Medium, options);
		string lowJson = JsonSerializer.Serialize(DomainConfidenceLevel.Low, options);
		string noneJson = JsonSerializer.Serialize(DomainConfidenceLevel.None, options);

		highJson.Should().Be("\"high\"");
		mediumJson.Should().Be("\"medium\"");
		lowJson.Should().Be("\"low\"");
		noneJson.Should().Be("\"none\"");
	}

	[Fact]
	public void JsonStringEnumConverter_DeserializesCamelCaseDomainEnumValues()
	{
		JsonSerializerOptions options = GetConfiguredJsonOptions();

		DomainConfidenceLevel high = JsonSerializer.Deserialize<DomainConfidenceLevel>("\"high\"", options);
		DomainConfidenceLevel medium = JsonSerializer.Deserialize<DomainConfidenceLevel>("\"medium\"", options);
		DomainConfidenceLevel low = JsonSerializer.Deserialize<DomainConfidenceLevel>("\"low\"", options);
		DomainConfidenceLevel none = JsonSerializer.Deserialize<DomainConfidenceLevel>("\"none\"", options);

		high.Should().Be(DomainConfidenceLevel.High);
		medium.Should().Be(DomainConfidenceLevel.Medium);
		low.Should().Be(DomainConfidenceLevel.Low);
		none.Should().Be(DomainConfidenceLevel.None);
	}

	[Fact]
	public void JsonStringEnumConverter_SerializesGeneratedDtoEnumValuesAsCamelCase()
	{
		// RECEIPTS-660: the DTO enum lives in API.Generated.Dtos and was previously
		// shadowed by a per-property [JsonConverter] attribute that produced PascalCase
		// output. With the DtoSplitter rewriter stripping that override, the global
		// converter (camelCase) now applies.
		JsonSerializerOptions options = GetConfiguredJsonOptions();

		string highJson = JsonSerializer.Serialize(DtoConfidenceLevel.High, options);
		string mediumJson = JsonSerializer.Serialize(DtoConfidenceLevel.Medium, options);
		string lowJson = JsonSerializer.Serialize(DtoConfidenceLevel.Low, options);
		string noneJson = JsonSerializer.Serialize(DtoConfidenceLevel.None, options);

		highJson.Should().Be("\"high\"");
		mediumJson.Should().Be("\"medium\"");
		lowJson.Should().Be("\"low\"");
		noneJson.Should().Be("\"none\"");
	}

	[Fact]
	public void JsonStringEnumConverter_SerializesProposedReceiptResponse_WithLowercaseConfidence()
	{
		// RECEIPTS-660: end-to-end check. Serialize a ProposedReceiptResponse via
		// the configured options and assert each confidence field is rendered with
		// the lowercase value declared in openapi/spec.yaml.
		JsonSerializerOptions options = GetConfiguredJsonOptions();

		ProposedReceiptResponse response = new()
		{
			StoreNameConfidence = DtoConfidenceLevel.High,
			StoreAddressConfidence = DtoConfidenceLevel.Medium,
			StorePhoneConfidence = DtoConfidenceLevel.Low,
			DateConfidence = DtoConfidenceLevel.None,
			SubtotalConfidence = DtoConfidenceLevel.High,
			TotalConfidence = DtoConfidenceLevel.High,
			PaymentMethodConfidence = DtoConfidenceLevel.Medium,
			Items = [],
			TaxLines = [],
		};

		string json = JsonSerializer.Serialize(response, options);

		json.Should().Contain("\"storeNameConfidence\":\"high\"");
		json.Should().Contain("\"storeAddressConfidence\":\"medium\"");
		json.Should().Contain("\"storePhoneConfidence\":\"low\"");
		json.Should().Contain("\"dateConfidence\":\"none\"");
		json.Should().Contain("\"subtotalConfidence\":\"high\"");
		json.Should().Contain("\"totalConfidence\":\"high\"");
		json.Should().Contain("\"paymentMethodConfidence\":\"medium\"");

		// And explicitly assert no PascalCase leaks through.
		json.Should().NotContain("\"High\"");
		json.Should().NotContain("\"Medium\"");
		json.Should().NotContain("\"Low\"");
		json.Should().NotContain("\"None\"");
	}
}
