using API.Mapping.Aggregates;
using API.Middleware;
using Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace API.Services;

public static class ProgramService
{
	public static IServiceCollection RegisterProgramServices(this IServiceCollection services)
	{
		services.AddControllers();
		services.AddHttpContextAccessor();
		// Singleton (RECEIPTS-753): CurrentUserAccessor is a stateless lazy facade over
		// IHttpContextAccessor — it reads HttpContext?.User on each property access and captures nothing
		// at construction. A singleton lifetime lets the singleton IDbContextFactory inject it into
		// factory-created contexts without a captive dependency, while still observing the current request.
		services.AddSingleton<ICurrentUserAccessor, CurrentUserAccessor>();

		services
			.AddSingleton<API.Mapping.Core.AccountMapper>()
			.AddSingleton<API.Mapping.Core.CardMapper>()
			.AddSingleton<API.Mapping.Core.CategoryMapper>()
			.AddSingleton<API.Mapping.Core.SubcategoryMapper>()
			.AddSingleton<API.Mapping.Core.ReceiptMapper>()
			.AddSingleton<API.Mapping.Core.ReceiptItemMapper>()
			.AddSingleton<API.Mapping.Core.TransactionMapper>()
			.AddSingleton<API.Mapping.Core.AdjustmentMapper>()
			.AddSingleton<API.Mapping.Core.ItemTemplateMapper>()
			.AddSingleton<API.Mapping.Core.YnabMapper>()
			.AddSingleton<ReceiptWithItemsMapper>()
			.AddSingleton<TransactionAccountMapper>()
			.AddSingleton<TripMapper>();

		services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuthorizationLoggingHandler>();

		services.AddSignalR();
		services.AddSingleton<IEntityChangeNotifier, EntityChangeNotifier>();

		return services;
	}
}
