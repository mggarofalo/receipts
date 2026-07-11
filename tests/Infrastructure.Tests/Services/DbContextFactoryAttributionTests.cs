using Application.Interfaces.Services;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Audit;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SampleData.Entities;

namespace Infrastructure.Tests.Services;

/// <summary>
/// Regression tests for RECEIPTS-753. Every repository/service writes through the
/// <see cref="IDbContextFactory{TContext}"/>. Before the fix the factory built contexts via the
/// options-only constructor (it carried <c>[ActivatorUtilitiesConstructor]</c>), leaving
/// <c>ICurrentUserAccessor</c> and <c>IDescriptionChangeSignal</c> null on the primary write path — so
/// soft-delete attribution and audit rows were stamped with a null user.
///
/// Unlike the existing attribution tests (which hand-roll a 3-param <c>TestDbContextFactoryWithUser</c>
/// and therefore cannot catch this), these tests resolve the REAL EF Core <c>DbContextFactory&lt;T&gt;</c>
/// out of a DI container and let ActivatorUtilities pick the constructor — exactly the broken mechanism.
/// </summary>
public class DbContextFactoryAttributionTests
{
	[Fact]
	public async Task FactoryCreatedContext_SoftDelete_StampsCurrentUser_OnEntityAndAudit()
	{
		// Arrange — mirror production wiring: a SINGLETON IDbContextFactory resolving a SINGLETON
		// ICurrentUserAccessor. AddDbContextFactory registers the real EF DbContextFactory<T>, which uses
		// ActivatorUtilities honoring [ActivatorUtilitiesConstructor] on the 3-param ctor.
		const string userId = "factory-path-user";
		string dbName = "attr_" + Guid.NewGuid();

		ServiceCollection services = new();
		services.AddDbContextFactory<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
		services.AddSingleton<IDescriptionChangeSignal, DescriptionChangeSignal>();
		services.AddSingleton<ICurrentUserAccessor>(new MockCurrentUserAccessor { UserId = userId });

		await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
		{
			ValidateScopes = true,
			ValidateOnBuild = true,
		});

		IDbContextFactory<ApplicationDbContext> factory =
			provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

		Guid templateId;
		await using (ApplicationDbContext context = factory.CreateDbContext())
		{
			ItemTemplateEntity entity = ItemTemplateEntityGenerator.Generate();
			await context.ItemTemplates.AddAsync(entity);
			await context.SaveChangesAsync();
			templateId = entity.Id;
		}

		// Act — soft-delete through a factory-created context.
		await using (ApplicationDbContext context = factory.CreateDbContext())
		{
			ItemTemplateEntity template = await context.ItemTemplates.FirstAsync(t => t.Id == templateId);
			context.ItemTemplates.Remove(template);
			await context.SaveChangesAsync();
		}

