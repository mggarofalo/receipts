using API.Generated.Dtos;

namespace API;

public static class EnumLabels
{
	public static readonly EnumValuePair[] AdjustmentTypes =
	[
		new() { Value = "Tip", Label = "Tip" },
		new() { Value = "Discount", Label = "Discount" },
		new() { Value = "Rounding", Label = "Rounding" },
		new() { Value = "LoyaltyRedemption", Label = "Loyalty Redemption" },
		new() { Value = "Coupon", Label = "Coupon" },
		new() { Value = "StoreCredit", Label = "Store Credit" },
		new() { Value = "Other", Label = "Other" },
	];

	public static readonly EnumValuePair[] AuthEventTypes =
	[
		new() { Value = "Login", Label = "Login" },
		new() { Value = "LoginFailed", Label = "Login Failed" },
		new() { Value = "Logout", Label = "Logout" },
		new() { Value = "ApiKeyUsed", Label = "API Key Used" },
		new() { Value = "ApiKeyCreated", Label = "API Key Created" },
		new() { Value = "ApiKeyRevoked", Label = "API Key Revoked" },
		new() { Value = "PasswordChanged", Label = "Password Changed" },
		new() { Value = "UserRegistered", Label = "User Registered" },
		new() { Value = "AccountDisabled", Label = "Account Disabled" },
		new() { Value = "TokenRevoked", Label = "Token Revoked" },
		new() { Value = "RateLimitExceeded", Label = "Rate Limit Exceeded" },
	];

	public static readonly EnumValuePair[] AuditActions =
	[
		new() { Value = "Create", Label = "Created" },
		new() { Value = "Update", Label = "Updated" },
		new() { Value = "Delete", Label = "Deleted" },
		new() { Value = "Restore", Label = "Restored" },
		new() { Value = "Merge", Label = "Merged" },
		new() { Value = "Split", Label = "Split" },
	];

	public static readonly EnumValuePair[] EntityTypes =
	[
		new() { Value = "Account", Label = "Account" },
		new() { Value = "Card", Label = "Card" },
		new() { Value = "Receipt", Label = "Receipt" },
		new() { Value = "ReceiptItem", Label = "Receipt Item" },
		new() { Value = "Transaction", Label = "Transaction" },
		new() { Value = "Adjustment", Label = "Adjustment" },
		new() { Value = "ItemTemplate", Label = "Item Template" },
		// Normalized descriptions have been auto-audited all along, but were missing from this
		// list, so their entries could never be selected in the audit page's entity-type filter
		// (RECEIPTS-890).
		new() { Value = "NormalizedDescription", Label = "Normalized Description" },
	];
}
