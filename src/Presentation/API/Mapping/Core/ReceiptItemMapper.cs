using API.Generated.Dtos;
using Common;
using Domain;
using Domain.Core;
using Riok.Mapperly.Abstractions;

namespace API.Mapping.Core;

[Mapper]
public partial class ReceiptItemMapper
{
	[MapProperty(nameof(ReceiptItem.UnitPrice.Amount), nameof(ReceiptItemResponse.UnitPrice))]
	[MapperIgnoreSource(nameof(ReceiptItem.TotalAmount))]
	[MapperIgnoreSource(nameof(ReceiptItem.Classification))]
	[MapperIgnoreSource(nameof(ReceiptItem.NormalizationIntent))]
	[MapperIgnoreTarget(nameof(ReceiptItemResponse.AdditionalProperties))]
	public partial ReceiptItemResponse ToResponse(ReceiptItem source);

	public ReceiptItem ToDomain(CreateReceiptItemRequest source)
	{
		decimal quantity = (decimal)source.Quantity;
		decimal unitPrice = (decimal)source.UnitPrice;
		Money unitPriceMoney = new(unitPrice, Currency.USD);
		Money totalAmount = new(Math.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero), Currency.USD);

		return new ReceiptItem(
			Guid.Empty,
			source.ReceiptItemCode,
			source.Description,
			quantity,
			unitPriceMoney,
			totalAmount,
			source.Category,
			source.Subcategory
		)
		{ NormalizationIntent = MapIntent(source.NormalizationIntent), Classification = MapClassification(source.Classification) };
	}

	public ReceiptItem ToDomain(UpdateReceiptItemRequest source)
	{
		decimal quantity = (decimal)source.Quantity;
		decimal unitPrice = (decimal)source.UnitPrice;
		Money unitPriceMoney = new(unitPrice, Currency.USD);
		Money totalAmount = new(Math.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero), Currency.USD);

		return new ReceiptItem(
			source.Id,
			source.ReceiptItemCode,
			source.Description,
			quantity,
			unitPriceMoney,
			totalAmount,
			source.Category,
			source.Subcategory
		)
		{ NormalizationIntent = MapIntent(source.NormalizationIntent), Classification = MapClassification(source.Classification) };
	}

	private static Domain.Taxonomy.ClassificationIntent? MapClassification(ClassificationRequest? value)
		=> value is null ? null : new(value.CategoryId, value.TaxonomyRevisionId, value.ExpectedAssignmentId, value.OperationId, value.Reason);

	private static Domain.NormalizedDescriptions.ReceiptNormalizationIntent? MapIntent(API.Generated.Dtos.ReceiptNormalizationIntent? value)
		=> value is null ? null : new((Domain.NormalizedDescriptions.ReceiptNormalizationIntentKind)value.Kind,
			value.ClientRowId, value.SourceFingerprint, value.TargetId, value.ExpectedTargetRevision, value.ExpectedMappingRevision,
			value.ExpectedItemRevision, value.NewName, value.RememberSource, value.Reason);

	private static CurrentClassificationResponse MapCurrentClassification(Domain.Taxonomy.CurrentClassification source) => new()
	{
		AssignmentId = source.AssignmentId,
		CategoryId = source.CategoryId,
		TaxonomyRevisionId = source.TaxonomyRevisionId,
		RecordedPath = source.RecordedPath.Select(node => new TaxonomyNodeResponse
		{
			Id = node.Id,
			ParentId = node.ParentId,
			Name = node.Name,
			Definition = node.Definition,
			Archived = node.Archived
		}).ToList()
	};

	private double MapDecimalToDouble(decimal value) => (double)value;
}
