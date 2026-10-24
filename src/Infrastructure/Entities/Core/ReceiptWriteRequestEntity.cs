namespace Infrastructure.Entities.Core;

public sealed class ReceiptWriteRequestEntity
{
	public string Actor { get; set; } = "";
	public Guid RequestId { get; set; }
	public string RequestHash { get; set; } = "";
	public string Kind { get; set; } = "";
	public Guid ReceiptId { get; set; }
	public Guid[] ItemIds { get; set; } = [];
}
