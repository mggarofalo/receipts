using System.Security.Claims;
using System.Text.Json;
using API.Controllers;
using API.Http;
using Application.Taxonomy;
using Application.Taxonomy.Restructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Presentation.API.Tests.Controllers;

public class RestructurePlansControllerTests
{
	[Fact]
	public async Task AServiceForbiddenBecomesAForbiddenProblemDocument()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		Mock<IRestructurePlanService> service = new();
		service.Setup(x => x.CancelAsync("admin-2", id, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new TaxonomyException(403, "An enabled administrator is required to restructure categories."));
		RestructurePlansController controller = new(service.Object)
		{
			ControllerContext = new()
			{
				HttpContext = new DefaultHttpContext
				{
					User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "admin-2")], "test")),
				},
			},
		};
		DefaultHttpContext response = new()
		{
			RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider(),
			Response = { Body = new MemoryStream() },
		};

		// Act
		Results<Ok<RestructureApplicationSummary>, BadRequestProblem, ForbiddenProblem, NotFoundProblem, ConflictProblem> result =
			await controller.Cancel(id, CancellationToken.None);
		await result.Result.Should().BeOfType<ForbiddenProblem>().Which.ExecuteAsync(response);

		// Assert
		response.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
		response.Response.ContentType.Should().StartWith("application/problem+json");
		response.Response.Body.Position = 0;
		using JsonDocument problem = await JsonDocument.ParseAsync(response.Response.Body);
		problem.RootElement.GetProperty("status").GetInt32().Should().Be(403);
		problem.RootElement.GetProperty("detail").GetString().Should().Be("An enabled administrator is required to restructure categories.");
		problem.RootElement.GetProperty("code").GetString().Should().Be("enabled_admin_required");
	}
}
