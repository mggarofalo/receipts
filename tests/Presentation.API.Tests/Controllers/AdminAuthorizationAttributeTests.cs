using System.Reflection;
using API.Controllers;
using API.Controllers.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;

namespace Presentation.API.Tests.Controllers;

/// <summary>
/// Reflection-based regression tests that lock in the admin-only authorization on
/// the audit and trash-purge endpoints (RECEIPTS-759, RECEIPTS-758, RECEIPTS-775).
/// These assert the presence (and, for the self-service endpoint, the absence) of an
/// <see cref="AuthorizeAttribute"/> carrying the <c>RequireAdmin</c> policy so a future
/// change that drops the policy fails the build instead of silently reopening the hole.
/// </summary>
public class AdminAuthorizationAttributeTests
{
	private const string RequireAdminPolicy = "RequireAdmin";

	private static bool HasRequireAdmin(IEnumerable<AuthorizeAttribute> attributes) =>
		attributes.Any(a => a.Policy == RequireAdminPolicy);

	private static MethodInfo GetAction(Type controllerType, string methodName)
	{
		MethodInfo? method = controllerType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
		method.Should().NotBeNull($"{controllerType.Name}.{methodName} should exist");
		return method!;
	}

	[Fact]
	public void PurgeTrash_RequiresAdminPolicy()
	{
		MethodInfo action = GetAction(typeof(TrashController), nameof(TrashController.PurgeTrash));

		HasRequireAdmin(action.GetCustomAttributes<AuthorizeAttribute>())
			.Should().BeTrue("POST /api/trash/purge permanently deletes all soft-deleted data and must be admin-only");
	}

	[Fact]
	public void AuditController_RequiresAdminPolicy_AtClassLevel()
	{
		HasRequireAdmin(typeof(AuditController).GetCustomAttributes<AuthorizeAttribute>())
			.Should().BeTrue("AuditController exposes cross-user change-history and must be admin-only for every action");
	}

	[Fact]
	public void AuthAuditGetRecent_RequiresAdminPolicy()
	{
		MethodInfo action = GetAction(typeof(AuthAuditController), nameof(AuthAuditController.GetRecent));

		HasRequireAdmin(action.GetCustomAttributes<AuthorizeAttribute>())
			.Should().BeTrue("GET /api/auth/audit/recent returns every user's sign-in events and must be admin-only");
	}

	[Fact]
	public void AuthAuditGetFailed_RequiresAdminPolicy()
	{
		MethodInfo action = GetAction(typeof(AuthAuditController), nameof(AuthAuditController.GetFailed));

		HasRequireAdmin(action.GetCustomAttributes<AuthorizeAttribute>())
			.Should().BeTrue("GET /api/auth/audit/failed returns every user's failed-login attempts and must be admin-only");
	}

	[Fact]
	public void AuthAuditGetMyAuditLog_DoesNotRequireAdminPolicy()
	{
		MethodInfo action = GetAction(typeof(AuthAuditController), nameof(AuthAuditController.GetMyAuditLog));

		HasRequireAdmin(action.GetCustomAttributes<AuthorizeAttribute>())
			.Should().BeFalse("GET /api/auth/audit/me self-filters to the caller's own userId and must stay reachable by any authenticated user");
	}
}
