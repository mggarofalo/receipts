using Application.Models.Reports;
namespace Application.Interfaces.Services;

public interface IDuplicateReportReader
{
	Task<DuplicateDetectionResult> GetDuplicatesAsync(string matchOn, string locationTolerance, decimal totalTolerance, bool includeAccepted, CancellationToken cancellationToken);
	Task<AcceptedDuplicatesResult> GetAcceptedDuplicatesAsync(CancellationToken cancellationToken);
}
