using Application.Interfaces.Services;
using Application.Models.Search;
using Application.Queries.Search;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Search;

public class SearchPurchasesQueryTests
{
	[Theory]
	[InlineData(null, 0, 25)]
	[InlineData("", 0, 25)]
	[InlineData("  a  ", 0, 25)]
	[InlineData("milk", -1, 25)]
	[InlineData("milk", 0, 0)]
	[InlineData("milk", 0, 101)]
	public void Validate_RejectsInvalidSearch(string? query, int offset, int limit)
		=> new SearchPurchasesQueryValidator().Validate(new SearchPurchasesQuery(query!, offset, limit)).IsValid.Should().BeFalse();

	[Theory]
	[InlineData(2, 1)]
	[InlineData(200, 100)]
	public void Validate_AcceptsTrimmedLengthAndPageBoundaries(int length, int limit)
		=> new SearchPurchasesQueryValidator().Validate(new SearchPurchasesQuery("  " + new string('x', length) + "  ", 0, limit)).IsValid.Should().BeTrue();

	[Fact]
	public void Validate_RejectsOverlongQuery()
		=> new SearchPurchasesQueryValidator().Validate(new SearchPurchasesQuery(new string('x', 201))).IsValid.Should().BeFalse();

	[Fact]
	public async Task Handle_TrimsPhraseAndPreservesPaginationAndCancellation()
	{
		using CancellationTokenSource cancellation = new();
		PurchaseSearchResult expected = new([], 204, 200, 25);
		Mock<IPurchaseSearchReader> reader = new(MockBehavior.Strict);
		reader.Setup(x => x.SearchAsync("whole milk", 200, 25, cancellation.Token)).ReturnsAsync(expected);
		PurchaseSearchResult result = await new SearchPurchasesQueryHandler(reader.Object)
			.Handle(new("  whole milk  ", 200, 25), cancellation.Token);
		result.Should().BeSameAs(expected);
		reader.VerifyAll();
	}
}

