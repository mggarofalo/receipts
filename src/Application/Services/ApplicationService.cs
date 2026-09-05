using Application.Behaviors;
using Application.Interfaces;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Services;

public static class ApplicationService
{
	public static IServiceCollection RegisterApplicationServices(this IServiceCollection services, IConfiguration configuration)
	{
		// Register validators with their owning layer so every Mediator entry point
		// receives the same validation, independently of the HTTP host.
		services.AddValidatorsFromAssembly(typeof(ApplicationService).Assembly);

		services.AddMediator(opts =>
		{
			opts.ServiceLifetime = ServiceLifetime.Scoped;
			opts.Assemblies = [typeof(ICommand<>)];
			opts.PipelineBehaviors = [typeof(ValidationBehavior<,>)];
		});

		return services;
	}
}