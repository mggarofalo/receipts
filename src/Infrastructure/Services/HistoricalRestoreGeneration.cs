using Application.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

// Caller captures this once when work is requested, never when a worker resumes it.
internal static class HistoricalRestoreGeneration
{
	internal static async Task RequireCurrentAsync(ApplicationDbContext db, Guid expected, CancellationToken ct)
	{
		Guid current = await db.CategoryAssistanceSettings.AsNoTracking()
				.Select(x => x.RestoreGeneration).SingleAsync(ct);
		if (expected == Guid.Empty || expected != current)
		{ throw new TaxonomyException(409, "Automatic historical request predates restore."); }
	}
}
