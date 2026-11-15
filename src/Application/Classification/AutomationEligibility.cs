namespace Application.Classification;

/// <summary>
/// The one rule for which assignments an automatic writer (the approved-mapping worker or Jev) may write over.
/// No assignment is eligible too; callers check that case themselves. A manual assignment, including a manual
/// clear, and any assignment with a category are never eligible.
/// </summary>
public static class AutomationEligibility
{
	public static bool IsUntyped(string? source, Guid? categoryId, string? legacyResolution) =>
		categoryId == null && source switch
		{
			"legacy_import" or "restructure" => true,
			"legacy_write" => legacyResolution == "unresolved_path",
			_ => false
		};
}
