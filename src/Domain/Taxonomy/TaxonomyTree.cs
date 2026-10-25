namespace Domain.Taxonomy;

public sealed record TaxonomyNode(Guid Id, Guid? ParentId, string Name, string? Definition, bool Archived);
public sealed record TaxonomyEdit(string Kind, Guid CategoryId, Guid? ParentId = null, string? Name = null, string? Definition = null);
public sealed class TaxonomyValidationException(string message) : Exception(message);

public static class TaxonomyTree
{
	public const int MaximumNameLength = 200;
	public const int MaximumDefinitionLength = 4000;
	public static IReadOnlyList<TaxonomyNode> Apply(IReadOnlyList<TaxonomyNode> original, IReadOnlyList<TaxonomyEdit> changes)
	{
		Dictionary<Guid, TaxonomyNode> nodes = original.ToDictionary(node => node.Id);
		foreach (TaxonomyEdit edit in changes)
		{
			if (edit.CategoryId == Guid.Empty) { throw new TaxonomyValidationException("Category ID must not be empty."); }
			if ((edit.Kind != "create" && edit.Kind != "move" && edit.ParentId != null)
				|| (edit.Kind != "create" && edit.Kind != "rename" && edit.Name != null)
				|| (edit.Kind != "create" && edit.Kind != "define" && edit.Definition != null))
			{ throw new TaxonomyValidationException("Change contains fields that do not belong to its kind."); }
			if (edit.Kind == "create")
			{
				if (!nodes.TryAdd(edit.CategoryId, new(edit.CategoryId, edit.ParentId, edit.Name!, edit.Definition, false)))
				{ throw new TaxonomyValidationException("Category identity already exists."); }
				continue;
			}
			if (!nodes.TryGetValue(edit.CategoryId, out TaxonomyNode? node)) { throw new TaxonomyValidationException("Category does not exist."); }
			switch (edit.Kind)
			{
				case "rename": nodes[node.Id] = node with { Name = edit.Name! }; break;
				case "define": nodes[node.Id] = node with { Definition = edit.Definition }; break;
				case "move": nodes[node.Id] = node with { ParentId = edit.ParentId }; break;
				case "restore": nodes[node.Id] = node with { Archived = false }; break;
				case "archive":
					HashSet<Guid> subtree = [node.Id];
					Queue<Guid> pending = new(); pending.Enqueue(node.Id);
					while (pending.TryDequeue(out Guid parent))
					{
						foreach (TaxonomyNode child in nodes.Values.Where(candidate => candidate.ParentId == parent))
						{ if (subtree.Add(child.Id)) { pending.Enqueue(child.Id); } }
					}
					foreach (Guid id in subtree) { nodes[id] = nodes[id] with { Archived = true }; }
					break;
				default: throw new TaxonomyValidationException("Unknown taxonomy change kind.");
			}
		}
		HashSet<(Guid?, string)> siblingNames = [];
		foreach (TaxonomyNode node in nodes.Values)
		{
			if (node.Name?.Contains('\0') == true || node.Definition?.Contains('\0') == true)
			{ throw new TaxonomyValidationException("Category names and definitions must not contain NUL characters."); }
			if (node.Name != null && node.Name.EnumerateRunes().Count() > MaximumNameLength)
			{ throw new TaxonomyValidationException("Names must contain at most 200 Unicode scalar values."); }
			if (node.Definition != null && node.Definition.EnumerateRunes().Count() > MaximumDefinitionLength)
			{ throw new TaxonomyValidationException("Definitions must contain at most 4000 Unicode scalar values."); }
			if (string.IsNullOrWhiteSpace(node.Name) || node.Name != node.Name.Trim())
			{ throw new TaxonomyValidationException("Names must be nonempty and have no surrounding whitespace."); }
			if (!node.Archived && !siblingNames.Add((node.ParentId, node.Name)))
			{ throw new TaxonomyValidationException("Active sibling names must be unique (exact ordinal equality)."); }
			HashSet<Guid> path = [node.Id];
			TaxonomyNode current = node;
			while (current.ParentId is Guid parent)
			{
				if (!nodes.TryGetValue(parent, out current!)) { throw new TaxonomyValidationException("Parent category does not exist."); }
				if (!path.Add(parent)) { throw new TaxonomyValidationException("Category hierarchy cannot contain a cycle."); }
				if (!node.Archived && current.Archived) { throw new TaxonomyValidationException("Active categories require active ancestors."); }
			}
		}
		return nodes.Values.OrderBy(node => node.Id).ToArray();
	}
}
