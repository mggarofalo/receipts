using API.Middleware;
using Scalar.AspNetCore;

namespace API.Configuration;

public static class OpenApiConfiguration
{
	public static IServiceCollection AddOpenApiServices(this IServiceCollection services)
	{
		services.AddOpenApi();
		return services;
	}

	public static WebApplication UseOpenApiServices(this WebApplication app)
	{
		if (app.Environment.IsDevelopment())
		{
			app.MapOpenApi();
			app.MapScalarApiReference();
			// Response validation is registered separately via UseOpenApiResponseValidation()
			// AFTER UseApplicationServices() (which includes UseResponseCompression()).
			// This ensures the validation middleware reads uncompressed response bodies.
		}
		else
		{
			app.UseHsts();
		}

		return app;
	}
}
