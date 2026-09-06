using System.Text.Json;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Utilities;

internal static class TemplateWriteGuard
{
	private sealed class RowRevision
	{
		public Guid Id { get; set; }
		public string Revision { get; set; } = string.Empty;
	}

	internal static async Task<Dictionary<Guid, string>> ReadRevisionsAsync(
		ApplicationDbContext context, Guid[] ids, CancellationToken cancellationToken)
	{
		if (ids.Length == 0)
		{
			return [];
		}
		if (context.Database.IsNpgsql())
		{
			return await context.Database.SqlQuery<RowRevision>($"""
				SELECT "Id", xmin::text AS "Revision" FROM library."ItemTemplates"
				WHERE "Id" = ANY ({ids}) AND "DeletedAt" IS NULL
				""").ToDictionaryAsync(row => row.Id, row => row.Revision, cancellationToken);
		}

		// Unit fixtures compare values. Only PostgreSQL supplies revision/ABA guarantees.
		List<ItemTemplateEntity> templates = await context.ItemTemplates.AsNoTracking()
			.Where(template => ids.Contains(template.Id)).ToListAsync(cancellationToken);
		return templates.ToDictionary(template => template.Id, template => JsonSerializer.Serialize(new
		{
			template.Name,
			template.DefaultCategory,
			template.DefaultSubcategory,
			template.DefaultUnitPrice,
			template.DefaultUnitPriceCurrency,
			template.DefaultItemCode,
			template.Description,
			template.NormalizedDescriptionId,
			template.DeletedAt,
			template.DeletedByUserId,
			template.DeletedByApiKeyId,
			template.CascadeDeletedByParentId,
		}));
	}

	internal static async Task LockTemplatesAsync(ApplicationDbContext context, Guid[] ids, CancellationToken cancellationToken)
	{
		if (!context.Database.IsNpgsql() || ids.Length == 0)
		{
			return;
		}
		if (context.Database.CurrentTransaction is null)
		{
			throw new InvalidOperationException("Template write locks require an active transaction.");
		}
		await context.Database.SqlQuery<Guid>($"""
			SELECT "Id" AS "Value" FROM library."ItemTemplates"
			WHERE "Id" = ANY ({ids}) ORDER BY "Id" FOR UPDATE
			""").ToListAsync(cancellationToken);
	}
}
