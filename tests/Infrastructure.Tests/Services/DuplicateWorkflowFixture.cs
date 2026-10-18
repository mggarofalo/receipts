using Application.Commands.Reports;
using Application.Models.Reports;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Services;

internal sealed class DuplicateWorkflowFixture(IDbContextFactory<ApplicationDbContext> factory)
{
	private readonly DuplicateReportReader reader = new(factory);
	private readonly DuplicateAcceptanceStore store = new(factory);
	public Task<DuplicateDetectionResult> GetDuplicatesAsync(string matchOn, string locationTolerance, decimal totalTolerance, bool includeAccepted, CancellationToken token)
		=> reader.GetDuplicatesAsync(matchOn, locationTolerance, totalTolerance, includeAccepted, token);
	public Task<AcceptedDuplicatesResult> GetAcceptedDuplicatesAsync(CancellationToken token) => reader.GetAcceptedDuplicatesAsync(token);
	public Task<int> AcceptDuplicateGroupAsync(List<Guid> ids, CancellationToken token)
		=> new AcceptDuplicateGroupCommandHandler(store, TimeProvider.System).Handle(new(ids), token).AsTask();
	public Task<int> UnacceptDuplicateGroupAsync(List<Guid> ids, CancellationToken token)
		=> new UnacceptDuplicateGroupCommandHandler(store).Handle(new(ids), token).AsTask();
}
