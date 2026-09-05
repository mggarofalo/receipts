using API.Generated.Dtos;

namespace SampleData.Dtos.Core;

public static class TransactionDtoGenerator
{
	public static CreateTransactionRequest GenerateCreateRequest()
	{
		return new CreateTransactionRequest
		{
			Amount = 100.00,
			Date = DateOnly.FromDateTime(DateTime.Today),
			CardId = Guid.NewGuid()
		};
	}

	public static List<CreateTransactionRequest> GenerateCreateRequestList(int count)
	{
		return [.. Enumerable.Range(0, count).Select(_ => GenerateCreateRequest())];
	}

	public static UpdateTransactionRequest GenerateUpdateRequest()
	{
		return new UpdateTransactionRequest
		{
			Id = Guid.NewGuid(),
			Amount = 100.00,
			Date = DateOnly.FromDateTime(DateTime.Today),
			CardId = Guid.NewGuid()
		};
	}

	public static List<UpdateTransactionRequest> GenerateUpdateRequestList(int count)
	{
		return [.. Enumerable.Range(0, count).Select(_ => GenerateUpdateRequest())];
	}
}
