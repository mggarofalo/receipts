using Application.Interfaces.Services;
using Application.Models.Ocr;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace VlmEval.Tests;

/// <summary>
/// Tests for <see cref="FixtureEvaluator"/> scoring helpers and the end-to-end
/// <see cref="FixtureEvaluator.EvaluateAsync(Fixture, CancellationToken)"/> flow.
///
/// Money tolerance is inclusive: <c>|delta| &lt;= MoneyTolerance</c> passes (RECEIPTS-634).
/// </summary>
public class FixtureEvaluatorTests
{
	#region DiffStore

	[Fact]
	public void DiffStore_SubstringMatch_ReturnsPass()
	{
		FieldDiff diff = FixtureEvaluator.DiffStore("Walmart", FieldConfidence<string>.High("Walmart Supercenter #1234"));

		diff.Field.Should().Be("store");
		diff.Status.Should().Be(DiffStatus.Pass);
		diff.Detail.Should().BeNull();
	}

	[Fact]
	public void DiffStore_CaseInsensitiveMatch_ReturnsPass()
	{
		FieldDiff diff = FixtureEvaluator.DiffStore("walmart", FieldConfidence<string>.High("WALMART SUPERCENTER"));

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffStore_NoSubstringMatch_ReturnsFail()
	{
		FieldDiff diff = FixtureEvaluator.DiffStore("Walmart", FieldConfidence<string>.High("Target Store"));

		diff.Status.Should().Be(DiffStatus.Fail);
		diff.Expected.Should().Be("Walmart");
		diff.Actual.Should().Be("Target Store");
		diff.Detail.Should().Contain("does not contain expected substring");
	}

	[Fact]
	public void DiffStore_MissingActualValue_ReturnsFail()
	{
		FieldDiff diff = FixtureEvaluator.DiffStore("Walmart", FieldConfidence<string>.None());

		diff.Status.Should().Be(DiffStatus.Fail);
		diff.Expected.Should().Be("Walmart");
		diff.Actual.Should().BeNull();
		diff.Detail.Should().Be("VLM did not extract store name");
	}

	[Fact]
	public void DiffStore_WhitespaceActualValue_ReturnsFail()
	{
		FieldDiff diff = FixtureEvaluator.DiffStore("Walmart", FieldConfidence<string>.High("   "));

		diff.Status.Should().Be(DiffStatus.Fail);
		diff.Detail.Should().Be("VLM did not extract store name");
	}

	[Fact]
	public void DiffStore_ExpectedNull_ReturnsNotDeclared()
	{
		FieldDiff diff = FixtureEvaluator.DiffStore(null, FieldConfidence<string>.High("Walmart"));

		diff.Status.Should().Be(DiffStatus.NotDeclared);
		diff.Expected.Should().BeNull();
		diff.Actual.Should().Be("Walmart");
	}

	#endregion

	#region DiffDate

	[Fact]
	public void DiffDate_ExactMatch_ReturnsPass()
	{
		DateOnly expected = new(2026, 1, 14);

		FieldDiff diff = FixtureEvaluator.DiffDate(expected, FieldConfidence<DateOnly>.High(expected));

		diff.Field.Should().Be("date");
		diff.Status.Should().Be(DiffStatus.Pass);
		diff.Expected.Should().Be("2026-01-14");
		diff.Actual.Should().Be("2026-01-14");
	}

	[Fact]
	public void DiffDate_Mismatch_ReturnsFail()
	{
		DateOnly expected = new(2026, 1, 14);
		DateOnly actual = new(2026, 1, 15);

		FieldDiff diff = FixtureEvaluator.DiffDate(expected, FieldConfidence<DateOnly>.High(actual));

		diff.Status.Should().Be(DiffStatus.Fail);
		diff.Expected.Should().Be("2026-01-14");
		diff.Actual.Should().Be("2026-01-15");
	}

	[Fact]
	public void DiffDate_NoneConfidenceActual_ReturnsFail()
	{
		DateOnly expected = new(2026, 1, 14);

		FieldDiff diff = FixtureEvaluator.DiffDate(expected, FieldConfidence<DateOnly>.None());

		diff.Status.Should().Be(DiffStatus.Fail);
		diff.Expected.Should().Be("2026-01-14");
		diff.Actual.Should().BeNull();
		diff.Detail.Should().Be("VLM did not extract date");
	}

	[Fact]
	public void DiffDate_LowConfidenceMatch_ReturnsPass()
	{
		// RECEIPTS-631: Low(date) is a real (uncertain) extracted date and should be
		// compared against expected. Only None() should short-circuit as "did not extract".
		DateOnly expected = new(2026, 1, 14);

		FieldDiff diff = FixtureEvaluator.DiffDate(expected, FieldConfidence<DateOnly>.Low(expected));

		diff.Status.Should().Be(DiffStatus.Pass);
		diff.Actual.Should().Be("2026-01-14");
	}

	[Fact]
	public void DiffDate_ExpectedNull_ReturnsNotDeclared()
	{
		DateOnly actual = new(2026, 1, 14);

		FieldDiff diff = FixtureEvaluator.DiffDate(null, FieldConfidence<DateOnly>.High(actual));

		diff.Status.Should().Be(DiffStatus.NotDeclared);
		diff.Expected.Should().BeNull();
		diff.Actual.Should().Be("2026-01-14");
	}

	[Fact]
	public void DiffDate_ExpectedNull_NoneConfidenceActual_FormatsActualAsNull()
	{
		// RECEIPTS-650: when the VLM did not extract a date, FieldConfidence<DateOnly>.None()
		// carries default(DateOnly). The NotDeclared branch must guard on IsPresent and emit
		// null for Actual, not the bogus "0001-01-01" placeholder.
		FieldDiff diff = FixtureEvaluator.DiffDate(null, FieldConfidence<DateOnly>.None());

		diff.Status.Should().Be(DiffStatus.NotDeclared);
		diff.Expected.Should().BeNull();
		diff.Actual.Should().BeNull();
		diff.Detail.Should().BeNull();
	}

	[Theory]
	[InlineData("de-DE")]
	[InlineData("fr-FR")]
	[InlineData("ja-JP")]
	public void DiffDate_FormatsAsInvariantYyyyMmDd_AcrossCultures(string cultureName)
	{
		// Guards against regression of RECEIPTS-649: the NotDeclared branch must use
		// invariant `yyyy-MM-dd` formatting like every other branch, regardless of host culture.
		System.Globalization.CultureInfo originalCulture = System.Globalization.CultureInfo.CurrentCulture;
		try
		{
			System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(cultureName);
			DateOnly actual = new(2026, 1, 14);

			FieldDiff diff = FixtureEvaluator.DiffDate(null, FieldConfidence<DateOnly>.High(actual));

			diff.Actual.Should().Be("2026-01-14");
		}
		finally
		{
			System.Globalization.CultureInfo.CurrentCulture = originalCulture;
		}
	}

	#endregion

	#region DiffMoney

	[Fact]
	public void DiffMoney_ExactMatch_ReturnsPass()
	{
		FieldDiff diff = FixtureEvaluator.DiffMoney("total", 70.43m, FieldConfidence<decimal>.High(70.43m));

		diff.Field.Should().Be("total");
		diff.Status.Should().Be(DiffStatus.Pass);
		diff.Expected.Should().Be("70.43");
		diff.Actual.Should().Be("70.43");
	}

	[Fact]
	public void DiffMoney_DeltaZero_ReturnsPass()
	{
		// delta = 0.00 < tolerance (0.01) → pass
		FieldDiff diff = FixtureEvaluator.DiffMoney("subtotal", 1.00m, FieldConfidence<decimal>.High(1.00m));

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffMoney_DeltaUnderHalfCent_ReturnsPass()
	{
		// delta = 0.005 < tolerance (0.01) → pass
		FieldDiff diff = FixtureEvaluator.DiffMoney("total", 1.000m, FieldConfidence<decimal>.High(1.005m));

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffMoney_DeltaJustUnderTolerance_ReturnsPass()
	{
		// delta = 0.009 < tolerance (0.01) → pass
		FieldDiff diff = FixtureEvaluator.DiffMoney("total", 1.000m, FieldConfidence<decimal>.High(1.009m));

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffMoney_DeltaExactlyOneCent_ReturnsPass()
	{
		// RECEIPTS-634: tolerance is inclusive — a delta of EXACTLY $0.01 must pass per the
		// README's "within $0.01 of expected" contract.
		FieldDiff diff = FixtureEvaluator.DiffMoney("total", 1.00m, FieldConfidence<decimal>.High(1.01m));

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffMoney_NegativeDeltaExactlyOneCent_ReturnsPass()
	{
		// RECEIPTS-634: |actual - expected| = $0.01 with actual below expected must also pass.
		FieldDiff diff = FixtureEvaluator.DiffMoney("subtotal", 1.00m, FieldConfidence<decimal>.High(0.99m));

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffMoney_DeltaOverTolerance_ReturnsFail()
	{
		// delta = 0.011 > tolerance (0.01) → fail
		FieldDiff diff = FixtureEvaluator.DiffMoney("total", 1.000m, FieldConfidence<decimal>.High(1.011m));

		diff.Status.Should().Be(DiffStatus.Fail);
		diff.Expected.Should().Be("1.00");
		diff.Actual.Should().Be("1.01");
	}

	[Fact]
	public void DiffMoney_HonorsCustomTolerance_DeltaUnderCustomBound_ReturnsPass()
	{
		// With a $0.05 tolerance, a $0.04 delta passes (where it would fail at default).
		FieldDiff diff = FixtureEvaluator.DiffMoney("total", 10.00m, FieldConfidence<decimal>.High(10.04m), tolerance: 0.05m);

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffMoney_HonorsCustomTolerance_DeltaOverCustomBound_ReturnsFail()
	{
		// With a $0.05 tolerance, a $0.06 delta fails.
		FieldDiff diff = FixtureEvaluator.DiffMoney("total", 10.00m, FieldConfidence<decimal>.High(10.06m), tolerance: 0.05m);

		diff.Status.Should().Be(DiffStatus.Fail);
	}

	[Fact]
	public void DiffMoney_NegativeDelta_UsesAbsoluteValue()
	{
		// actual is below expected — Math.Abs ensures it's still a small delta → pass
		FieldDiff diff = FixtureEvaluator.DiffMoney("subtotal", 1.000m, FieldConfidence<decimal>.High(0.995m));

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffMoney_NoneConfidenceActual_ReturnsFail()
	{
		FieldDiff diff = FixtureEvaluator.DiffMoney("total", 70.43m, FieldConfidence<decimal>.None());

		diff.Status.Should().Be(DiffStatus.Fail);
		diff.Expected.Should().Be("70.43");
		diff.Actual.Should().BeNull();
		diff.Detail.Should().Be("VLM did not extract total");
	}

	[Fact]
	public void DiffMoney_LowConfidenceActualMatchingExpected_ReturnsPass()
	{
		// RECEIPTS-631: a low-confidence extracted value (e.g. illegible total the VLM
		// transcribed uncertainly) must still be compared against the expected value.
		// Only None() should short-circuit to "did not extract".
		FieldDiff diff = FixtureEvaluator.DiffMoney("total", 70.43m, FieldConfidence<decimal>.Low(70.43m));

		diff.Status.Should().Be(DiffStatus.Pass);
		diff.Actual.Should().Be("70.43");
	}

	[Fact]
	public void DiffMoney_ExpectedNull_ReturnsNotDeclared_WithFormattedActual()
	{
		FieldDiff diff = FixtureEvaluator.DiffMoney("total", null, FieldConfidence<decimal>.High(70.43m));

		diff.Status.Should().Be(DiffStatus.NotDeclared);
		diff.Expected.Should().BeNull();
		diff.Actual.Should().Be("70.43");
	}

	[Fact]
	public void DiffMoney_ExpectedNull_NoneConfidenceActual_FormatsActualAsNull()
	{
		FieldDiff diff = FixtureEvaluator.DiffMoney("total", null, FieldConfidence<decimal>.None());

		diff.Status.Should().Be(DiffStatus.NotDeclared);
		diff.Actual.Should().BeNull();
	}

	#endregion

	#region DiffTaxLines

	[Fact]
	public void DiffTaxLines_SingleMatch_ReturnsPass()
	{
		List<ExpectedTaxLine> expected = [new ExpectedTaxLine { Amount = 0.75m }];
		List<ParsedTaxLine> actual =
		[
			new ParsedTaxLine(FieldConfidence<string>.High("Tax"), FieldConfidence<decimal>.High(0.75m))
		];

		FieldDiff diff = FixtureEvaluator.DiffTaxLines(expected, actual);

		diff.Field.Should().Be("taxLines");
		diff.Status.Should().Be(DiffStatus.Pass);
		diff.Detail.Should().BeNull();
	}

	[Fact]
	public void DiffTaxLines_MultipleAmounts_AllMatched_ReturnsPass()
	{
		List<ExpectedTaxLine> expected =
		[
			new ExpectedTaxLine { Amount = 0.75m },
			new ExpectedTaxLine { Amount = 1.20m },
		];
		List<ParsedTaxLine> actual =
		[
			new ParsedTaxLine(FieldConfidence<string>.High("State"), FieldConfidence<decimal>.High(0.75m)),
			new ParsedTaxLine(FieldConfidence<string>.High("County"), FieldConfidence<decimal>.High(1.20m)),
		];

		FieldDiff diff = FixtureEvaluator.DiffTaxLines(expected, actual);

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffTaxLines_MissingActualLine_ReturnsFail()
	{
		List<ExpectedTaxLine> expected = [new ExpectedTaxLine { Amount = 0.75m }];
		List<ParsedTaxLine> actual = [];

		FieldDiff diff = FixtureEvaluator.DiffTaxLines(expected, actual);

		diff.Status.Should().Be(DiffStatus.Fail);
		diff.Detail.Should().Contain("no tax line within $0.01 of $0.75");
	}

	[Fact]
	public void DiffTaxLines_ActualHasMoreLines_StillPasses()
	{
		// README: "actual may contain more lines"
		List<ExpectedTaxLine> expected = [new ExpectedTaxLine { Amount = 0.75m }];
		List<ParsedTaxLine> actual =
		[
			new ParsedTaxLine(FieldConfidence<string>.High("Tax"), FieldConfidence<decimal>.High(0.75m)),
			new ParsedTaxLine(FieldConfidence<string>.High("Other"), FieldConfidence<decimal>.High(2.50m)),
		];

		FieldDiff diff = FixtureEvaluator.DiffTaxLines(expected, actual);

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffTaxLines_DuplicateExpectedAmounts_ConsumesEachActualOnce()
	{
		// Expected has two 0.75 lines; actual has two 0.75 lines → both should match.
		List<ExpectedTaxLine> expected =
		[
			new ExpectedTaxLine { Amount = 0.75m },
			new ExpectedTaxLine { Amount = 0.75m },
		];
		List<ParsedTaxLine> actual =
		[
			new ParsedTaxLine(FieldConfidence<string>.High("Tax 1"), FieldConfidence<decimal>.High(0.75m)),
			new ParsedTaxLine(FieldConfidence<string>.High("Tax 2"), FieldConfidence<decimal>.High(0.75m)),
		];

		FieldDiff diff = FixtureEvaluator.DiffTaxLines(expected, actual);

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffTaxLines_DuplicateExpectedAmounts_OnlyOneActual_FailsForSecond()
	{
		// Two expected 0.75 lines but only one actual 0.75 line → second consumes index -1 → fail.
		List<ExpectedTaxLine> expected =
		[
			new ExpectedTaxLine { Amount = 0.75m },
			new ExpectedTaxLine { Amount = 0.75m },
		];
		List<ParsedTaxLine> actual =
		[
			new ParsedTaxLine(FieldConfidence<string>.High("Tax"), FieldConfidence<decimal>.High(0.75m)),
		];

		FieldDiff diff = FixtureEvaluator.DiffTaxLines(expected, actual);

		diff.Status.Should().Be(DiffStatus.Fail);
		diff.Detail.Should().Contain("no tax line within $0.01 of $0.75");
	}

	[Fact]
	public void DiffTaxLines_NoneConfidenceActualAmounts_AreIgnored()
	{
		// A None-confidence (absent) actual amount should not be available to match against.
		List<ExpectedTaxLine> expected = [new ExpectedTaxLine { Amount = 0.75m }];
		List<ParsedTaxLine> actual =
		[
			new ParsedTaxLine(FieldConfidence<string>.High("Tax"), FieldConfidence<decimal>.None()),
		];

		FieldDiff diff = FixtureEvaluator.DiffTaxLines(expected, actual);

		diff.Status.Should().Be(DiffStatus.Fail);
	}

	[Fact]
	public void DiffTaxLines_LowConfidenceActualAmount_IsStillMatched()
	{
		// RECEIPTS-631: Low(value) carries a real (if uncertain) amount and must be
		// considered when matching against expected tax lines. Only None() amounts
		// should be excluded from the matching pool.
		List<ExpectedTaxLine> expected = [new ExpectedTaxLine { Amount = 0.75m }];
		List<ParsedTaxLine> actual =
		[
			new ParsedTaxLine(FieldConfidence<string>.High("Tax"), FieldConfidence<decimal>.Low(0.75m)),
		];

		FieldDiff diff = FixtureEvaluator.DiffTaxLines(expected, actual);

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffTaxLines_ExpectedNull_ReturnsNotDeclared()
	{
		List<ParsedTaxLine> actual =
		[
			new ParsedTaxLine(FieldConfidence<string>.High("Tax"), FieldConfidence<decimal>.High(0.75m)),
		];

		FieldDiff diff = FixtureEvaluator.DiffTaxLines(null, actual);

		diff.Status.Should().Be(DiffStatus.NotDeclared);
		diff.Actual.Should().Be("actual=1");
	}

	[Fact]
	public void DiffTaxLines_ExpectedEmpty_ReturnsNotDeclared()
	{
		FieldDiff diff = FixtureEvaluator.DiffTaxLines([], []);

		diff.Status.Should().Be(DiffStatus.NotDeclared);
	}

	[Fact]
	public void DiffTaxLines_ExpectedAmountIsNull_IsSkipped()
	{
		// Lines with a null amount don't constitute an assertion → pass.
		List<ExpectedTaxLine> expected = [new ExpectedTaxLine { Label = "Sales", Amount = null }];
		List<ParsedTaxLine> actual = [];

		FieldDiff diff = FixtureEvaluator.DiffTaxLines(expected, actual);

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffTaxLines_DeltaExactlyOneCent_ReturnsPass()
	{
		// RECEIPTS-634: tax-line tolerance is inclusive too — $0.01 delta must pass.
		List<ExpectedTaxLine> expected = [new ExpectedTaxLine { Amount = 0.75m }];
		List<ParsedTaxLine> actual =
		[
			new ParsedTaxLine(FieldConfidence<string>.High("Tax"), FieldConfidence<decimal>.High(0.76m)),
		];

		FieldDiff diff = FixtureEvaluator.DiffTaxLines(expected, actual);

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffTaxLines_DuplicatePrices_GreedyByInputOrder_StableOnTies()
	{
		// Pin documented greedy-by-input-order behavior: when expected has two identical
		// amounts and pool has multiple matching candidates, FindClosest picks the earlier
		// index on tied deltas. The first expected consumes pool[0]; the second consumes pool[0]
		// after removal (i.e. the originally-at-index-1 entry).
		List<ExpectedTaxLine> expected =
		[
			new ExpectedTaxLine { Amount = 1.00m },
			new ExpectedTaxLine { Amount = 1.00m },
		];
		List<ParsedTaxLine> actual =
		[
			new ParsedTaxLine(FieldConfidence<string>.High("A"), FieldConfidence<decimal>.High(1.00m)),
			new ParsedTaxLine(FieldConfidence<string>.High("B"), FieldConfidence<decimal>.High(1.00m)),
			new ParsedTaxLine(FieldConfidence<string>.High("C"), FieldConfidence<decimal>.High(1.00m)),
		];

		FieldDiff diff = FixtureEvaluator.DiffTaxLines(expected, actual);

		// Both expected lines must find a match; behavior is deterministic.
		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffTaxLines_HonorsCustomTolerance_MultiplePassUnderCustomBound()
	{
		// With a $0.05 tolerance, a $0.04 delta passes.
		List<ExpectedTaxLine> expected = [new ExpectedTaxLine { Amount = 0.75m }];
		List<ParsedTaxLine> actual =
		[
			new ParsedTaxLine(FieldConfidence<string>.High("Tax"), FieldConfidence<decimal>.High(0.79m)),
		];

		FieldDiff diff = FixtureEvaluator.DiffTaxLines(expected, actual, tolerance: 0.05m);

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	#endregion

	#region DiffPaymentMethod

	[Fact]
	public void DiffPaymentMethod_SubstringMatch_ReturnsPass()
	{
		// README: "MASTERCARD" matches "MasterCard ****1234"
		FieldDiff diff = FixtureEvaluator.DiffPaymentMethod("MASTERCARD", FieldConfidence<string?>.High("MasterCard ****1234"));

		diff.Field.Should().Be("paymentMethod");
		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffPaymentMethod_NoMatch_ReturnsFail()
	{
		FieldDiff diff = FixtureEvaluator.DiffPaymentMethod("VISA", FieldConfidence<string?>.High("Cash"));

		diff.Status.Should().Be(DiffStatus.Fail);
		diff.Detail.Should().Contain("does not contain expected substring");
	}

	[Fact]
	public void DiffPaymentMethod_MissingActual_ReturnsFail()
	{
		FieldDiff diff = FixtureEvaluator.DiffPaymentMethod("VISA", FieldConfidence<string?>.None());

		diff.Status.Should().Be(DiffStatus.Fail);
		diff.Detail.Should().Be("VLM did not extract paymentMethod");
	}

	[Fact]
	public void DiffPaymentMethod_ExpectedNull_ReturnsNotDeclared()
	{
		FieldDiff diff = FixtureEvaluator.DiffPaymentMethod(null, FieldConfidence<string?>.High("VISA"));

		diff.Status.Should().Be(DiffStatus.NotDeclared);
	}

	#endregion

	#region DiffMinItemCount

	[Fact]
	public void DiffMinItemCount_ActualMeetsThreshold_ReturnsPass()
	{
		List<ParsedReceiptItem> items =
		[
			MakeItem("Apple", 1.00m),
			MakeItem("Bread", 2.00m),
			MakeItem("Cheese", 3.00m),
		];

		FieldDiff diff = FixtureEvaluator.DiffMinItemCount(3, items);

		diff.Status.Should().Be(DiffStatus.Pass);
		diff.Expected.Should().Be(">=3");
		diff.Actual.Should().Be("3");
	}

	[Fact]
	public void DiffMinItemCount_ActualExceedsThreshold_ReturnsPass()
	{
		List<ParsedReceiptItem> items =
		[
			MakeItem("Apple", 1.00m),
			MakeItem("Bread", 2.00m),
		];

		FieldDiff diff = FixtureEvaluator.DiffMinItemCount(1, items);

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffMinItemCount_ActualBelowThreshold_ReturnsFail()
	{
		List<ParsedReceiptItem> items = [MakeItem("Apple", 1.00m)];

		FieldDiff diff = FixtureEvaluator.DiffMinItemCount(3, items);

		diff.Status.Should().Be(DiffStatus.Fail);
		diff.Detail.Should().Contain("expected at least 3 items, got 1");
	}

	[Fact]
	public void DiffMinItemCount_ExpectedNull_ReturnsNotDeclared()
	{
		List<ParsedReceiptItem> items = [MakeItem("Apple", 1.00m)];

		FieldDiff diff = FixtureEvaluator.DiffMinItemCount(null, items);

		diff.Status.Should().Be(DiffStatus.NotDeclared);
		diff.Actual.Should().Be("1");
	}

	#endregion

	#region DiffSubtotalReconciliation

	[Fact]
	public void DiffSubtotalReconciliation_ExactMatch_ReturnsPass()
	{
		List<ParsedReceiptItem> items =
		[
			MakeItem("Apple", 1.00m),
			MakeItem("Bread", 2.00m),
			MakeItem("Cheese", 3.00m),
		];

		FieldDiff diff = FixtureEvaluator.DiffSubtotalReconciliation(FieldConfidence<decimal>.High(6.00m), items);

		diff.Field.Should().Be("subtotalReconciliation");
		diff.Status.Should().Be(DiffStatus.Pass);
		diff.Detail.Should().BeNull();
	}

	[Fact]
	public void DiffSubtotalReconciliation_DeltaWithinDefaultTolerance_ReturnsPass()
	{
		List<ParsedReceiptItem> items = [MakeItem("Apple", 1.00m)];

		// Default tolerance is $0.01 (inclusive). Delta of exactly $0.01 passes.
		FieldDiff diff = FixtureEvaluator.DiffSubtotalReconciliation(FieldConfidence<decimal>.High(1.01m), items);

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffSubtotalReconciliation_DeltaOverTolerance_ReturnsFail()
	{
		// Walmart 2026-01-14 (RECEIPTS-663): subtotal=$69.68, items sum to $69.57, delta=$0.11.
		List<ParsedReceiptItem> items =
		[
			MakeItem("Item A", 30.00m),
			MakeItem("Item B", 25.57m),
			MakeItem("Item C", 14.00m),
		];

		FieldDiff diff = FixtureEvaluator.DiffSubtotalReconciliation(FieldConfidence<decimal>.High(69.68m), items, tolerance: 0.05m);

		diff.Status.Should().Be(DiffStatus.Fail);
		diff.Actual.Should().Contain("delta=0.11");
		diff.Detail.Should().Contain("0.11");
	}

	[Fact]
	public void DiffSubtotalReconciliation_DeltaAtCustomToleranceBoundary_ReturnsPass()
	{
		// Inclusive bound: delta == tolerance passes (RECEIPTS-634 convention).
		List<ParsedReceiptItem> items = [MakeItem("Item", 10.00m)];

		FieldDiff diff = FixtureEvaluator.DiffSubtotalReconciliation(FieldConfidence<decimal>.High(10.05m), items, tolerance: 0.05m);

		diff.Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffSubtotalReconciliation_NoSubtotal_ReturnsNotDeclared()
	{
		List<ParsedReceiptItem> items = [MakeItem("Apple", 1.00m)];

		FieldDiff diff = FixtureEvaluator.DiffSubtotalReconciliation(FieldConfidence<decimal>.None(), items);

		diff.Status.Should().Be(DiffStatus.NotDeclared);
		diff.Detail.Should().Be("subtotal not extracted");
	}

	[Fact]
	public void DiffSubtotalReconciliation_NoItems_ReturnsNotDeclared()
	{
		FieldDiff diff = FixtureEvaluator.DiffSubtotalReconciliation(FieldConfidence<decimal>.High(10.00m), []);

		diff.Status.Should().Be(DiffStatus.NotDeclared);
		diff.Detail.Should().Be("no item totals extracted");
	}

	#endregion

	#region DiffItems

	[Fact]
	public void DiffItems_ExpectedNull_ReturnsEmpty()
	{
		List<FieldDiff> diffs = FixtureEvaluator.DiffItems(null, [MakeItem("Apple", 1.00m)]);

		diffs.Should().BeEmpty();
	}

	[Fact]
	public void DiffItems_ExpectedEmpty_ReturnsEmpty()
	{
		List<FieldDiff> diffs = FixtureEvaluator.DiffItems([], [MakeItem("Apple", 1.00m)]);

		diffs.Should().BeEmpty();
	}

	[Fact]
	public void DiffItems_PriceAndDescriptionBothNull_ReturnsNotDeclared()
	{
		List<ExpectedItem> expected = [new ExpectedItem()];
		List<ParsedReceiptItem> actual = [MakeItem("Apple", 1.00m)];

		List<FieldDiff> diffs = FixtureEvaluator.DiffItems(expected, actual);

		diffs.Should().HaveCount(1);
		diffs[0].Status.Should().Be(DiffStatus.NotDeclared);
		diffs[0].Field.Should().Be("items[0]");
	}

	[Fact]
	public void DiffItems_PriceMatch_ReturnsPass()
	{
		List<ExpectedItem> expected = [new ExpectedItem { Description = "Apple", TotalPrice = 1.00m }];
		List<ParsedReceiptItem> actual = [MakeItem("Apple", 1.00m)];

		List<FieldDiff> diffs = FixtureEvaluator.DiffItems(expected, actual);

		diffs.Should().HaveCount(1);
		diffs[0].Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffItems_PriceMatchPicksClosestPrice()
	{
		// Two candidate prices: 1.05 (delta 0.05) and 0.995 (delta 0.005). Both 0.995 and 1.005
		// would tie within tolerance now that the bound is inclusive (RECEIPTS-634), but 0.995 is
		// closer than 1.05 and should win.
		List<ExpectedItem> expected = [new ExpectedItem { Description = "Apple", TotalPrice = 1.000m }];
		List<ParsedReceiptItem> actual =
		[
			MakeItem("Bread", 1.05m),
			MakeItem("Apple", 0.995m),
		];

		List<FieldDiff> diffs = FixtureEvaluator.DiffItems(expected, actual);

		diffs[0].Status.Should().Be(DiffStatus.Pass);
		diffs[0].Actual.Should().Contain("Apple");
	}

	[Fact]
	public void DiffItems_PriceExactlyOneCentDelta_ReturnsPass()
	{
		// RECEIPTS-634: a $0.01 item-price delta must pass (the items-side off-by-one was line 252).
		List<ExpectedItem> expected = [new ExpectedItem { Description = "Apple", TotalPrice = 1.000m }];
		List<ParsedReceiptItem> actual = [MakeItem("Apple", 1.010m)];

		List<FieldDiff> diffs = FixtureEvaluator.DiffItems(expected, actual);

		diffs[0].Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffItems_PriceMatchedThenSecondaryValidationOneCent_ReturnsPass()
	{
		// RECEIPTS-634: when the price-match step finds a candidate, the subsequent in-line
		// totalPrice validation (formerly `>= MoneyTolerance` on line 301) must also use the
		// inclusive bound. Description fallback locks an item with a $0.01 delta — the secondary
		// check should not flag that as a mismatch.
		List<ExpectedItem> expected = [new ExpectedItem { Description = "milk", TotalPrice = 4.99m }];
		List<ParsedReceiptItem> actual = [MakeItem("Whole Milk Gallon", 5.00m)];

		List<FieldDiff> diffs = FixtureEvaluator.DiffItems(expected, actual);

		diffs[0].Status.Should().Be(DiffStatus.Pass);
		diffs[0].Detail.Should().BeNull();
	}

	[Fact]
	public void DiffItems_DescriptionFallback_FirstSubstringHitWins_DeterministicOrder()
	{
		// Pin the documented description-only fallback behavior: when no price is declared (or
		// no price within tolerance), the matcher takes the FIRST pool entry whose description
		// contains the expected substring. README's "Diff rules" calls this out explicitly.
		List<ExpectedItem> expected = [new ExpectedItem { Description = "milk" }];
		List<ParsedReceiptItem> actual =
		[
			MakeItem("Skim Milk Quart", 2.99m),
			MakeItem("Whole Milk Gallon", 4.99m),
		];

		List<FieldDiff> diffs = FixtureEvaluator.DiffItems(expected, actual);

		diffs[0].Status.Should().Be(DiffStatus.Pass);
		// First pool entry wins — Skim Milk Quart, NOT the longer/closer-overlap Whole Milk Gallon.
		diffs[0].Actual.Should().Contain("Skim Milk Quart");
	}

	[Fact]
	public void DiffItems_DuplicatePrices_GreedyByInputOrder_FirstExpectedConsumesFirstActual()
	{
		// Pin the greedy, input-order-stable matching for items with duplicate prices: the first
		// expected entry consumes the first matching pool entry (closest delta, earlier index on
		// ties). README "Matching algorithm" subsection documents this.
		List<ExpectedItem> expected =
		[
			new ExpectedItem { Description = "X", TotalPrice = 1.00m },
			new ExpectedItem { Description = "Y", TotalPrice = 1.00m },
		];
		List<ParsedReceiptItem> actual =
		[
			MakeItem("X", 1.00m),
			MakeItem("Y", 1.00m),
		];

		List<FieldDiff> diffs = FixtureEvaluator.DiffItems(expected, actual);

		diffs.Should().HaveCount(2);
		// X went to actual[0], Y went to actual[1]; both pass.
		diffs[0].Status.Should().Be(DiffStatus.Pass);
		diffs[0].Actual.Should().Contain("X");
		diffs[1].Status.Should().Be(DiffStatus.Pass);
		diffs[1].Actual.Should().Contain("Y");
	}

	[Fact]
	public void DiffItems_DescriptionOnlyFallback_ReturnsPass()
	{
		// No price declared → falls back to description-only substring match.
		List<ExpectedItem> expected = [new ExpectedItem { Description = "milk" }];
		List<ParsedReceiptItem> actual =
		[
			MakeItem("Whole Milk Gallon", 4.99m),
		];

		List<FieldDiff> diffs = FixtureEvaluator.DiffItems(expected, actual);

		diffs[0].Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffItems_PriceMismatch_FallsBackToDescription_ThenFailsPriceValidation()
	{
		// Price doesn't match any item, but description does. The description fallback locks
		// in the matched line, but the subsequent price validation still flags the mismatch
		// and the overall result is Fail (with a "totalPrice mismatch" detail).
		List<ExpectedItem> expected = [new ExpectedItem { Description = "milk", TotalPrice = 99.99m }];
		List<ParsedReceiptItem> actual =
		[
			MakeItem("Whole Milk Gallon", 4.99m),
		];

		List<FieldDiff> diffs = FixtureEvaluator.DiffItems(expected, actual);

		diffs[0].Status.Should().Be(DiffStatus.Fail);
		diffs[0].Detail.Should().Contain("totalPrice mismatch");
	}

	[Fact]
	public void DiffItems_NoMatch_ReturnsFail()
	{
		List<ExpectedItem> expected = [new ExpectedItem { Description = "Apple", TotalPrice = 1.00m }];
		List<ParsedReceiptItem> actual = [MakeItem("Bread", 5.00m)];

		List<FieldDiff> diffs = FixtureEvaluator.DiffItems(expected, actual);

		diffs[0].Status.Should().Be(DiffStatus.Fail);
		diffs[0].Detail.Should().Be("no matching line in VLM output");
	}

	[Fact]
	public void DiffItems_DescriptionMismatch_ReturnsFail()
	{
		// Price matches but description doesn't → reports description mismatch.
		List<ExpectedItem> expected = [new ExpectedItem { Description = "Apple", TotalPrice = 1.000m }];
		List<ParsedReceiptItem> actual = [MakeItem("Bread", 1.000m)];

		List<FieldDiff> diffs = FixtureEvaluator.DiffItems(expected, actual);

		diffs[0].Status.Should().Be(DiffStatus.Fail);
		diffs[0].Detail.Should().Contain("description mismatch");
	}

	[Fact]
	public void DiffItems_NoneConfidenceTotalPrice_NotMatchedByPrice()
	{
		// Items with absent (None) prices are skipped by the price matcher.
		// The fallback description matcher then finds them by description.
		List<ExpectedItem> expected = [new ExpectedItem { Description = "Apple", TotalPrice = 1.000m }];
		List<ParsedReceiptItem> actual =
		[
			new ParsedReceiptItem(
				Code: FieldConfidence<string?>.None(),
				Description: FieldConfidence<string>.High("Apple"),
				Quantity: FieldConfidence<decimal>.High(1m),
				UnitPrice: FieldConfidence<decimal>.None(),
				TotalPrice: FieldConfidence<decimal>.None()),
		];

		List<FieldDiff> diffs = FixtureEvaluator.DiffItems(expected, actual);

		// Price matcher skips the item, description fallback finds it, but then the
		// "is the matched item's totalPrice present?" check reports a missing price.
		diffs[0].Status.Should().Be(DiffStatus.Fail);
		diffs[0].Detail.Should().Contain("missing totalPrice");
	}

	[Fact]
	public void DiffItems_LowConfidenceTotalPriceMatchingExpected_ReturnsPass()
	{
		// RECEIPTS-631: a low-confidence extracted item price is real data and must be
		// matched. The previous behavior incorrectly skipped such items.
		List<ExpectedItem> expected = [new ExpectedItem { Description = "Apple", TotalPrice = 1.000m }];
		List<ParsedReceiptItem> actual =
		[
			new ParsedReceiptItem(
				Code: FieldConfidence<string?>.None(),
				Description: FieldConfidence<string>.High("Apple"),
				Quantity: FieldConfidence<decimal>.High(1m),
				UnitPrice: FieldConfidence<decimal>.High(1m),
				TotalPrice: FieldConfidence<decimal>.Low(1.000m)),
		];

		List<FieldDiff> diffs = FixtureEvaluator.DiffItems(expected, actual);

		diffs[0].Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffItems_PoolItemConsumedOnFirstMatch_ReturnsExpectedDiffs()
	{
		// Two expected lines both expecting Apple/1.00. Actual has two Apples at 1.00.
		// First should consume actual[0]; second should consume actual[1].
		List<ExpectedItem> expected =
		[
			new ExpectedItem { Description = "Apple", TotalPrice = 1.000m },
			new ExpectedItem { Description = "Apple", TotalPrice = 1.000m },
		];
		List<ParsedReceiptItem> actual =
		[
			MakeItem("Apple A", 1.000m),
			MakeItem("Apple B", 1.000m),
		];

		List<FieldDiff> diffs = FixtureEvaluator.DiffItems(expected, actual);

		diffs.Should().HaveCount(2);
		diffs[0].Status.Should().Be(DiffStatus.Pass);
		diffs[1].Status.Should().Be(DiffStatus.Pass);
	}

	[Fact]
	public void DiffItems_PoolExhausted_SecondExpectedFails()
	{
		// Two expected, only one actual.
		List<ExpectedItem> expected =
		[
			new ExpectedItem { Description = "Apple", TotalPrice = 1.000m },
			new ExpectedItem { Description = "Apple", TotalPrice = 1.000m },
		];
		List<ParsedReceiptItem> actual = [MakeItem("Apple", 1.000m)];

		List<FieldDiff> diffs = FixtureEvaluator.DiffItems(expected, actual);

		diffs.Should().HaveCount(2);
		diffs[0].Status.Should().Be(DiffStatus.Pass);
		diffs[1].Status.Should().Be(DiffStatus.Fail);
		diffs[1].Detail.Should().Be("no matching line in VLM output");
	}

	#endregion

	#region EvaluateAsync end-to-end

	[Fact]
	public async Task EvaluateAsync_FileMissing_ReturnsFailWithReadError()
	{
		Mock<IReceiptExtractionService> service = new();
		FixtureEvaluator evaluator = new(service.Object, new VlmEvalOptions(), NullLogger<FixtureEvaluator>.Instance);

		Fixture fixture = new(
			Name: "missing.jpg",
			FilePath: Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-does-not-exist.jpg"),
			ContentType: "image/jpeg",
			Expected: new ExpectedReceipt());

		FixtureResult result = await evaluator.EvaluateAsync(fixture, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Error.Should().StartWith("Failed to read fixture file");
		service.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task EvaluateAsync_ExtractionThrows_ReturnsFailWithVlmError()
	{
		string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
		await File.WriteAllBytesAsync(tempFile, [0x01, 0x02, 0x03]);
		try
		{
			Mock<IReceiptExtractionService> service = new();
			service
				.Setup(s => s.ExtractAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
				.ThrowsAsync(new InvalidOperationException("VLM down"));

			FixtureEvaluator evaluator = new(service.Object, new VlmEvalOptions(), NullLogger<FixtureEvaluator>.Instance);

			Fixture fixture = new("test.jpg", tempFile, "image/jpeg", new ExpectedReceipt());

			FixtureResult result = await evaluator.EvaluateAsync(fixture, CancellationToken.None);

			result.Passed.Should().BeFalse();
			result.Error.Should().Contain("VLM call failed");
			result.Error.Should().Contain("InvalidOperationException");
		}
		finally
		{
			File.Delete(tempFile);
		}
	}

	[Fact]
	public async Task EvaluateAsync_AllAssertionsPass_ReturnsPassed()
	{
		string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
		await File.WriteAllBytesAsync(tempFile, [0x01, 0x02, 0x03]);
		try
		{
			ParsedReceipt parsed = new(
				StoreName: FieldConfidence<string>.High("Walmart Supercenter"),
				Date: FieldConfidence<DateOnly>.High(new DateOnly(2026, 1, 14)),
				Items: [MakeItem("Apple", 1.00m)],
				Subtotal: FieldConfidence<decimal>.High(1.00m),
				TaxLines: [new ParsedTaxLine(FieldConfidence<string>.High("State"), FieldConfidence<decimal>.High(0.08m))],
				Total: FieldConfidence<decimal>.High(1.08m),
				PaymentMethod: FieldConfidence<string?>.High("VISA ****1111"));

			Mock<IReceiptExtractionService> service = new();
			service
				.Setup(s => s.ExtractAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
				.ReturnsAsync(parsed);

			FixtureEvaluator evaluator = new(service.Object, new VlmEvalOptions(), NullLogger<FixtureEvaluator>.Instance);

			Fixture fixture = new(
				Name: "test.jpg",
				FilePath: tempFile,
				ContentType: "image/jpeg",
				Expected: new ExpectedReceipt
				{
					Store = "Walmart",
					Date = new DateOnly(2026, 1, 14),
					Subtotal = 1.00m,
					Total = 1.08m,
					TaxLines = [new ExpectedTaxLine { Amount = 0.08m }],
					PaymentMethod = "VISA",
					MinItemCount = 1,
					Items = [new ExpectedItem { Description = "Apple", TotalPrice = 1.00m }],
				});

			FixtureResult result = await evaluator.EvaluateAsync(fixture, CancellationToken.None);

			result.Passed.Should().BeTrue();
			result.Error.Should().BeNull();
			result.FieldDiffs.Should().NotBeEmpty();
			result.FieldDiffs.Should().NotContain(d => d.Status == DiffStatus.Fail);
		}
		finally
		{
			File.Delete(tempFile);
		}
	}

	[Fact]
	public async Task EvaluateAsync_OnlyUndeclaredFields_ReturnsPassed_AllNotDeclared()
	{
		// An empty ExpectedReceipt (nothing declared) should pass — every diff is NotDeclared.
		string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
		await File.WriteAllBytesAsync(tempFile, [0x01, 0x02, 0x03]);
		try
		{
			ParsedReceipt parsed = new(
				StoreName: FieldConfidence<string>.None(),
				Date: FieldConfidence<DateOnly>.None(),
				Items: [],
				Subtotal: FieldConfidence<decimal>.None(),
				TaxLines: [],
				Total: FieldConfidence<decimal>.None(),
				PaymentMethod: FieldConfidence<string?>.None());

			Mock<IReceiptExtractionService> service = new();
			service
				.Setup(s => s.ExtractAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
				.ReturnsAsync(parsed);

			FixtureEvaluator evaluator = new(service.Object, new VlmEvalOptions(), NullLogger<FixtureEvaluator>.Instance);

			Fixture fixture = new("test.jpg", tempFile, "image/jpeg", new ExpectedReceipt());

			FixtureResult result = await evaluator.EvaluateAsync(fixture, CancellationToken.None);

			result.Passed.Should().BeTrue();
			result.FieldDiffs.Should().OnlyContain(d => d.Status == DiffStatus.NotDeclared);
		}
		finally
		{
			File.Delete(tempFile);
		}
	}

	[Fact]
	public async Task EvaluateAsync_OneAssertionFails_OverallResultFails()
	{
		string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
		await File.WriteAllBytesAsync(tempFile, [0x01, 0x02, 0x03]);
		try
		{
			ParsedReceipt parsed = new(
				StoreName: FieldConfidence<string>.High("Target"), // expected Walmart → fail
				Date: FieldConfidence<DateOnly>.None(),
				Items: [],
				Subtotal: FieldConfidence<decimal>.None(),
				TaxLines: [],
				Total: FieldConfidence<decimal>.None(),
				PaymentMethod: FieldConfidence<string?>.None());

			Mock<IReceiptExtractionService> service = new();
			service
				.Setup(s => s.ExtractAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
				.ReturnsAsync(parsed);

			FixtureEvaluator evaluator = new(service.Object, new VlmEvalOptions(), NullLogger<FixtureEvaluator>.Instance);

			Fixture fixture = new(
				"test.jpg",
				tempFile,
				"image/jpeg",
				new ExpectedReceipt { Store = "Walmart" });

			FixtureResult result = await evaluator.EvaluateAsync(fixture, CancellationToken.None);

			result.Passed.Should().BeFalse();
			result.FieldDiffs.Should().Contain(d => d.Field == "store" && d.Status == DiffStatus.Fail);
		}
		finally
		{
			File.Delete(tempFile);
		}
	}

	[Fact]
	public async Task EvaluateAsync_OptionsTolerance_AppliedToMoneyDiffs()
	{
		// VlmEvalOptions.MoneyTolerance widens the bound for an entire run. With a $0.05 default,
		// a $0.04 subtotal delta must pass.
		string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
		await File.WriteAllBytesAsync(tempFile, [0x01, 0x02, 0x03]);
		try
		{
			ParsedReceipt parsed = new(
				StoreName: FieldConfidence<string>.High("Walmart"),
				Date: FieldConfidence<DateOnly>.None(),
				Items: [],
				Subtotal: FieldConfidence<decimal>.High(10.04m),
				TaxLines: [],
				Total: FieldConfidence<decimal>.None(),
				PaymentMethod: FieldConfidence<string?>.None());

			Mock<IReceiptExtractionService> service = new();
			service
				.Setup(s => s.ExtractAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
				.ReturnsAsync(parsed);

			FixtureEvaluator evaluator = new(
				service.Object,
				new VlmEvalOptions { MoneyTolerance = 0.05m },
				NullLogger<FixtureEvaluator>.Instance);

			Fixture fixture = new(
				"test.jpg",
				tempFile,
				"image/jpeg",
				new ExpectedReceipt { Store = "Walmart", Subtotal = 10.00m });

			FixtureResult result = await evaluator.EvaluateAsync(fixture, CancellationToken.None);

			result.Passed.Should().BeTrue();
			result.FieldDiffs.Should().Contain(d => d.Field == "subtotal" && d.Status == DiffStatus.Pass);
		}
		finally
		{
			File.Delete(tempFile);
		}
	}

	[Fact]
	public async Task EvaluateAsync_SidecarToleranceOverride_TakesPrecedenceOverOptions()
	{
		// The sidecar's MoneyTolerance overrides the run-wide default for that single fixture.
		// Run-wide is $0.01 (would fail $0.04); sidecar pushes it to $0.05 → pass.
		string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
		await File.WriteAllBytesAsync(tempFile, [0x01, 0x02, 0x03]);
		try
		{
			ParsedReceipt parsed = new(
				StoreName: FieldConfidence<string>.High("Walmart"),
				Date: FieldConfidence<DateOnly>.None(),
				Items: [],
				Subtotal: FieldConfidence<decimal>.High(10.04m),
				TaxLines: [],
				Total: FieldConfidence<decimal>.None(),
				PaymentMethod: FieldConfidence<string?>.None());

			Mock<IReceiptExtractionService> service = new();
			service
				.Setup(s => s.ExtractAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
				.ReturnsAsync(parsed);

			FixtureEvaluator evaluator = new(
				service.Object,
				new VlmEvalOptions { MoneyTolerance = 0.01m },
				NullLogger<FixtureEvaluator>.Instance);

			Fixture fixture = new(
				"test.jpg",
				tempFile,
				"image/jpeg",
				new ExpectedReceipt
				{
					Store = "Walmart",
					Subtotal = 10.00m,
					MoneyTolerance = 0.05m,
				});

			FixtureResult result = await evaluator.EvaluateAsync(fixture, CancellationToken.None);

			result.Passed.Should().BeTrue();
			result.FieldDiffs.Should().Contain(d => d.Field == "subtotal" && d.Status == DiffStatus.Pass);
		}
		finally
		{
			File.Delete(tempFile);
		}
	}

	#endregion

	private static ParsedReceiptItem MakeItem(string description, decimal totalPrice) =>
		new(
			Code: FieldConfidence<string?>.None(),
			Description: FieldConfidence<string>.High(description),
			Quantity: FieldConfidence<decimal>.High(1m),
			UnitPrice: FieldConfidence<decimal>.High(totalPrice),
			TotalPrice: FieldConfidence<decimal>.High(totalPrice));
}
