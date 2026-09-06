using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using API.Configuration;
using API.Controllers.Core;
using API.Middleware;
using API.Services;
using Application.Interfaces.Services;
using Application.Services;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public partial class TemplateNormalizationOwnershipTests
{
	[Fact]
	public async Task HttpStaleTemplateUpdate_Returns409ProblemWithoutSuccessNotification_AndKeepsManualChanges()
	{
		Seed seed = await SeedAsync();
		Guid target = await AddCanonicalAsync("Requested change");
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Mock<INormalizedDescriptionService> canonical = new();
		canonical.Setup(service => service.GetOrCreateForTemplateAsync("Requested change", It.IsAny<CancellationToken>())).Returns(async (string _, CancellationToken token) =>
		{
			entered.TrySetResult(); await release.Task.WaitAsync(token); return Canonical(target, "Requested change");
		});
		Mock<IEntityChangeNotifier> notifier = new();
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddVersioningServices().AddApplicationServices(builder.Configuration).RegisterProgramServices().RegisterApplicationServices(builder.Configuration);
		builder.Services.AddAuthorization();
		builder.Services.AddControllers().AddApplicationPart(typeof(ItemTemplatesController).Assembly);
		RegisterServices(builder.Services, canonical.Object);
		builder.Services.AddSingleton(notifier.Object);
		await using WebApplication app = builder.Build();
		app.UseMiddleware<ValidationExceptionMiddleware>();
		app.UseAuthorization();
		app.MapControllers().AllowAnonymous();
		await app.StartAsync();
		using HttpClient client = app.GetTestClient();
		Task<HttpResponseMessage> pending = client.PutAsJsonAsync($"/api/item-templates/{seed.Template}", new { id = seed.Template, name = "Requested change", defaultCategory = "Attempted" });
		try
		{
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await using ApplicationDbContext edit = fixture.CreateDbContext();
			(await edit.ItemTemplates.SingleAsync(row => row.Id == seed.Template)).DefaultCategory = "Newer manual category";
			await edit.SaveChangesAsync();
		}
		finally { release.TrySetResult(); }
		using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
		response.StatusCode.Should().Be(HttpStatusCode.Conflict);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
		using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		problem.RootElement.GetProperty("status").GetInt32().Should().Be(409);
		problem.RootElement.GetProperty("detail").GetString().Should().Contain("Reload");
		notifier.Verify(service => service.NotifyUpdated(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		var stored = await verify.ItemTemplates.SingleAsync(row => row.Id == seed.Template);
		stored.Name.Should().Be("Original template");
		stored.DefaultCategory.Should().Be("Newer manual category");
	}
}
