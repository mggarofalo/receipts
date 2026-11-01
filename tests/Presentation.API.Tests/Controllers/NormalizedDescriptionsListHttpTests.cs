using System.Net;
using API.Configuration;
using API.Controllers;
using API.Middleware;
using Application.Models;
using Application.Models.NormalizedDescriptions;
using Application.Queries.NormalizedDescription.GetAll;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Presentation.API.Tests.Fixtures;

namespace Presentation.API.Tests.Controllers;

// Runs the list endpoint through the real MVC pipeline: model binding turns an absent
// repeatable ?status into an empty array before the validation filter sees it (RECEIPTS-1108).
public class NormalizedDescriptionsListHttpTests
{
	private readonly Mock<IMediator> _mediator = new();

	private async Task<WebApplication> StartAsync()
	{
		WebApplicationBuilder builder = ConfiguredApiTestHost.CreateBuilder([]);
		builder.Services.AddVersioningServices().AddApplicationServices(builder.Configuration);
		builder.Services.AddControllers().AddApplicationPart(typeof(NormalizedDescriptionsController).Assembly);
		builder.Services.AddAuthorization(options => options.AddPolicy("RequireAdmin", policy => policy.RequireAssertion(_ => true)));
		builder.Services.AddSingleton(_mediator.Object);
		WebApplication app = builder.Build();
		app.UseMiddleware<ValidationExceptionMiddleware>();
		app.UseAuthorization();
		app.MapControllers().AllowAnonymous();
		await app.StartAsync();
		return app;
	}

	[Fact]
	public async Task GetAll_WithoutStatus_ListsEveryStatus()
	{
		// Arrange
		_mediator
			.Setup(m => m.Send(It.IsAny<GetAllNormalizedDescriptionsQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<NormalizedDescriptionDetail>([], 0, 0, 50));
		await using WebApplication app = await StartAsync();
		using HttpClient client = app.GetTestClient();

		// Act
		using HttpResponseMessage response = await client.GetAsync("/api/normalized-descriptions");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
		_mediator.Verify(m => m.Send(It.Is<GetAllNormalizedDescriptionsQuery>(q => q.StatusFilter == null), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetAll_WithAnUnknownStatus_ReturnsAProblemNamingTheParameter()
	{
		// Arrange
		await using WebApplication app = await StartAsync();
		using HttpClient client = app.GetTestClient();

		// Act
		using HttpResponseMessage response = await client.GetAsync("/api/normalized-descriptions?status=Archived");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
		(await response.Content.ReadAsStringAsync()).Should().Contain(NormalizedDescriptionsController.InvalidStatusFilter);
	}
}
