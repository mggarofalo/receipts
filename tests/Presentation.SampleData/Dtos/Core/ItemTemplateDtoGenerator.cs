using API.Generated.Dtos;

namespace SampleData.Dtos.Core;

public static class ItemTemplateDtoGenerator
{
	public static CreateItemTemplateRequest GenerateCreateRequest()
	{
		return new CreateItemTemplateRequest
		{
			Name = "Test Item Template",
			DefaultCategory = "Test Category",
			DefaultSubcategory = "Test Subcategory",
			DefaultUnitPrice = 9.99,
			DefaultItemCode = "ITEM-001",
			Description = "Test Description"
		};
	}

	public static List<CreateItemTemplateRequest> GenerateCreateRequestList(int count)
	{
		return [.. Enumerable.Range(0, count).Select(_ => GenerateCreateRequest())];
	}

	public static UpdateItemTemplateRequest GenerateUpdateRequest()
	{
		return new UpdateItemTemplateRequest
		{
			Id = Guid.NewGuid(),
			Name = "Test Item Template",
			DefaultCategory = "Test Category",
			DefaultSubcategory = "Test Subcategory",
			DefaultUnitPrice = 9.99,
			DefaultItemCode = "ITEM-001",
			Description = "Test Description"
		};
	}

	public static List<UpdateItemTemplateRequest> GenerateUpdateRequestList(int count)
	{
		return [.. Enumerable.Range(0, count).Select(_ => GenerateUpdateRequest())];
	}
}
