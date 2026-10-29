using System.Text.Json;
using Application.Normalization;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

// Review choices protect shared purchases across actors, but only for their exact source.
// null means the whole source is protected (including malformed saved evidence).
internal static class HistoricalApprovedMappingReviewProtection
{
	internal static async Task<Guid[]?> ExcludedItemsAsync(ApplicationDbContext db, NormalizationSourceKey source, CancellationToken ct)
	{
		var preferences = await db.CategoryAssistanceReviewPreferences.FromSqlInterpolated($"""
            SELECT * FROM category_assistance."ReviewPreferences"
            WHERE md5("Location" || chr(31) || "Description" || chr(31) || "ItemCodeKey") =
                md5({source.Location} || chr(31) || {source.Description} || chr(31) || {source.ItemCode ?? ""})
              AND "Location" = {source.Location} AND "Description" = {source.Description}
              AND "ItemCodeKey" = {source.ItemCode ?? ""}
            """).AsNoTracking().ToListAsync(ct);
		HashSet<Guid> excluded = [];
		foreach (var preference in preferences)
		{
			if (string.IsNullOrWhiteSpace(preference.Actor) || preference.AttemptId == Guid.Empty ||
				preference.Revision == Guid.Empty || preference.State != "active") { return null; }
			try
			{
				var ids = JsonSerializer.Deserialize<Guid[]>(preference.ExcludedItemIdsJson);
				if (ids == null || ids.Length > 1000 || ids.Any(x => x == Guid.Empty) ||
					ids.Distinct().Count() != ids.Length) { return null; }
				excluded.UnionWith(ids);
			}
			catch (JsonException) { return null; }
			catch (ArgumentNullException) { return null; }
		}
		return excluded.ToArray();
	}
}
