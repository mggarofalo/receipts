using System.Linq.Expressions;
using System.Reflection;
using Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Extensions;

public static class OwnedChildRestoreExtensions
{
	private static readonly MethodInfo RestoreChildrenOfTypeMethod =
		typeof(OwnedChildRestoreExtensions).GetMethod(nameof(RestoreChildrenOfType), BindingFlags.NonPublic | BindingFlags.Static)!;

	public static async Task RestoreOwnedChildrenAsync<TParent>(this ApplicationDbContext context, Guid parentId, CancellationToken cancellationToken)
		where TParent : class, ISoftDeletable
	{
		Type parentType = typeof(TParent);
		if (!OwnedChildrenMapProvider.Map.TryGetValue(parentType, out OwnedChildrenMapProvider.ParentEntry? parentEntry))
		{
			return;
		}

		foreach (OwnedChildrenMapProvider.OwnedChildEntry child in parentEntry.Children)
		{
			MethodInfo method = RestoreChildrenOfTypeMethod.MakeGenericMethod(child.ChildType);
			await (Task)method.Invoke(null, [context, parentId, child.FkPropertyName, cancellationToken])!;
		}
	}

	private static async Task RestoreChildrenOfType<TChild>(ApplicationDbContext context, Guid parentId, string fkPropertyName, CancellationToken cancellationToken)
		where TChild : class, ISoftDeletable
	{
		ParameterExpression param = Expression.Parameter(typeof(TChild), "e");
		MemberExpression fkAccess = Expression.Property(param, fkPropertyName);
		BinaryExpression fkEquals = Expression.Equal(fkAccess, Expression.Constant(parentId));
		Expression<Func<TChild, bool>> predicate = Expression.Lambda<Func<TChild, bool>>(fkEquals, param);

		List<TChild> items = await context.Set<TChild>()
			.IncludeDeleted()
			.Where(predicate)
			.Where(e => e.DeletedAt != null && e.CascadeDeletedByParentId == parentId)
			.ToListAsync(cancellationToken);

		foreach (TChild item in items)
		{
			item.DeletedAt = null;
			item.DeletedByUserId = null;
			item.DeletedByApiKeyId = null;
			item.CascadeDeletedByParentId = null;
		}

		// Recurse: a restored child may itself own grandchildren that were
		// cascade-soft-deleted BY it (tagged with the child's own Id, not this parentId).
		// Restoring the child must revive those too — the exact inverse of the multi-level
		// cascade in ApplicationDbContext.HandleSoftDelete, which walks the same map.
		// Only entities that appear as a parent in the owned-children map have children;
		// others (e.g. ReceiptItem, Adjustment) simply have no map entry and are skipped.
		if (items.Count > 0 && OwnedChildrenMapProvider.Map.TryGetValue(typeof(TChild), out OwnedChildrenMapProvider.ParentEntry? childAsParent))
		{
			foreach (TChild item in items)
			{
				Guid childId = (Guid)childAsParent.IdProperty.GetValue(item)!;
				await context.RestoreOwnedChildrenAsync<TChild>(childId, cancellationToken);
			}
		}
	}
}
