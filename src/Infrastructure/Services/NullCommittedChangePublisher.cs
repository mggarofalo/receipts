using Application.Interfaces.Services;
using Application.Models.CommittedChanges;

namespace Infrastructure.Services;

internal sealed class NullCommittedChangePublisher : ICommittedChangePublisher
{
	public Task PublishAsync(CommittedEntityChange change)
	{
		_ = change;
		return Task.CompletedTask;
	}
}
