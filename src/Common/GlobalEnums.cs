namespace Common;

public enum Currency
{
	USD
}

public enum AdjustmentType
{
	Tip,
	Discount,
	Rounding,
	LoyaltyRedemption,
	Coupon,
	StoreCredit,
	Other
}

public enum AuthEventType
{
	Login,
	LoginFailed,
	Logout,
	ApiKeyUsed,
	ApiKeyCreated,
	ApiKeyRevoked,
	PasswordChanged,
	UserRegistered,
	AccountDisabled,
	TokenRevoked,
	RateLimitExceeded,
}

public enum YnabSyncType
{
	MemoUpdate,
	TransactionPush
}

public enum YnabSyncStatus
{
	Pending,
	Synced,
	Failed
}

public enum YnabSyncEventType
{
	Push,
	Validate
}
