using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace API.Configuration;

internal static class GeneratedDtoJsonContract
{
	private const string GeneratedDtoNamespace = "API.Generated.Dtos";

	public static void Apply(JsonSerializerOptions options)
	{
		IJsonTypeInfoResolver resolver = options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
		options.TypeInfoResolver = resolver.WithAddedModifier(RemoveGeneratedEnumPropertyConverters);
	}

	private static void RemoveGeneratedEnumPropertyConverters(JsonTypeInfo typeInfo)
	{
		if (typeInfo.Kind != JsonTypeInfoKind.Object
			|| typeInfo.Type.Namespace != GeneratedDtoNamespace)
		{
			return;
		}

		foreach (JsonPropertyInfo property in typeInfo.Properties)
		{
			Type propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
			if (!propertyType.IsEnum || property.AttributeProvider is not ICustomAttributeProvider attributes)
			{
				continue;
			}

			bool hasGeneratedEnumConverter = attributes
				.GetCustomAttributes(typeof(JsonConverterAttribute), inherit: true)
				.OfType<JsonConverterAttribute>()
				.Select(attribute => attribute.ConverterType)
				.Any(converterType => converterType is { IsGenericType: true }
					&& converterType.GetGenericTypeDefinition() == typeof(JsonStringEnumConverter<>));

			if (hasGeneratedEnumConverter)
			{
				property.CustomConverter = null;
			}
		}
	}
}
