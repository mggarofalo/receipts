using API.Generated.Dtos;

namespace SampleData.Dtos.Core;

public static class AccountDtoGenerator
{
	public static CreateAccountRequest GenerateCreateRequest()
	{
		return new CreateAccountRequest
		{
			Name = "Test Account",
			IsActive = true
		};
	}

	public static List<CreateAccountRequest> GenerateCreateRequestList(int count)
	{
		return [.. Enumerable.Range(0, count).Select(_ => GenerateCreateRequest())];
	}

	public static UpdateAccountRequest GenerateUpdateRequest()
	{
		return new UpdateAccountRequest
		{
			Id = Guid.NewGuid(),
			Name = "Test Account",
			IsActive = true
		};
	}

	public static List<UpdateAccountRequest> GenerateUpdateRequestList(int count)
	{
		return [.. Enumerable.Range(0, count).Select(_ => GenerateUpdateRequest())];
	}
}
