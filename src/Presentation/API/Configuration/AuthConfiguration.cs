using System.Text;
using API.Authentication;
using API.Middleware;
using Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace API.Configuration;

public static class AuthConfiguration
{
	public const string PolicySchemeName = "JwtOrApiKey";

	public static IServiceCollection AddAuthServices(this IServiceCollection services, IConfiguration configuration)
	{
		string jwtKey = configuration[ConfigurationVariables.JwtKey]
			?? throw new InvalidOperationException($"Configuration value '{ConfigurationVariables.JwtKey}' is required.");
		string jwtIssuer = configuration[ConfigurationVariables.JwtIssuer] ?? "receipts-api";
		string jwtAudience = configuration[ConfigurationVariables.JwtAudience] ?? "receipts-app";

		services
			.AddAuthentication(PolicySchemeName)
			.AddPolicyScheme(PolicySchemeName, "JWT or API Key", options =>
			{
				options.ForwardDefaultSelector = context =>
					context.Request.Headers.ContainsKey("X-API-Key")
						? ApiKeyAuthenticationDefaults.AuthenticationScheme
						: JwtBearerDefaults.AuthenticationScheme;
			})
			.AddJwtBearer(options =>
			{
				options.TokenValidationParameters = new TokenValidationParameters
				{
					ValidateIssuerSigningKey = true,
					IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
					ValidateIssuer = true,
					ValidIssuer = jwtIssuer,
					ValidateAudience = true,
					ValidAudience = jwtAudience,
					ValidateLifetime = true,
					ClockSkew = TimeSpan.Zero,
				};

				// Support JWT via query string for SignalR WebSocket connections.
				// HTTP headers are unavailable on the WebSocket upgrade request, so the
				// SignalR client passes the token as ?access_token=… instead.
				options.Events = new JwtBearerEvents
				{
					OnMessageReceived = context =>
					{
						string? token = context.Request.Query["access_token"];
						if (!string.IsNullOrEmpty(token) &&
							context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
						{
							context.Token = token;
						}

						return Task.CompletedTask;
					},
					// Re-validate the token against the user's live state on every request. A JWT is
					// otherwise trusted until it expires, so an access token issued before a user was
					// deactivated or had their password reset would keep working. Rotating the user's
					// security stamp (see UsersController / AuthController) makes this fail closed
					// immediately (RECEIPTS-800).
					OnTokenValidated = JwtSecurityStampValidator.RevalidateAsync,
					OnAuthenticationFailed = context =>
					{
						ILoggerFactory loggerFactory = context.HttpContext.RequestServices
							.GetRequiredService<ILoggerFactory>();
						ILogger logger = loggerFactory.CreateLogger("API.Configuration.AuthConfiguration");
						logger.LogWarning(
							"Authentication failed. Path: {Path}, IP: {IP}, Error: {Error}",
							context.Request.Path,
							context.HttpContext.Connection.RemoteIpAddress,
							context.Exception.Message);
						return Task.CompletedTask;
					},
					OnChallenge = context =>
					{
						ILoggerFactory loggerFactory = context.HttpContext.RequestServices
							.GetRequiredService<ILoggerFactory>();
						ILogger logger = loggerFactory.CreateLogger("API.Configuration.AuthConfiguration");
						logger.LogInformation(
							"Authentication challenge issued. Path: {Path}, IP: {IP}",
							context.Request.Path,
							context.HttpContext.Connection.RemoteIpAddress);
						return Task.CompletedTask;
					},
				};
			})
			.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
				ApiKeyAuthenticationDefaults.AuthenticationScheme,
				_ => { });

		services.AddAuthorization(options =>
		{
			options.AddPolicy("ApiOrJwt", policy =>
			{
				policy.AddAuthenticationSchemes(
					JwtBearerDefaults.AuthenticationScheme,
					ApiKeyAuthenticationDefaults.AuthenticationScheme);
				policy.RequireAuthenticatedUser();
			});

			options.AddPolicy("RequireAdmin", policy =>
			{
				policy.AddAuthenticationSchemes(
					JwtBearerDefaults.AuthenticationScheme,
					ApiKeyAuthenticationDefaults.AuthenticationScheme);
				policy.RequireAuthenticatedUser();
				policy.RequireRole(AppRoles.Admin);
			});

			options.DefaultPolicy = options.GetPolicy("ApiOrJwt")!;
		});

		return services;
	}

	public static IApplicationBuilder UseAuthServices(this IApplicationBuilder app)
	{
		app.UseAuthentication();
		app.UseAuthorization();
		app.UseRateLimiter();
		app.UseMiddleware<MustResetPasswordMiddleware>();
		return app;
	}
}
