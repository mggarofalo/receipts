using API.Generated.Dtos;

namespace SampleData.Dtos.Core;

public static class SubcategoryDtoGenerator
{
	public static CreateSubcategoryRequest GenerateCreateRequest()
	{
		return new CreateSubcategoryRequest
		{
			Name = "Test Subcategory",
			CategoryId = Guid.NewGuid(),
			Description = "Test Description"
		};
	}

	public static List<CreateSubcategoryRequest> GenerateCreateRequestList(int count)
	{
		return [.. Enumerable.Range(0, count).Select(_ => GenerateCreateRequest())];
	}

	public static UpdateSubcategoryRequest GenerateUpdateRequest()
	{
		return new UpdateSubcategoryRequest
		{
			Id = Guid.NewGuid(),
			Name = "Test Subcategory",
			CategoryId = Guid.NewGuid(),
			Description = "Test Description"
		};
	}

	public static List<UpdateSubcategoryRequest> GenerateUpdateRequestList(int count)
	{
		return [.. Enumerable.Range(0, count).Select(_ => GenerateUpdateRequest())];
	}
}
