using Application.Queries.Core.ItemTemplate.GetHistoryCandidates;
using FluentAssertions;

namespace Application.Tests.Queries.Core.ItemTemplate.GetHistoryCandidates;

public class GetItemTemplateHistoryCandidatesQueryTests : IQueryTests
{
	[Fact]
	public void Query_CanBeCreated()
	{
		GetItemTemplateHistoryCandidatesQuery query = new(0, 50, 2);
		Assert.NotNull(query);
	}

	[Fact]
	public void Query_ExposesConstructorArguments()
	{
		GetItemTemplateHistoryCandidatesQuery query = new(10, 25, 3);

		query.Offset.Should().Be(10);
		query.Limit.Should().Be(25);
		query.MinCount.Should().Be(3);
	}
}
