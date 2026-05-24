using Application.Interfaces.Services;
using Application.Models.Ynab;
using Common;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;

namespace Infrastructure.Services;

public class YnabSyncEventService(IYnabSyncEventRepository repository) : IYnabSyncEventService
{
	public async Task RecordAsync(
		YnabSyncType eventType,
		YnabSyncStatus outcome,
		Guid? localTransactionId,
		Guid? receiptId,
		string? ynabBudgetId,
		string? ynabTransactionId,
		string? errorMessage,
		CancellationToken cancellationToken)
	{
		YnabSyncEventEntity entity = new()
		{
			Id = Guid.NewGuid(),
			OccurredAt = DateTimeOffset.UtcNow,
			EventType = eventType,
			Outcome = outcome,
			LocalTransactionId = localTransactionId,
			ReceiptId = receiptId,
			YnabBudgetId = ynabBudgetId,
			YnabTransactionId = ynabTransactionId,
			ErrorMessage = errorMessage,
		};

		await repository.CreateAsync(entity, cancellationToken);
	}

	public async Task<YnabSyncEventsPage> ListAsync(int offset, int limit, YnabSyncStatus? outcome, CancellationToken cancellationToken)
	{
		(IReadOnlyList<YnabSyncEventEntity> events, int total) = await repository.ListAsync(offset, limit, outcome, cancellationToken);
		List<YnabSyncEventDto> dtos = events.Select(ToDto).ToList();
		return new YnabSyncEventsPage(dtos, total);
	}

	public Task<DateTimeOffset?> GetLatestOccurrenceAsync(YnabSyncStatus outcome, CancellationToken cancellationToken)
		=> repository.GetLatestOccurrenceAsync(outcome, cancellationToken);

	public Task<int> CountSinceAsync(DateTimeOffset since, YnabSyncStatus? outcome, CancellationToken cancellationToken)
		=> repository.CountSinceAsync(since, outcome, cancellationToken);

	private static YnabSyncEventDto ToDto(YnabSyncEventEntity entity) => new(
		entity.Id,
		entity.OccurredAt,
		entity.EventType,
		entity.Outcome,
		entity.LocalTransactionId,
		entity.ReceiptId,
		entity.YnabBudgetId,
		entity.YnabTransactionId,
		entity.ErrorMessage);
}
