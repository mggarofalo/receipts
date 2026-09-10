using API.Generated.Dtos;

namespace SampleData.Dtos.Core;

public static class ReceiptItemDtoGenerator
{
	public static CreateReceiptItemRequest GenerateCreateRequest(string? receiptItemCode = "ITEM-001", string? subcategory = "Test Subcategory")
	{
		return new CreateReceiptItemRequest
		{
			ReceiptItemCode = receiptItemCode,
			Description = "Test Item",
			Quantity = 2.0,
			UnitPrice = 9.99,
			Category = "Test Category",
			Subcategory = subcategory
		};
	}

	public static List<CreateReceiptItemRequest> GenerateCreateRequestList(int count)
	{
		return [.. Enumerable.Range(0, count).Select(_ => GenerateCreateRequest())];
	}

	public static UpdateReceiptItemRequest GenerateUpdateRequest(string? receiptItemCode = "ITEM-001", string? subcategory = "Test Subcategory")
	{
		return new UpdateReceiptItemRequest
		{
			Id = Guid.NewGuid(),
			ReceiptItemCode = receiptItemCode,
			Description = "Test Item",
			Quantity = 2.0,
			UnitPrice = 9.99,
			Category = "Test Category",
			Subcategory = subcategory
		};
	}

	public static List<UpdateReceiptItemRequest> GenerateUpdateRequestList(int count)
	{
		return [.. Enumerable.Range(0, count).Select(_ => GenerateUpdateRequest())];
	}
}
