using System.Text.Json.Serialization;

namespace Infrastructure.Ynab.Models;

/// <summary>
/// Request body for POST /v1/budgets/{budget_id}/transactions.
/// </summary>
internal sealed class YnabSaveTransactionWrapper
{
	[JsonPropertyName("transaction")]
	public YnabSaveTransactionDto Transaction { get; set; } = null!;
}

internal sealed class YnabSaveTransactionDto
{
	[JsonPropertyName("account_id")]
	public string AccountId { get; set; } = string.Empty;

	[JsonPropertyName("date")]
	public string Date { get; set; } = string.Empty;

	[JsonPropertyName("amount")]
	public long Amount { get; set; }

	[JsonPropertyName("memo")]
	public string? Memo { get; set; }

	[JsonPropertyName("payee_name")]
	public string? PayeeName { get; set; }

	[JsonPropertyName("category_id")]
	public string? CategoryId { get; set; }

	[JsonPropertyName("approved")]
	public bool Approved { get; set; }

	[JsonPropertyName("import_id")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? ImportId { get; set; }

	[JsonPropertyName("subtransactions")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public List<YnabSaveSubTransactionDto>? SubTransactions { get; set; }
}

internal sealed class YnabSaveSubTransactionDto
{
	[JsonPropertyName("amount")]
	public long Amount { get; set; }

	[JsonPropertyName("category_id")]
	public string CategoryId { get; set; } = string.Empty;

	[JsonPropertyName("memo")]
	public string? Memo { get; set; }
}

/// <summary>
/// Response envelope for POST /v1/budgets/{budget_id}/transactions.
/// </summary>
internal sealed class YnabCreateTransactionResponseEnvelope
{
	[JsonPropertyName("data")]
	public YnabCreateTransactionResponseData Data { get; set; } = null!;
}

internal sealed class YnabCreateTransactionResponseData
{
	[JsonPropertyName("transaction_ids")]
	public List<string> TransactionIds { get; set; } = [];

	[JsonPropertyName("transaction")]
	public YnabTransactionDto? Transaction { get; set; }
}
