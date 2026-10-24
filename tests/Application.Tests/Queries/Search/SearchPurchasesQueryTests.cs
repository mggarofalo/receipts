using Application.Interfaces.Services;
using Application.Models.Search;
using Application.Queries.Search;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Search;

public class SearchPurchasesQueryTests
{
	[Theory]
	[InlineData("date", "asc")]
	[InlineData("description", "desc")]
	[InlineData("location", "asc")]
	[InlineData("unitPrice", "desc")]
	[InlineData("quantity", "asc")]
	[InlineData("totalAmount", "desc")]
	public void Validate_AcceptsSupportedSortsAndInclusiveEqualBounds(string column, string direction)
	{
		var options = new PurchaseSearchOptions(column, direction, "  " + new string('x', 200) + "  ",
			new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 1), 0m, 0m);
		new SearchPurchasesQueryValidator().Validate(new SearchPurchasesQuery("milk", Options: options)).IsValid.Should().BeTrue();
	}

	public static IEnumerable<object[]> InvalidOptions =>
	[
		[new PurchaseSearchOptions(SortBy: "receiptId")],
		[new PurchaseSearchOptions(SortDirection: "descending")],
		[new PurchaseSearchOptions(Location: new string('x', 201))],
		[new PurchaseSearchOptions(StartDate: new(2025, 1, 2), EndDate: new(2025, 1, 1))],
		[new PurchaseSearchOptions(MinUnitPrice: -0.0001m)],
		[new PurchaseSearchOptions(MaxUnitPrice: -0.0001m)],
		[new PurchaseSearchOptions(MinUnitPrice: 1.0001m, MaxUnitPrice: 1m)],
	];

	[Theory]
	[MemberData(nameof(InvalidOptions))]
	public void Validate_RejectsInvalidFilterAndSortOptions(PurchaseSearchOptions options)
		=> new SearchPurchasesQueryValidator().Validate(new SearchPurchasesQuery("milk", Options: options)).IsValid.Should().BeFalse();

	[Fact]
	public async Task Handle_NormalizesLocationWithoutDroppingOtherOptions()
	{
		var options = new PurchaseSearchOptions("unitPrice", "asc", "  50% Market  ", new(2025, 1, 1), new(2025, 1, 2), 1.2345m, 2.3456m);
		var expectedOptions = options with { Location = "50% Market" };
		var reader = new Mock<IPurchaseSearchReader>(MockBehavior.Strict);
		var result = new PurchaseSearchResult([], 0, 10, 10);
		using var cancellation = new CancellationTokenSource();
		reader.Setup(x => x.SearchAsync("milk", 10, 10, expectedOptions, cancellation.Token)).ReturnsAsync(result);
		(await new SearchPurchasesQueryHandler(reader.Object).Handle(new(" milk ", 10, 10, options), cancellation.Token)).Should().BeSameAs(result);
		reader.VerifyAll();
	}
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
		reader.Setup(x => x.SearchAsync("whole milk", 200, 25, new PurchaseSearchOptions(), cancellation.Token)).ReturnsAsync(expected);
		PurchaseSearchResult result = await new SearchPurchasesQueryHandler(reader.Object)
			.Handle(new("  whole milk  ", 200, 25), cancellation.Token);
		result.Should().BeSameAs(expected);
		reader.VerifyAll();
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public async Task IdentityHistory_AcceptsEmptyPhraseAndPreservesIdentityOptions(string? phrase)
	{
		var options = new PurchaseSearchOptions(Location: " Market ",
				NormalizedDescriptionId: Guid.NewGuid(), IncludeUnreviewed: true);
		new SearchPurchasesQueryValidator().Validate(new SearchPurchasesQuery(phrase, Options: options))
				.IsValid.Should().BeTrue();
		var reader = new Mock<IPurchaseSearchReader>(MockBehavior.Strict);
		var expected = new PurchaseSearchResult([], 0, 0, 25);
		var expectedOptions = options with { Location = "Market" };
		reader.Setup(x => x.SearchAsync("", 0, 25, expectedOptions,
						CancellationToken.None)).ReturnsAsync(expected);
		(await new SearchPurchasesQueryHandler(reader.Object).Handle(new(phrase, Options: options),
				CancellationToken.None)).Should().BeSameAs(expected);
		reader.VerifyAll();
	}

	[Theory]
	[InlineData("a")]
	[InlineData(" a ")]
	public void IdentityHistory_RejectsTooShortSuppliedPhrase(string phrase)
			=> new SearchPurchasesQueryValidator().Validate(new SearchPurchasesQuery(phrase,
					Options: new(NormalizedDescriptionId: Guid.NewGuid()))).IsValid.Should().BeFalse();
}
