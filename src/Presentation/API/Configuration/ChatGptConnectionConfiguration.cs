using Application.NormalizationAi;
using Infrastructure.NormalizationAi;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace API.Configuration;

public static class ChatGptConnectionConfiguration
{
	public static IServiceCollection AddChatGptConnectionServices(this IServiceCollection services, bool runRecovery)
	{
		services.TryAddSingleton(TimeProvider.System);
		services.AddSingleton<IChatGptDeviceSignIn, ChatGptDeviceSignIn>();
		if (runRecovery)
		{
			services.AddHostedService<ChatGptConnectionRecoveryWorker>();
		}

		return services;
	}
}
