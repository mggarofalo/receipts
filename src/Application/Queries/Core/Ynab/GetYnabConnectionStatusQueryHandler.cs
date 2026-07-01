using Application.Interfaces.Services;
using Application.Models.Ynab;
using Common;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Application.Queries.Core.Ynab;

public class GetYnabConnectionStatusQueryHandler(
	IYnabApiClient ynabApiClient,
	IYnabSyncRecordService syncRecordService,
	IYnabSyncEventService ynabSyncEventService,
	IYnabResponseContext ynabResponseContext,
	ILogger<GetYnabConnectionStatusQueryHandler> logger) : IRequestHandler<GetYnabConnectionStatusQuery, YnabConnectionStatus>
{
	public async ValueTask<YnabConnectionStatus> Handle(GetYnabConnectionStatusQuery request, CancellationToken cancellationToken)
	{
		bool isConfigured = ynabApiClient.IsConfigured;

		if (!isConfigured)
		{
			return new YnabConnectionStatus(false, false, null);
		}

		bool isConnected;
		string? validateError = null;
		try
		{
			await ynabApiClient.GetBudgetsAsync(cancellationToken);
			isConnected = true;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			isConnected = false;
			validateError = ex.Message;
		}

		// RECEIPTS-737: record this live validation so /ynab can show "last validated at".
		// Best-effort — never fail the status check because event logging failed.
		try
		{
			await ynabSyncEventService.WriteAsync(
				YnabSyncEventType.Validate,
				isConnected,
				httpStatus: ynabResponseContext.LastStatusCode,
				errorMessage: validateError,
				requestId: ynabResponseContext.LastRequestId,
				cancellationToken: cancellationToken);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Failed to write YNAB validate event");
		}

		DateTimeOffset? lastSync = await syncRecordService.GetLatestSuccessfulSyncTimestampAsync(cancellationToken);

		return new YnabConnectionStatus(isConfigured, isConnected, lastSync);
	}
}
