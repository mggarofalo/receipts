using System.ComponentModel.DataAnnotations;
using API.Generated.Dtos;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace API.Configuration;

// OpenAPI maxLength counts Unicode scalars; generated StringLength counts UTF-16
// code units. The domain enforces the public scalar bounds for taxonomy writes.
internal sealed class TaxonomyScalarLengthMetadata : IValidationMetadataProvider
{
	public void CreateValidationMetadata(ValidationMetadataProviderContext context)
	{
		if (context.Key.ContainerType != typeof(TaxonomyChange)
			|| context.Key.Name is not (nameof(TaxonomyChange.Name) or nameof(TaxonomyChange.Definition))) { return; }
		foreach (object validator in context.ValidationMetadata.ValidatorMetadata.OfType<StringLengthAttribute>().ToArray())
		{ context.ValidationMetadata.ValidatorMetadata.Remove(validator); }
	}
}
