using Application.Interfaces.Services;
using Application.Queries.Core.ItemTemplate.GetCategoryRecommendations;
using Application.Queries.Core.ItemTemplate.GetHistoryCandidates;
using Application.Queries.Core.ItemTemplate.GetSimilarItems;
using Application.Services;
using FluentAssertions;
using FluentValidation;
using Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Application.Tests.Services;

public class ValidationCompositionTests
{
	private static ServiceProvider CreateProvider(Mock<IItemTemplateSimilarityService> similarity, Mock<IItemTemplateHistoryCandidateService> history)
	{
		ServiceCollection services = new();
		services.RegisterApplicationServices(new ConfigurationBuilder().Build());
		services.AddSingleton(similarity.Object);
		services.AddSingleton(history.Object);
		return services.BuildServiceProvider();
	}

	[Fact]
	public void ProductionRegistration_ResolvesAllApplicationValidatorsOnce()
	{
		using ServiceProvider provider = CreateProvider(new(), new());
		using IServiceScope scope = provider.CreateScope();
		scope.ServiceProvider.GetServices<IValidator<GetSimilarItemsQuery>>().Should().ContainSingle().Which.Should().BeOfType<GetSimilarItemsQueryValidator>();
		scope.ServiceProvider.GetServices<IValidator<GetCategoryRecommendationsQuery>>().Should().ContainSingle().Which.Should().BeOfType<GetCategoryRecommendationsQueryValidator>();
		scope.ServiceProvider.GetServices<IValidator<GetItemTemplateHistoryCandidatesQuery>>().Should().ContainSingle().Which.Should().BeOfType<GetItemTemplateHistoryCandidatesQueryValidator>();
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(0)]
	[InlineData(21)]
	public async Task Mediator_InvalidSimilarityLimit_RejectsBeforeService(int limit)
	{
		Mock<IItemTemplateSimilarityService> similarity = new();
		using ServiceProvider provider = CreateProvider(similarity, new());
		using IServiceScope scope = provider.CreateScope();
		Func<Task> act = async () => await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new GetSimilarItemsQuery("aa", limit, 0.3, false));
		await act.Should().ThrowAsync<ValidationException>();
		similarity.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData(1)]
	[InlineData(20)]
	public async Task Mediator_ValidBoundaryLimit_DispatchesOnce(int limit)
	{
		Mock<IItemTemplateSimilarityService> similarity = new();
		similarity.Setup(s => s.GetSimilarItemsAsync("aa", limit, 0.3, false, It.IsAny<CancellationToken>())).ReturnsAsync([]);
		using ServiceProvider provider = CreateProvider(similarity, new());
		using IServiceScope scope = provider.CreateScope();
		(await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new GetSimilarItemsQuery("aa", limit, 0.3, false))).Should().BeEmpty();
		similarity.Verify(s => s.GetSimilarItemsAsync("aa", limit, 0.3, false, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Mediator_OtherApplicationValidators_RejectBeforeEitherService()
	{
		Mock<IItemTemplateSimilarityService> similarity = new();
		Mock<IItemTemplateHistoryCandidateService> history = new();
		using ServiceProvider provider = CreateProvider(similarity, history);
		using IServiceScope scope = provider.CreateScope();
		IMediator mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
		Func<Task> category = async () => await mediator.Send(new GetCategoryRecommendationsQuery("a", 21));
		Func<Task> candidates = async () => await mediator.Send(new GetItemTemplateHistoryCandidatesQuery(-1, 501, 0));
		await category.Should().ThrowAsync<ValidationException>();
		await candidates.Should().ThrowAsync<ValidationException>();
		similarity.VerifyNoOtherCalls();
		history.VerifyNoOtherCalls();
	}
}
