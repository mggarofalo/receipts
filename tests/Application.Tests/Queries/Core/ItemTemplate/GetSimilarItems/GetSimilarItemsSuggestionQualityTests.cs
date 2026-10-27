using Application.Interfaces.Services;
using Application.Queries.Core.ItemTemplate.GetSimilarItems;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.ItemTemplate.GetSimilarItems;

public sealed class GetSimilarItemsSuggestionQualityTests
{
	[Theory]
	[InlineData("history")]
	[InlineData("template")]
	public async Task Handle_RejectsSharedBrandWithoutProductEvidence(string source)
	{
		var unrelated = Result("Acme mallet", source, .95);
		var handler = HandlerReturning(unrelated);

		var results = await handler.Handle(new("Acme beans", 5, .3), CancellationToken.None);

		results.Should().BeEmpty("a shared brand cannot complete a different product name");
	}

	[Theory]
	[InlineData("history")]
	[InlineData("template")]
	public async Task Handle_RejectsNumberAndUnitOnlyOverlapDespiteAdmittedSearchScore(string source)
	{
		// The retrieval service has already admitted this candidate above the .3
		// trigram threshold. A high search/semantic score is not product evidence.
		var unrelated = Result("Hammer 12 oz", source, 6d / 19d);
		unrelated.SemanticSimilarity = .95;
		unrelated.CombinedScore = .9;
		var handler = HandlerReturning(unrelated);

		var results = await handler.Handle(new("Beans 12 oz", 5, .3), CancellationToken.None);

		results.Should().BeEmpty("the only overlap is a package number and unit");
	}

	[Theory]
	[InlineData("mil", "Milk")]
	[InlineData("Milk", "Milk")]
	[InlineData("Whole milk", "Milk")]
	public async Task Handle_RetainsUsefulTypingAndProductMatches(string input, string name)
	{
		var useful = Result(name, "history", .5);
		useful.DefaultUnitPrice = 2.49m;
		useful.DefaultItemCode = "MILK-1";
		var handler = HandlerReturning(useful);

		var results = await handler.Handle(new(input, 5, .3), CancellationToken.None);

		results.Should().ContainSingle().Which.Should().BeSameAs(useful,
			"advisory history defaults and provenance must remain intact");
	}

	[Fact]
	public async Task Handle_FillsRequestedPageFromUsefulRowsAfterRejectingWeakReserve()
	{
		var useful = Result("Beans 12 oz", "history", .5);
		var service = new Mock<IItemTemplateSimilarityService>();
		service.Setup(value => value.GetSimilarItemsAsync("Beans 12 oz", 5, .3, true,
			It.IsAny<CancellationToken>())).ReturnsAsync([
				Result("Hammer 12 oz", "history", .9),
				Result("Mallet 12 oz", "template", .8),
				useful,
			]);
		var handler = new GetSimilarItemsQueryHandler(service.Object);

		var results = await handler.Handle(new("Beans 12 oz", 1, .3), CancellationToken.None);

		results.Should().ContainSingle().Which.Should().BeSameAs(useful);
		service.Verify(value => value.GetSimilarItemsAsync("Beans 12 oz", 5, .3, true,
			It.IsAny<CancellationToken>()), Times.Once);
	}

	private static GetSimilarItemsQueryHandler HandlerReturning(params SimilarItemResult[] rows)
	{
		var service = new Mock<IItemTemplateSimilarityService>();
		service.Setup(value => value.GetSimilarItemsAsync(It.IsAny<string>(), It.IsAny<int>(),
			It.IsAny<double>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(rows.ToList());
		return new(service.Object);
	}

	private static SimilarItemResult Result(string name, string source, double score)
		=> new() { Name = name, Source = source, Similarity = score, CombinedScore = score };
}
