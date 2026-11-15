using System.Reflection;
using System.Security.Claims;
using API.Controllers;
using API.Http;
using Application.Taxonomy;
using Common;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Presentation.API.Tests.Controllers;

/// <summary>
/// Cleanup and Jev historical requests are Admin-only (RECEIPTS-1122). Automatic
/// activity and its undo stay open to the owner, so undo decides by operation kind.
/// </summary>
public class HistoricalCleanupAuthorizationTests
{
	private const string RequireAdmin = "RequireAdmin";

	private static bool RequiresAdmin(MemberInfo member) =>
		member.GetCustomAttributes<AuthorizeAttribute>().Any(a => a.Policy == RequireAdmin);

	private static MethodInfo Action(string name) =>
		typeof(HistoricalReclassificationsController).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)!;

	[Fact]
	public void HistoricalRequestsRequireAdminForEveryAction() =>
		RequiresAdmin(typeof(CategoryAssistanceHistoricalRequestsController)).Should().BeTrue();

	[Theory]
	[InlineData(nameof(HistoricalReclassificationsController.Groups))]
	[InlineData(nameof(HistoricalReclassificationsController.List))]
	[InlineData(nameof(HistoricalReclassificationsController.SetDisposition))]
	[InlineData(nameof(HistoricalReclassificationsController.Preview))]
	[InlineData(nameof(HistoricalReclassificationsController.Read))]
	[InlineData(nameof(HistoricalReclassificationsController.Summary))]
	[InlineData(nameof(HistoricalReclassificationsController.Items))]
	[InlineData(nameof(HistoricalReclassificationsController.Apply))]
	[InlineData(nameof(HistoricalReclassificationsController.Cancel))]
	public void CleanupActionRequiresAdmin(string action) => RequiresAdmin(Action(action)).Should().BeTrue();

	[Theory]
	[InlineData(nameof(HistoricalReclassificationsController.Activity))]
	[InlineData(nameof(HistoricalReclassificationsController.Undo))]
	public void ActivityActionStaysOpenToOwner(string action)
	{
		RequiresAdmin(Action(action)).Should().BeFalse();
		RequiresAdmin(typeof(HistoricalReclassificationsController)).Should().BeFalse();
	}

	private static HistoricalReclassificationsController Controller(IHistoricalOperationService service, string role) =>
		new(service, Mock.Of<IHistoricalGroupReader>())
		{
			ControllerContext = new ControllerContext
			{
				HttpContext = new DefaultHttpContext
				{
					User = new ClaimsPrincipal(new ClaimsIdentity(
						[new Claim(ClaimTypes.NameIdentifier, "actor"), new Claim(ClaimTypes.Role, role)], "test"))
				}
			}
		};

	private static readonly HistoricalUndoBody Body = new(Guid.NewGuid(), "Restore", 1);
	private static readonly HistoricalProgress Progress = new(Guid.NewGuid(), HistoricalOperationState.Undone, 1, 1, 0, 0, 0, 1, 0, 0);

	[Fact]
	public async Task AdminUndoKeepsTheUnrestrictedPath()
	{
		Mock<IHistoricalOperationService> service = new(MockBehavior.Strict);
		Guid id = Guid.NewGuid();
		service.Setup(x => x.UndoChunkAsync("actor", id, It.IsAny<HistoricalUndoCommand>(), 1, It.IsAny<CancellationToken>()))
			.ReturnsAsync(Progress);

		var result = await Controller(service.Object, AppRoles.Admin).Undo(id, Body, CancellationToken.None);

		result.Result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<HistoricalProgress>>();
	}

	[Fact]
	public async Task NonAdminUndoIsLimitedToAutomaticOperations()
	{
		Mock<IHistoricalOperationService> service = new(MockBehavior.Strict);
		Guid id = Guid.NewGuid();
		service.Setup(x => x.UndoAutomaticChunkAsync("actor", id, It.IsAny<HistoricalUndoCommand>(), 1, It.IsAny<CancellationToken>()))
			.ReturnsAsync(Progress);

		var result = await Controller(service.Object, AppRoles.User).Undo(id, Body, CancellationToken.None);

		result.Result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<HistoricalProgress>>();
	}

	[Fact]
	public async Task NonAdminUndoOfCleanupIsForbidden()
	{
		Mock<IHistoricalOperationService> service = new(MockBehavior.Strict);
		Guid id = Guid.NewGuid();
		service.Setup(x => x.UndoAutomaticChunkAsync("actor", id, It.IsAny<HistoricalUndoCommand>(), 1, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new TaxonomyException(403, "Only an Admin can undo a cleanup."));

		var result = await Controller(service.Object, AppRoles.User).Undo(id, Body, CancellationToken.None);

		result.Result.Should().BeOfType<ForbiddenProblem>();
	}
}
