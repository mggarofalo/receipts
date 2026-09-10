using API.Generated.Dtos;

namespace SampleData.Dtos.Core;

public static class ReceiptDtoGenerator
{
	public static CreateReceiptRequest GenerateCreateRequest()
	{
		return new CreateReceiptRequest
		{
			Location = "Test Location",
			Date = DateOnly.FromDateTime(DateTime.Today),
			TaxAmount = 8.50
		};
	}

	public static List<CreateReceiptRequest> GenerateCreateRequestList(int count)
	{
		return [.. Enumerable.Range(0, count).Select(_ => GenerateCreateRequest())];
	}

	public static UpdateReceiptRequest GenerateUpdateRequest()
	{
		return new UpdateReceiptRequest
		{
			Id = Guid.NewGuid(),
			Location = "Test Location",
			Date = DateOnly.FromDateTime(DateTime.Today),
			TaxAmount = 8.50
		};
	}

	public static List<UpdateReceiptRequest> GenerateUpdateRequestList(int count)
	{
		return [.. Enumerable.Range(0, count).Select(_ => GenerateUpdateRequest())];
	}
}
