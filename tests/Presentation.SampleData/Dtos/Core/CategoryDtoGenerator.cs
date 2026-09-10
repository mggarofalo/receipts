using API.Generated.Dtos;

namespace SampleData.Dtos.Core;

public static class CategoryDtoGenerator
{
	public static CreateCategoryRequest GenerateCreateRequest()
	{
		return new CreateCategoryRequest
		{
			Name = "Test Category",
			Description = "Test Description"
		};
	}

	public static List<CreateCategoryRequest> GenerateCreateRequestList(int count)
	{
		return [.. Enumerable.Range(0, count).Select(_ => GenerateCreateRequest())];
	}

	public static UpdateCategoryRequest GenerateUpdateRequest()
	{
		return new UpdateCategoryRequest
		{
			Id = Guid.NewGuid(),
			Name = "Test Category",
			Description = "Test Description"
		};
	}

	public static List<UpdateCategoryRequest> GenerateUpdateRequestList(int count)
	{
		return [.. Enumerable.Range(0, count).Select(_ => GenerateUpdateRequest())];
	}
}
