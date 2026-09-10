namespace API.Services;

public interface IEntityChangeNotifier
{
	Task NotifyCreated(string entityType, Guid id);
	Task NotifyUpdated(string entityType, Guid id);
	Task NotifyDeleted(string entityType, Guid id);
	Task NotifyBulkChanged(string entityType, string changeType, IEnumerable<Guid> ids);
	Task NotifyAllChanged(string entityType, string changeType);
	Task NotifyAllChanged(string entityType, string changeType, bool suppressToast);
}
