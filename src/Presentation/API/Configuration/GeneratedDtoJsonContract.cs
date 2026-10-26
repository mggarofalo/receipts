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
		if (typeInfo.Kind == JsonTypeInfoKind.Object)
		{
			foreach (JsonPropertyInfo property in typeInfo.Properties)
			{
				// Missing nullable intent must not silently select a no-code group or clear a category.
				if ((typeInfo.Type == typeof(API.Controllers.HistoricalGroupChoiceBody)
						&& property.Name is "group" or "targetCategoryId")
					|| (typeInfo.Type == typeof(Application.Taxonomy.HistoricalGroupKey)
						&& property.Name is "store" or "rawDescription" or "itemCode"))
				{ property.IsRequired = true; }
			}
		}

		if (typeInfo.Kind != JsonTypeInfoKind.Object
			|| typeInfo.Type.Namespace != GeneratedDtoNamespace)
		{
			return;
		}

		foreach (JsonPropertyInfo property in typeInfo.Properties)
		{
			if (typeInfo.Type == typeof(API.Generated.Dtos.ClassificationRequest)
				&& property.Name is "categoryId" or "expectedAssignmentId")
			{
				property.IsRequired = true;
			}
			// Required value types need JSON presence checks: DataAnnotations cannot
			// distinguish an omitted enum from its valid zero value (create).
			if (typeInfo.Type == typeof(API.Generated.Dtos.TaxonomyChange) && property.Name == "kind")
			{
				property.IsRequired = true;
			}
			// Nullable values carry explicit intent: omission must never revoke a mapping.
			if ((typeInfo.Type == typeof(API.Generated.Dtos.CategoryMappingChangeRequest)
					&& property.Name is "source" or "sourceFingerprint" or "expectedMappingRevisionId" or "expectedTaxonomyRevisionId" or "category" or "operationId" or "reason")
				|| (typeInfo.Type == typeof(API.Generated.Dtos.CategoryMappingSourceRequest) && property.Name == "source")
				|| (typeInfo.Type == typeof(API.Generated.Dtos.CategoryMappingHistoryRequest) && property.Name is "source" or "limit"))
			{ property.IsRequired = true; }
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