		// Assert — before the fix these would both be null (the factory built a null-accessor context).
		await using (ApplicationDbContext context = factory.CreateDbContext())
		{
			ItemTemplateEntity deleted = await context.ItemTemplates
				.IgnoreQueryFilters()
				.FirstAsync(t => t.Id == templateId);
			deleted.DeletedAt.Should().NotBeNull();
			deleted.DeletedByUserId.Should().Be(userId);

			AuditLogEntity deleteAudit = await context.AuditLogs
				.FirstAsync(a => a.EntityType == "ItemTemplate" && a.Action == AuditAction.Delete);
			deleteAudit.ChangedByUserId.Should().Be(userId);
		}
	}

	[Fact]
	public void SingletonFactory_ResolvesSingletonAccessorFromRoot_NoCaptiveDependency()
	{
		// The reason the accessor and signal MUST be singletons: the IDbContextFactory is itself a singleton
		// and resolves the context's ctor dependencies from the ROOT provider. Resolving a scoped service
		// from root is a captive-dependency violation (throws under ValidateScopes). Here the factory builds
		// a context with NO active scope; a scoped accessor would throw.
		ServiceCollection services = new();
		services.AddDbContextFactory<ApplicationDbContext>(o => o.UseInMemoryDatabase("captive_" + Guid.NewGuid()));
		services.AddSingleton<IDescriptionChangeSignal, DescriptionChangeSignal>();
		services.AddSingleton<ICurrentUserAccessor, NullCurrentUserAccessor>();

		using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
		{
			ValidateScopes = true,
			ValidateOnBuild = true,
		});

		IDbContextFactory<ApplicationDbContext> factory =
			provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

		Action createWithoutScope = () =>
		{
			using ApplicationDbContext context = factory.CreateDbContext();
		};

		createWithoutScope.Should().NotThrow();
	}

	[Fact]
	public void RegisterInfrastructureServices_RegistersAccessorAsSingleton_AndDbContextAsScoped()
	{
		// Lock in the lifetimes the fix depends on: the fallback ICurrentUserAccessor is a singleton (so the
		// singleton factory can resolve it from root), and ApplicationDbContext stays scoped for Identity.
		ServiceCollection services = new();
		services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[ConfigurationVariables.PostgresHost] = "localhost",
				[ConfigurationVariables.PostgresPort] = "5432",
				[ConfigurationVariables.PostgresUser] = "user",
				[ConfigurationVariables.PostgresPassword] = "password",
				[ConfigurationVariables.PostgresDb] = "testdb",
			})
			.Build();

		services.RegisterInfrastructureServices(configuration);

		ServiceDescriptor accessor = services.Single(d => d.ServiceType == typeof(ICurrentUserAccessor));
		accessor.Lifetime.Should().Be(ServiceLifetime.Singleton);

		ServiceDescriptor dbContext = services.Last(d => d.ServiceType == typeof(ApplicationDbContext));
		dbContext.Lifetime.Should().Be(ServiceLifetime.Scoped);
	}

	[Fact]
	public async Task FactoryWithAccessorButNoSignal_LikeDbMigrator_SavesWithNullAttribution()
	{
		// Replicates DbMigrator's DI exactly: the EF factory + the null-attribution accessor, but NO
		// IDescriptionChangeSignal registered. The 3-param ctor's signal parameter is optional, so
		// ActivatorUtilities must fall back to its default (null) rather than throwing. If this regressed,
		// every entry point that registers the factory directly without the signal (DbMigrator, run under
		// docker-entrypoint.sh's `set -e` before the API starts) would abort container boot. RECEIPTS-753.
		ServiceCollection services = new();
		services.AddDbContextFactory<ApplicationDbContext>(o => o.UseInMemoryDatabase("migrator_" + Guid.NewGuid()));
		services.AddSingleton<ICurrentUserAccessor, NullCurrentUserAccessor>();
		// NOTE: intentionally NO IDescriptionChangeSignal registration — matches DbMigrator.

		await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
		{
			ValidateScopes = true,
			ValidateOnBuild = true,
		});

		IDbContextFactory<ApplicationDbContext> factory =
			provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

		ItemTemplateEntity entity = ItemTemplateEntityGenerator.Generate();
		await using (ApplicationDbContext context = factory.CreateDbContext())
		{
			await context.ItemTemplates.AddAsync(entity);
			await context.SaveChangesAsync();
		}

		// A null accessor and unregistered signal must not throw on the soft-delete/audit write path.
		await using (ApplicationDbContext context = factory.CreateDbContext())
		{
			ItemTemplateEntity template = await context.ItemTemplates.FirstAsync(t => t.Id == entity.Id);
			context.ItemTemplates.Remove(template);
			await context.SaveChangesAsync();
		}

		await using (ApplicationDbContext context = factory.CreateDbContext())
		{
			ItemTemplateEntity deleted = await context.ItemTemplates
				.IgnoreQueryFilters()
				.FirstAsync(t => t.Id == entity.Id);
			deleted.DeletedAt.Should().NotBeNull();
			deleted.DeletedByUserId.Should().BeNull();
		}
	}

	[Fact]
	public void FactoryWithoutAccessorRegistration_CreateDbContext_Throws()
	{
		// Documents the blast radius of moving [ActivatorUtilitiesConstructor] to the 3-param ctor: a
		// container that registers the factory but NOT ICurrentUserAccessor can no longer build a context.
		// This is the exact break DbMigrator hit — every entry point that registers the factory directly
		// must also register the accessor. RECEIPTS-753.
		ServiceCollection services = new();
		services.AddDbContextFactory<ApplicationDbContext>(o => o.UseInMemoryDatabase("noaccessor_" + Guid.NewGuid()));

		using ServiceProvider provider = services.BuildServiceProvider();
		IDbContextFactory<ApplicationDbContext> factory =
			provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

		Action createWithoutAccessor = () => factory.CreateDbContext();

		createWithoutAccessor.Should().Throw<InvalidOperationException>()
			.WithMessage("*ICurrentUserAccessor*");
	}
}
