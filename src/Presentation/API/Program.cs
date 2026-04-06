using System.Net;
using System.Security.Claims;
using API.Configuration;
using API.Hubs;
using API.Middleware;
using API.Services;
using Application.Services;
using Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Sentry;

// Create builder
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddApplicationConfiguration();

// Configure Sentry error tracking (disabled when SENTRY_DSN is empty/missing)
builder.WebHost.UseSentry(o =>
{
	o.Dsn = builder.Configuration["SENTRY_BACKEND_DSN"] ?? builder.Configuration["SENTRY_DSN"] ?? "";
	o.Environment = builder.Environment.EnvironmentName;
	o.TracesSampleRate = 0.1;
	o.SendDefaultPii = false;
});

// Persist DataProtection keys: use /data volume in containers, default location in development
if (Directory.Exists("/data"))
{
	builder.Services.AddDataProtection()
		.PersistKeysToFileSystem(new DirectoryInfo("/data/DataProtection-Keys"));
}
else
{
	builder.Services.AddDataProtection();
}

// Register services
builder.Services
	.AddOpenApiServices()
	.AddVersioningServices()
	.AddApplicationServices(builder.Configuration)
	.AddCorsServices()
	.AddAuthServices(builder.Configuration)
	.RegisterProgramServices()
	.RegisterApplicationServices(builder.Configuration)
	.RegisterInfrastructureServices(builder.Configuration);

// Build application
WebApplication app = builder.Build();

// Configure middleware
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
	ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwardedHeadersOptions.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
app.UseForwardedHeaders(forwardedHeadersOptions);
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GlobalExceptionHandlerMiddleware>();
app.UseOpenApiServices()
   .UseApplicationServices()
   .UseOpenApiResponseValidation()
   .UseSentryTracing()
   .UseCorsServices()
   .UseAuthServices();

// Attach authenticated user context to Sentry scope for error attribution
app.Use(async (context, next) =>
{
	if (context.User.Identity?.IsAuthenticated == true)
	{
		SentrySdk.ConfigureScope(scope =>
		{
			scope.User = new SentryUser
			{
				Id = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
				Email = context.User.FindFirst(ClaimTypes.Email)?.Value,
			};
		});
	}
	await next();
});

// Serve SPA static files in production (Vite dev server handles this in development)
if (!app.Environment.IsDevelopment())
{
	app.UseDefaultFiles();
	app.UseStaticFiles();
}

// Map Aspire health check endpoints (/health, /alive)
app.MapDefaultEndpoints();

// Map controllers
app.MapControllers();

// Map SignalR hubs
app.MapHub<EntityHub>("/hubs/entities");

// SPA fallback: serve index.html for client-side routes in production
if (!app.Environment.IsDevelopment())
{
	app.MapFallbackToFile("index.html");
}

// Run application
await app.RunAsync();
