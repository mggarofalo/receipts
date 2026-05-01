using System.Text.Json;

namespace Infrastructure.Services;

/// <summary>
/// JSON Schema for the <c>submit_receipt</c> tool used by
/// <see cref="AnthropicReceiptExtractionService"/>. Hand-authored to mirror
/// <see cref="VlmReceiptPayload"/> and reused at runtime as the tool's
/// <c>input_schema</c>. The Anthropic API uses this schema to constrain the
/// model's tool-call arguments, so any future field added to
/// <see cref="VlmReceiptPayload"/> must also be added here — there is no
/// codegen yet (the JSON Schema vocabulary needed is a tiny subset, not worth
/// pulling in NJsonSchema for one tool definition).
/// <para>
/// Cached as a parsed <see cref="JsonElement"/> so the request builder can
/// stamp it directly into <see cref="AnthropicTool.InputSchema"/> without
/// reparsing on every request.
/// </para>
/// </summary>
internal static class VlmReceiptToolSchema
{
	internal const string SchemaJson = """
		{
			"type": "object",
			"properties": {
				"schema_version": {
					"type": "integer",
					"description": "Schema version. MUST be 1 for the current contract."
				},
				"store": {
					"type": "object",
					"description": "Merchant info. Omit fields that are not visible on the receipt.",
					"properties": {
						"name": { "type": ["string", "null"], "description": "Merchant name as printed." },
						"address": { "type": ["string", "null"], "description": "Merchant address." },
						"phone": { "type": ["string", "null"], "description": "Merchant phone number." }
					}
				},
				"datetime": {
					"type": ["string", "null"],
					"description": "Purchase datetime. ISO-8601 preferred but receipt-style strings (MM/DD/YY HH:MM) are accepted."
				},
				"items": {
					"type": "array",
					"description": "Line items as printed on the receipt. Empty array if none are visible.",
					"items": {
						"type": "object",
						"properties": {
							"description": { "type": ["string", "null"], "description": "Item description." },
							"code": { "type": ["string", "null"], "description": "UPC / SKU / PLU printed on the line. null if not present." },
							"lineTotal": { "type": ["number", "null"], "description": "Final amount for this line." },
							"quantity": { "type": ["number", "null"], "description": "Numeric quantity if printed (weight or N @ price). null otherwise." },
							"unitPrice": { "type": ["number", "null"], "description": "Unit price if printed. null otherwise." },
							"taxCode": { "type": ["string", "null"], "description": "Tax code printed next to the line, if any." }
						}
					}
				},
				"subtotal": { "type": ["number", "null"], "description": "Pre-tax total." },
				"taxLines": {
					"type": "array",
					"description": "Tax lines. Empty array if none.",
					"items": {
						"type": "object",
						"properties": {
							"label": { "type": ["string", "null"], "description": "Tax label, e.g. 'TAX1 6.0000%'." },
							"amount": { "type": ["number", "null"], "description": "Tax amount." }
						}
					}
				},
				"total": { "type": ["number", "null"], "description": "Grand total." },
				"payments": {
					"type": "array",
					"description": "Tenders. Empty array if none visible.",
					"items": {
						"type": "object",
						"properties": {
							"method": { "type": ["string", "null"], "description": "Tender method, e.g. 'MASTERCARD', 'CASH'." },
							"amount": { "type": ["number", "null"], "description": "Amount paid via this tender." },
							"lastFour": { "type": ["string", "null"], "description": "Exactly four digits adjacent to the tender method, or null. NEVER substitute APPR# / REF# / auth-code digits." }
						}
					}
				},
				"receiptId": { "type": ["string", "null"], "description": "Receipt identifier as printed." },
				"storeNumber": { "type": ["string", "null"], "description": "Store number as printed." },
				"terminalId": { "type": ["string", "null"], "description": "Terminal identifier as printed." }
			},
			"required": ["schema_version"]
		}
		""";

	internal static JsonElement SchemaElement { get; } = JsonDocument.Parse(SchemaJson).RootElement.Clone();
}
