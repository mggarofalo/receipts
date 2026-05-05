using System.Diagnostics;
using System.Globalization;
using Application.Interfaces.Services;
using Application.Models.Ocr;
using Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace VlmEval;

public sealed class FixtureEvaluator(
	IReceiptExtractionService extractionService,
	VlmEvalOptions options,
	ILogger<FixtureEvaluator> logger)
{
	/// <summary>
	/// Default money comparison tolerance used when no <see cref="VlmEvalOptions"/> or sidecar
	/// override is provided. Kept as a constant so the static diff helpers (called by tests)
	/// have a deterministic default. Comparisons are inclusive: <c>|delta| &lt;= tolerance</c>.
	/// </summary>
	internal const decimal DefaultMoneyTolerance = 0.01m;

	public async Task<FixtureResult> EvaluateAsync(Fixture fixture, CancellationToken cancellationToken)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();

		byte[] bytes;
		try
		{
			bytes = await File.ReadAllBytesAsync(fixture.FilePath, cancellationToken);
		}
		catch (Exception ex)
		{
			return new FixtureResult(fixture.Name, false, stopwatch.Elapsed, [], $"Failed to read fixture file: {ex.Message}");
		}

		ParsedReceipt parsed;
		try
		{
			parsed = await extractionService.ExtractAsync(bytes, cancellationToken);
		}
		catch (Exception ex)
		{
			stopwatch.Stop();
			logger.LogError(ex, "VLM call failed for {Fixture}", fixture.Name);
			return new FixtureResult(fixture.Name, false, stopwatch.Elapsed, [], $"VLM call failed: {ex.GetType().Name}: {ex.Message}");
		}

		stopwatch.Stop();

		decimal tolerance = fixture.Expected.MoneyTolerance ?? options.MoneyTolerance;

		List<FieldDiff> diffs = [];
		diffs.Add(DiffStore(fixture.Expected.Store, parsed.StoreName));
		diffs.Add(DiffDate(fixture.Expected.Date, parsed.Date));
		diffs.Add(DiffMoney("subtotal", fixture.Expected.Subtotal, parsed.Subtotal, tolerance));
		diffs.Add(DiffMoney("total", fixture.Expected.Total, parsed.Total, tolerance));
		// Use the production reconciliation threshold (RECEIPTS-663) rather than the per-fixture
		// money tolerance: the eval check should mirror the live confidence-downgrade rule, not
		// the unrelated subtotal/total comparison tolerance.
		diffs.Add(DiffSubtotalReconciliation(
			parsed.Subtotal,
			parsed.Items,
			OllamaReceiptExtractionService.SubtotalReconciliationTolerance));
		diffs.Add(DiffTaxLines(fixture.Expected.TaxLines, parsed.TaxLines, tolerance));
		diffs.Add(DiffPaymentMethod(fixture.Expected.PaymentMethod, parsed.PaymentMethod));
		diffs.Add(DiffMinItemCount(fixture.Expected.MinItemCount, parsed.Items));
		diffs.AddRange(DiffItems(fixture.Expected.Items, parsed.Items, tolerance));

		bool allDeclaredPassed = diffs.All(d => d.Status != DiffStatus.Fail);

		return new FixtureResult(fixture.Name, allDeclaredPassed, stopwatch.Elapsed, diffs, Error: null);
	}

	internal static FieldDiff DiffStore(string? expected, FieldConfidence<string> actual)
	{
		if (expected is null)
		{
			return new FieldDiff("store", DiffStatus.NotDeclared, null, actual.Value, null);
		}

		if (!actual.IsPresent || string.IsNullOrWhiteSpace(actual.Value))
		{
			return new FieldDiff("store", DiffStatus.Fail, expected, null, "VLM did not extract store name");
		}

		bool match = actual.Value.Contains(expected, StringComparison.OrdinalIgnoreCase);
		return new FieldDiff(
			"store",
			match ? DiffStatus.Pass : DiffStatus.Fail,
			expected,
			actual.Value,
			match ? null : "store name does not contain expected substring");
	}

	internal static FieldDiff DiffDate(DateOnly? expected, FieldConfidence<DateOnly> actual)
	{
		if (expected is null)
		{
			// Confidence guard mirrors DiffMoney's Format() helper (RECEIPTS-650): when the VLM
			// did not extract a date, FieldConfidence<DateOnly>.None() carries default(DateOnly),
			// so unconditionally formatting it would emit "0001-01-01" — emit null instead.
			string? actualStr = actual.IsPresent
				? actual.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
				: null;
			return new FieldDiff("date", DiffStatus.NotDeclared, null, actualStr, null);
		}

		if (!actual.IsPresent)
		{
			return new FieldDiff("date", DiffStatus.Fail, expected.Value.ToString("yyyy-MM-dd"), null, "VLM did not extract date");
		}

		bool match = actual.Value == expected.Value;
		return new FieldDiff(
			"date",
			match ? DiffStatus.Pass : DiffStatus.Fail,
			expected.Value.ToString("yyyy-MM-dd"),
			actual.Value.ToString("yyyy-MM-dd"),
			null);
	}

	internal static FieldDiff DiffMoney(string field, decimal? expected, FieldConfidence<decimal> actual, decimal tolerance = DefaultMoneyTolerance)
	{
		if (expected is null)
		{
			return new FieldDiff(field, DiffStatus.NotDeclared, null, Format(actual), null);
		}

		if (!actual.IsPresent)
		{
			return new FieldDiff(field, DiffStatus.Fail, expected.Value.ToString("0.00", CultureInfo.InvariantCulture), null, $"VLM did not extract {field}");
		}

		decimal delta = Math.Abs(actual.Value - expected.Value);
		bool match = delta <= tolerance;
		return new FieldDiff(
			field,
			match ? DiffStatus.Pass : DiffStatus.Fail,
			expected.Value.ToString("0.00", CultureInfo.InvariantCulture),
			actual.Value.ToString("0.00", CultureInfo.InvariantCulture),
			match ? null : $"delta=${delta.ToString("0.00", CultureInfo.InvariantCulture)}");

		static string? Format(FieldConfidence<decimal> f) =>
			f.IsPresent ? f.Value.ToString("0.00", CultureInfo.InvariantCulture) : null;
	}

	internal static FieldDiff DiffTaxLines(List<ExpectedTaxLine>? expected, List<ParsedTaxLine> actual, decimal tolerance = DefaultMoneyTolerance)
	{
		if (expected is null || expected.Count == 0)
		{
			return new FieldDiff("taxLines", DiffStatus.NotDeclared, null, $"actual={actual.Count}", null);
		}

		// Greedy, input-order-stable matching: expected tax lines are matched against the actual
		// pool in declaration order. Each pass picks the closest still-unmatched actual amount
		// within `tolerance` and removes it from the pool. With duplicate prices the first
		// expected line consumes the first matching actual, so behavior is deterministic but
		// not optimal (no global assignment / Hungarian algorithm). Documented in README.
		List<decimal> actualAmounts = [.. actual
			.Where(t => t.Amount.IsPresent)
			.Select(t => t.Amount.Value)];

		List<string> failures = [];
		List<string> matched = [];
		foreach (ExpectedTaxLine declared in expected)
		{
			if (declared.Amount is null)
			{
				continue;
			}

			decimal wanted = declared.Amount.Value;
			int idx = FindClosest(actualAmounts, wanted, tolerance);
			if (idx < 0)
			{
				failures.Add($"no tax line within ${tolerance.ToString("0.00", CultureInfo.InvariantCulture)} of ${wanted.ToString("0.00", CultureInfo.InvariantCulture)}");
				continue;
			}

			matched.Add(actualAmounts[idx].ToString("0.00", CultureInfo.InvariantCulture));
			actualAmounts.RemoveAt(idx);
		}

		bool pass = failures.Count == 0;
		return new FieldDiff(
			"taxLines",
			pass ? DiffStatus.Pass : DiffStatus.Fail,
			string.Join(", ", expected.Where(t => t.Amount is not null).Select(t => t.Amount!.Value.ToString("0.00", CultureInfo.InvariantCulture))),
			string.Join(", ", actual.Where(t => t.Amount.IsPresent).Select(t => t.Amount.Value.ToString("0.00", CultureInfo.InvariantCulture))),
			pass ? null : string.Join("; ", failures));
	}

	private static int FindClosest(IList<decimal> pool, decimal target, decimal tolerance)
	{
		int bestIndex = -1;
		decimal bestDelta = decimal.MaxValue;
		for (int i = 0; i < pool.Count; i++)
		{
			decimal delta = Math.Abs(pool[i] - target);
			// Inclusive bound (delta == tolerance must pass) — see RECEIPTS-634. On ties (delta
			// equality with bestDelta) the earlier index wins because we use `<` here, which makes
			// the result stable and input-order-deterministic for duplicates in the pool.
			if (delta <= tolerance && delta < bestDelta)
			{
				bestDelta = delta;
				bestIndex = i;
			}
		}
		return bestIndex;
	}

	internal static FieldDiff DiffPaymentMethod(string? expected, FieldConfidence<string?> actual)
	{
		if (expected is null)
		{
			return new FieldDiff("paymentMethod", DiffStatus.NotDeclared, null, actual.Value, null);
		}

		if (!actual.IsPresent || string.IsNullOrWhiteSpace(actual.Value))
		{
			return new FieldDiff("paymentMethod", DiffStatus.Fail, expected, null, "VLM did not extract paymentMethod");
		}

		bool match = actual.Value.Contains(expected, StringComparison.OrdinalIgnoreCase);
		return new FieldDiff(
			"paymentMethod",
			match ? DiffStatus.Pass : DiffStatus.Fail,
			expected,
			actual.Value,
			match ? null : "payment method does not contain expected substring");
	}

	/// <summary>
	/// Cross-check that the VLM's extracted <c>subtotal</c> agrees with the sum of
	/// <c>items[].totalPrice</c> within <paramref name="tolerance"/>. Fires for every fixture
	/// (no per-sidecar opt-in). Returns <see cref="DiffStatus.NotDeclared"/> when either side
	/// of the equation is missing — the assertion only makes sense when both are present.
	/// See RECEIPTS-663.
	/// </summary>
	internal static FieldDiff DiffSubtotalReconciliation(FieldConfidence<decimal> subtotal, List<ParsedReceiptItem> items, decimal tolerance = DefaultMoneyTolerance)
	{
		if (!subtotal.IsPresent)
		{
			return new FieldDiff("subtotalReconciliation", DiffStatus.NotDeclared, null, null, "subtotal not extracted");
		}

		List<decimal> totals = [.. items
			.Where(i => i.TotalPrice.IsPresent)
			.Select(i => i.TotalPrice.Value)];

		if (totals.Count == 0)
		{
			return new FieldDiff("subtotalReconciliation", DiffStatus.NotDeclared, null, null, "no item totals extracted");
		}

		decimal sum = totals.Sum();
		decimal delta = Math.Abs(subtotal.Value - sum);
		bool match = delta <= tolerance;
		string expected = $"|subtotal - sum(items)| <= {tolerance.ToString("0.00", CultureInfo.InvariantCulture)}";
		string actual = $"subtotal={subtotal.Value.ToString("0.00", CultureInfo.InvariantCulture)}, sum={sum.ToString("0.00", CultureInfo.InvariantCulture)}, delta={delta.ToString("0.00", CultureInfo.InvariantCulture)}";
		return new FieldDiff(
			"subtotalReconciliation",
			match ? DiffStatus.Pass : DiffStatus.Fail,
			expected,
			actual,
			match ? null : $"delta=${delta.ToString("0.00", CultureInfo.InvariantCulture)}");
	}

	internal static FieldDiff DiffMinItemCount(int? expected, List<ParsedReceiptItem> actual)
	{
		if (expected is null)
		{
			return new FieldDiff("minItemCount", DiffStatus.NotDeclared, null, actual.Count.ToString(CultureInfo.InvariantCulture), null);
		}

		bool match = actual.Count >= expected.Value;
		return new FieldDiff(
			"minItemCount",
			match ? DiffStatus.Pass : DiffStatus.Fail,
			$">={expected.Value}",
			actual.Count.ToString(CultureInfo.InvariantCulture),
			match ? null : $"expected at least {expected.Value} items, got {actual.Count}");
	}

	internal static List<FieldDiff> DiffItems(List<ExpectedItem>? expected, List<ParsedReceiptItem> actual, decimal tolerance = DefaultMoneyTolerance)
	{
		if (expected is null || expected.Count == 0)
		{
			return [];
		}

		// Matching strategy (greedy, deterministic — see README "Diff rules"):
		//   1. If expected has a totalPrice, match against the unmatched pool entry with the
		//      closest price within `tolerance`.
		//   2. If no price match (or expected has no price), fall back to the FIRST pool entry
		//      whose description contains expected.Description (substring, case-insensitive).
		//   3. The matched pool entry is removed so it cannot be matched again.
		// Item-list ordering of `expected` matters: earlier entries claim pool entries first.
		List<ParsedReceiptItem> pool = [.. actual];
		List<FieldDiff> results = [];

		for (int i = 0; i < expected.Count; i++)
		{
			ExpectedItem item = expected[i];
			string fieldName = $"items[{i}]";

			if (item.TotalPrice is null && item.Description is null)
			{
				results.Add(new FieldDiff(fieldName, DiffStatus.NotDeclared, null, null, null));
				continue;
			}

			int matchedIndex = -1;
			if (item.TotalPrice is not null)
			{
				decimal target = item.TotalPrice.Value;
				decimal best = decimal.MaxValue;
				for (int p = 0; p < pool.Count; p++)
				{
					if (!pool[p].TotalPrice.IsPresent)
					{
						continue;
					}
					decimal delta = Math.Abs(pool[p].TotalPrice.Value - target);
					// Inclusive bound — RECEIPTS-634. Tie-break by earlier index.
					if (delta <= tolerance && delta < best)
					{
						best = delta;
						matchedIndex = p;
					}
				}
			}

			if (matchedIndex < 0 && item.Description is not null)
			{
				// First-substring-hit fallback. Documented in README "Diff rules".
				for (int p = 0; p < pool.Count; p++)
				{
					if (!string.IsNullOrWhiteSpace(pool[p].Description.Value)
						&& pool[p].Description.Value!.Contains(item.Description, StringComparison.OrdinalIgnoreCase))
					{
						matchedIndex = p;
						break;
					}
				}
			}

			if (matchedIndex < 0)
			{
				results.Add(new FieldDiff(
					fieldName,
					DiffStatus.Fail,
					FormatExpectedItem(item),
					null,
					"no matching line in VLM output"));
				continue;
			}

			ParsedReceiptItem matched = pool[matchedIndex];
			pool.RemoveAt(matchedIndex);

			List<string> issues = [];
			if (item.Description is not null
				&& (matched.Description.Value is null
					|| !matched.Description.Value.Contains(item.Description, StringComparison.OrdinalIgnoreCase)))
			{
				issues.Add($"description mismatch (expected='{item.Description}', actual='{matched.Description.Value}')");
			}

			if (item.TotalPrice is not null)
			{
				if (!matched.TotalPrice.IsPresent)
				{
					issues.Add("missing totalPrice");
				}
				else if (Math.Abs(matched.TotalPrice.Value - item.TotalPrice.Value) > tolerance)
				{
					issues.Add($"totalPrice mismatch (expected={item.TotalPrice.Value:0.00}, actual={matched.TotalPrice.Value:0.00})");
				}
			}

			results.Add(new FieldDiff(
				fieldName,
				issues.Count == 0 ? DiffStatus.Pass : DiffStatus.Fail,
				FormatExpectedItem(item),
				FormatActualItem(matched),
				issues.Count == 0 ? null : string.Join("; ", issues)));
		}

		return results;
	}

	private static string FormatExpectedItem(ExpectedItem item)
	{
		string desc = item.Description ?? "*";
		string price = item.TotalPrice?.ToString("0.00", CultureInfo.InvariantCulture) ?? "*";
		return $"{desc} @ {price}";
	}

	private static string FormatActualItem(ParsedReceiptItem item)
	{
		string desc = item.Description.Value ?? "?";
		string price = item.TotalPrice.IsPresent
			? item.TotalPrice.Value.ToString("0.00", CultureInfo.InvariantCulture)
			: "?";
		return $"{desc} @ {price}";
	}
}
