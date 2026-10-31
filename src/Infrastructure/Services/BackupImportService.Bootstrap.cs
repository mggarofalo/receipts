using System.Reflection;
using Infrastructure.Entities.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure.Services;

public partial class BackupImportService
{
	// A database-wide proof; never permission to remove selected seed IDs from
	// an established installation. New business tables automatically fail closed.
	internal static async Task<bool> HasPristineBootstrapAsync(ApplicationDbContext db, CancellationToken ct)
	{
		var model = db.GetService<IDesignTimeModel>().Model;
		foreach (var entity in BootstrapBusinessTypes(model))
		{
			var check = typeof(BackupImportService).GetMethod(nameof(MatchesBootstrapAsync), BindingFlags.NonPublic | BindingFlags.Static)!
				.MakeGenericMethod(entity.ClrType);
			if (!await (Task<bool>)check.Invoke(null, [db, entity, ct])!) { return false; }
		}
		return true;
	}

	private static IEnumerable<IEntityType> BootstrapBusinessTypes(IModel model) => model.GetEntityTypes()
		.Where(e => e.GetSchema() != "identity" && e.ClrType != typeof(AuthAuditLogEntity))
		.OrderBy(e => e.GetSchema()).ThenBy(e => e.GetTableName());

	private static async Task<bool> MatchesBootstrapAsync<TEntity>(ApplicationDbContext db, IEntityType entity, CancellationToken ct)
		where TEntity : class
	{
		var seeds = entity.GetSeedData().ToList();
		var query = db.Set<TEntity>().IgnoreQueryFilters().IgnoreAutoIncludes();
		if (await query.CountAsync(ct) != seeds.Count) { return false; }
		foreach (var row in await query.ToListAsync(ct))
		{
			var entry = db.Entry(row);
			bool matches = seeds.Any(seed => entity.GetProperties().All(property =>
			{
				// Only store-generated concurrency state (xmin) is database-local.
				if (property.IsConcurrencyToken && property.ValueGenerated == ValueGenerated.OnAddOrUpdate) { return true; }
				object? expected = seed.TryGetValue(property.Name, out var value) ? value
					: property.ClrType.IsValueType ? Activator.CreateInstance(property.ClrType) : null;
				return Equals(entry.Property(property.Name).CurrentValue, expected);
			}));
			entry.State = EntityState.Detached;
			if (!matches) { return false; }
		}
		return true;
	}

	private static async Task RemovePristineBootstrapAsync(ApplicationDbContext db, CancellationToken ct)
	{
		if (!await HasPristineBootstrapAsync(db, ct)) { return; }
		var sql = db.GetService<ISqlGenerationHelper>();
		var tables = BootstrapBusinessTypes(db.Model).Select(e => sql.DelimitIdentifier(e.GetTableName()!, e.GetSchema())).Distinct();
		// Prevent business writes between proof and removal, until import commit.
		// Identifiers come only from EF metadata and are provider-quoted above.
#pragma warning disable EF1003
		await db.Database.ExecuteSqlRawAsync("LOCK TABLE " + string.Join(", ", tables) + " IN SHARE ROW EXCLUSIVE MODE", ct);
#pragma warning restore EF1003
		if (!await HasPristineBootstrapAsync(db, ct)) { return; }
		// Bypass soft-delete: missing bootstrap rows must not become portable trash.
		await db.ItemTemplates.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
		await db.Subcategories.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
		await db.Categories.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
	}
}
