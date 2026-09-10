namespace API.Hubs;

public record EntityChangeNotification(
	string EntityType,
	string ChangeType,
	Guid? Id,
	int Count = 1,
	string? UserId = null,
	string? AuthMethod = null,
	string? ConnectionId = null,
	bool SuppressToast = false);
