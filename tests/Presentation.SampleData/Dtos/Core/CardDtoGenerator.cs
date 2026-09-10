using API.Generated.Dtos;

namespace SampleData.Dtos.Core;

public static class CardDtoGenerator
{
	public static CreateCardRequest GenerateCreateRequest()
	{
		return new CreateCardRequest
		{
			CardCode = "Test Card",
			Name = "Test Description",
			IsActive = true,
			AccountId = Guid.NewGuid()
		};
	}

	public static List<CreateCardRequest> GenerateCreateRequestList(int count)
	{
		return [.. Enumerable.Range(0, count).Select(_ => GenerateCreateRequest())];
	}

	public static UpdateCardRequest GenerateUpdateRequest()
	{
		return new UpdateCardRequest
		{
			Id = Guid.NewGuid(),
			CardCode = "Test Card",
			Name = "Test Description",
			IsActive = true,
			AccountId = Guid.NewGuid()
		};
	}

	public static List<UpdateCardRequest> GenerateUpdateRequestList(int count)
	{
		return [.. Enumerable.Range(0, count).Select(_ => GenerateUpdateRequest())];
	}
}
