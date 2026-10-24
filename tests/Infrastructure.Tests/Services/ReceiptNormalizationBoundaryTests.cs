using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Services;

namespace Infrastructure.Tests.Services;

public sealed class ReceiptNormalizationBoundaryTests
{
	[Fact]
	public void BackgroundResolutionCannotAcceptAnAutomaticMatchingOrEmbeddingDependency()
	{
		var dependencies = typeof(NormalizedDescriptionResolutionService).GetConstructors()
			.SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType);
		dependencies.Should().NotContain(typeof(INormalizedDescriptionService));
		dependencies.Should().NotContain(typeof(IEmbeddingService));
	}

	[Fact]
	public void AdvisoryServiceDoesNotExposeAutomaticIdentityCreation()
	{
		typeof(INormalizedDescriptionService).GetMethods().Select(method => method.Name)
			.Should().NotContain("GetOrCreateAsync", "receipt entry needs an explicit decision or approved scoped mapping, not a similarity-based creation API");
	}
}
