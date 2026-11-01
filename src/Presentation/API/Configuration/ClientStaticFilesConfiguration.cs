using Microsoft.AspNetCore.StaticFiles;

namespace API.Configuration;

/// <summary>
/// Serves the built React client. Vite names every file under /assets by its content hash, so those
/// never change and are cached for a year. Everything else, index.html above all, is revalidated on
/// each request: without a Cache-Control header browsers cached index.html heuristically and kept
/// running the previous bundle for hours after an upgrade (RECEIPTS-1088).
/// </summary>
public static class ClientStaticFilesConfiguration
{
	public const string Revalidate = "no-cache";
	public const string Immutable = "public, max-age=31536000, immutable";

	private static readonly PathString HashedAssets = new("/assets");

	private static readonly StaticFileOptions Files = new()
	{
		// The SPA fallback answers any unknown path without a file extension with index.html, even
		// under /assets, so the file served decides and that answer is never marked immutable.
		OnPrepareResponse = context => context.Context.Response.Headers.CacheControl =
			context.Context.Request.Path.StartsWithSegments(HashedAssets) && !context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
				? Immutable
				: Revalidate,
	};

	public static IApplicationBuilder UseClientStaticFiles(this IApplicationBuilder app)
	{
		app.UseDefaultFiles();
		return app.UseStaticFiles(Files);
	}

	public static IEndpointRouteBuilder MapClientFallback(this IEndpointRouteBuilder endpoints)
	{
		endpoints.MapFallbackToFile("index.html", Files);
		return endpoints;
	}
}
