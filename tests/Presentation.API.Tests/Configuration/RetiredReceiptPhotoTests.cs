using System.Net;
using API.Configuration;
using API.Controllers.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Presentation.API.Tests.Configuration;

public class RetiredReceiptPhotoTests
{
	[Fact]
	public async Task RetiredPhotoUpload_HasNoRouteWhileReceiptRoutesRemainRegistered()
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Services.AddVersioningServices();
		builder.Services.AddAuthorization();
		builder.Services.AddControllers().AddApplicationPart(typeof(ReceiptsController).Assembly);
		await using WebApplication app = builder.Build();
		app.UseAuthorization();
		app.MapControllers().AllowAnonymous();
		await app.StartAsync();
		var actions = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items;
		actions.Should().Contain(action => action.AttributeRouteInfo != null && action.AttributeRouteInfo.Template == "api/receipts",
			"the test must discover the production controllers rather than pass with an empty route table");
		using HttpClient client = app.GetTestClient();
		using MultipartFormDataContent upload = new();
		upload.Add(new ByteArrayContent([137, 80, 78, 71]), "file", "receipt.png");
		using HttpResponseMessage response = await client.PostAsync($"/api/receipts/{Guid.NewGuid()}/image", upload);
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}
}
