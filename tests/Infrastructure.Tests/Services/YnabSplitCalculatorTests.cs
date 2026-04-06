using Application.Interfaces.Services;
using Common;
using Domain;
using Domain.Aggregates;
using Domain.Core;
using FluentAssertions;
using Infrastructure.Services;

namespace Infrastructure.Tests.Services;

public class YnabSplitCalculatorTests
{
	private readonly YnabSplitCalculator _calculator = new();

	// ── Helpers ─────────────────────────────────────

	private static Receipt MakeReceipt(decimal tax)
		=> new(Guid.NewGuid(), "TestStore", DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), new Money(tax));

	private static ReceiptItem MakeItem(string category, decimal amount)
		=> new(Guid.NewGuid(), null, $"Item-{category}", 1, new Money(amount), new Money(amount), category, null);

	private static Adjustment MakeAdjustment(decimal amount, AdjustmentType type = AdjustmentType.Discount)
		=> new(Guid.NewGuid(), type, new Money(amount));

	private static Transaction MakeTransaction(decimal amount)
		=> new(Guid.NewGuid(), new Money(amount), DateOnly.FromDateTime(DateTime.Today.AddDays(-1)));

	private static Dictionary<string, string> MakeMappings(params string[] categories)
	{
		Dictionary<string, string> map = new();
		foreach (string cat in categories)
		{
			map[cat] = $"ynab-{cat}";
		}
		return map;
	}

	// ── Category Allocation Tests ───────────────────

	[Fact]
	public void ComputeCategoryAllocations_SingleCategory_AllocatesAllTaxToThatCategory()
	{
		// Arrange
		ReceiptWithItems rwi = new()
		{
			Receipt = MakeReceipt(1.00m),
			Items = [MakeItem("Groceries", 10.00m)],
			Adjustments = [],
		};
		Dictionary<string, string> mappings = MakeMappings("Groceries");

		// Act
		List<YnabCategoryAllocation> allocations = YnabSplitCalculator.ComputeCategoryAllocations(rwi, mappings);

		// Assert
		allocations.Should().HaveCount(1);
		allocations[0].PreTaxAmount.Should().Be(10.00m);
		allocations[0].TaxAmount.Should().Be(1.00m);
		allocations[0].AdjustmentAmount.Should().Be(0m);
		allocations[0].Total.Should().Be(11.00m);
		allocations[0].Milliunits.Should().Be(11000L);
	}

	[Fact]
	public void ComputeCategoryAllocations_TwoCategories_AllocatesTaxProportionally()
	{
		// Arrange: 2/3 Groceries, 1/3 Gas
		ReceiptWithItems rwi = new()
		{
			Receipt = MakeReceipt(0.90m),
			Items =
			[
				MakeItem("Groceries", 6.00m),
				MakeItem("Gas", 3.00m),
			],
			Adjustments = [],
		};
		Dictionary<string, string> mappings = MakeMappings("Groceries", "Gas");

		// Act
		List<YnabCategoryAllocation> allocations = YnabSplitCalculator.ComputeCategoryAllocations(rwi, mappings);

		// Assert
		allocations.Should().HaveCount(2);

		YnabCategoryAllocation groceries = allocations.First(a => a.YnabCategoryId == "ynab-Groceries");
		YnabCategoryAllocation gas = allocations.First(a => a.YnabCategoryId == "ynab-Gas");

		groceries.TaxAmount.Should().Be(0.60m); // 6/9 * 0.90 = 0.60
		gas.TaxAmount.Should().Be(0.30m); // 3/9 * 0.90 = 0.30
		groceries.Total.Should().Be(6.60m);
		gas.Total.Should().Be(3.30m);
	}

	[Fact]
	public void ComputeCategoryAllocations_TaxAllocationWithRounding_ProducesMilliunits()
	{
		// Arrange: tax that doesn't divide evenly
		ReceiptWithItems rwi = new()
		{
			Receipt = MakeReceipt(1.00m),
			Items =
			[
				MakeItem("A", 10.00m),
				MakeItem("B", 10.00m),
				MakeItem("C", 10.00m),
			],
			Adjustments = [],
		};
		Dictionary<string, string> mappings = MakeMappings("A", "B", "C");

		// Act
		List<YnabCategoryAllocation> allocations = YnabSplitCalculator.ComputeCategoryAllocations(rwi, mappings);

		// Assert: each gets 1/3 of $1.00 tax = $0.333... repeating
		allocations.Should().HaveCount(3);

		foreach (YnabCategoryAllocation a in allocations)
		{
			// Tax should be exactly 1/3 in decimal (repeating)
			a.TaxAmount.Should().BeApproximately(0.33333333m, 0.00001m);
			// Total = 10 + 0.3333... = 10.3333...
			a.Total.Should().BeApproximately(10.3333333m, 0.0001m);
			// Milliunits of 10.3333... → 10333 (rounded)
			a.Milliunits.Should().Be(10333L);
		}
	}

	[Fact]
	public void ComputeCategoryAllocations_WithDiscount_AllocatesNegativeAdjustmentProportionally()
	{
		// Arrange: $1 discount on $15 receipt
		ReceiptWithItems rwi = new()
		{
			Receipt = MakeReceipt(0.00m),
			Items =
			[
				MakeItem("Groceries", 10.00m),
				MakeItem("Gas", 5.00m),
			],
			Adjustments = [MakeAdjustment(-1.00m)],
		};
		Dictionary<string, string> mappings = MakeMappings("Groceries", "Gas");

		// Act
		List<YnabCategoryAllocation> allocations = YnabSplitCalculator.ComputeCategoryAllocations(rwi, mappings);

		// Assert
		YnabCategoryAllocation groceries = allocations.First(a => a.YnabCategoryId == "ynab-Groceries");
		YnabCategoryAllocation gas = allocations.First(a => a.YnabCategoryId == "ynab-Gas");

		// 10/15 * -1.00 = -0.6667
		groceries.AdjustmentAmount.Should().BeApproximately(-0.66667m, 0.001m);
		// 5/15 * -1.00 = -0.3333
		gas.AdjustmentAmount.Should().BeApproximately(-0.33333m, 0.001m);

		groceries.Total.Should().BeApproximately(9.33333m, 0.001m);
		gas.Total.Should().BeApproximately(4.66667m, 0.001m);
	}

	[Fact]
	public void ComputeCategoryAllocations_MixedPositiveNegativeAdjustments()
	{
		// Arrange: tip (+2.00) and discount (-1.00) = net +1.00
		ReceiptWithItems rwi = new()
		{
			Receipt = MakeReceipt(0.00m),
			Items = [MakeItem("Groceries", 10.00m)],
			Adjustments =
			[
				MakeAdjustment(2.00m, AdjustmentType.Tip),
				MakeAdjustment(-1.00m, AdjustmentType.Discount),
			],
		};
		Dictionary<string, string> mappings = MakeMappings("Groceries");

		// Act
		List<YnabCategoryAllocation> allocations = YnabSplitCalculator.ComputeCategoryAllocations(rwi, mappings);

		// Assert: adjustment total = 2 + (-1) = 1.00
		allocations.Should().HaveCount(1);
		allocations[0].AdjustmentAmount.Should().Be(1.00m);
		allocations[0].Total.Should().Be(11.00m);
	}

	[Fact]
	public void ComputeCategoryAllocations_MultipleItemsSameCategory_Grouped()
	{
		// Arrange: two items in same category
		ReceiptWithItems rwi = new()
		{
			Receipt = MakeReceipt(0.50m),
			Items =
			[
				MakeItem("Groceries", 3.00m),
				MakeItem("Groceries", 7.00m),
			],
			Adjustments = [],
		};
		Dictionary<string, string> mappings = MakeMappings("Groceries");

		// Act
		List<YnabCategoryAllocation> allocations = YnabSplitCalculator.ComputeCategoryAllocations(rwi, mappings);

		// Assert: grouped as single category with $10 pre-tax
		allocations.Should().HaveCount(1);
		allocations[0].PreTaxAmount.Should().Be(10.00m);
		allocations[0].TaxAmount.Should().Be(0.50m);
		allocations[0].Total.Should().Be(10.50m);
	}

	// ── Largest Remainder Tests ─────────────────────

	[Fact]
	public void LargestRemainder_NoRemainder_ReturnsUnchanged()
	{
		// Arrange
		List<YnabSubTransactionSplit> subs =
		[
			new("cat-a", -5000),
			new("cat-b", -3000),
		];

		// Act
		List<YnabSubTransactionSplit> result = YnabSplitCalculator.ApplyLargestRemainderCorrection(-8000, subs);

		// Assert
		result.Should().HaveCount(2);
		result.Sum(s => s.Milliunits).Should().Be(-8000);
	}

	[Fact]
	public void LargestRemainder_PositiveRemainder_AddsToLargest()
	{
		// Arrange: sum is -8001, total is -8000, remainder = +1
		List<YnabSubTransactionSplit> subs =
		[
			new("cat-a", -5001),
			new("cat-b", -3000),
		];

		// Act
		List<YnabSubTransactionSplit> result = YnabSplitCalculator.ApplyLargestRemainderCorrection(-8000, subs);

		// Assert
		result.Sum(s => s.Milliunits).Should().Be(-8000);
		// Largest (abs) gets the +1 adjustment
		result.First(s => s.YnabCategoryId == "cat-a").Milliunits.Should().Be(-5000);
	}

	[Fact]
	public void LargestRemainder_NegativeRemainder_SubtractsFromLargest()
	{
		// Arrange: sum is -7999, total is -8000, remainder = -1
		List<YnabSubTransactionSplit> subs =
		[
			new("cat-a", -5000),
			new("cat-b", -2999),
		];

		// Act
		List<YnabSubTransactionSplit> result = YnabSplitCalculator.ApplyLargestRemainderCorrection(-8000, subs);

		// Assert
		result.Sum(s => s.Milliunits).Should().Be(-8000);
		// Largest gets -1
		result.First(s => s.YnabCategoryId == "cat-a").Milliunits.Should().Be(-5001);
	}

	[Fact]
	public void LargestRemainder_MultipleCorrections_DistributesAcrossLargest()
	{
		// Arrange: sum is -14997, total is -15000, remainder = -3
		List<YnabSubTransactionSplit> subs =
		[
			new("cat-a", -5000),
			new("cat-b", -5000),
			new("cat-c", -4997),
		];

		// Act
		List<YnabSubTransactionSplit> result = YnabSplitCalculator.ApplyLargestRemainderCorrection(-15000, subs);

		// Assert
		result.Sum(s => s.Milliunits).Should().Be(-15000);
	}

	[Fact]
	public void LargestRemainder_RemainderExceedsCount_Throws()
	{
		// Arrange: sum is -5000, total is -8000, remainder = -3000 (way too large)
		List<YnabSubTransactionSplit> subs =
		[
			new("cat-a", -5000),
		];

		// Act & Assert
		Action act = () => YnabSplitCalculator.ApplyLargestRemainderCorrection(-8000, subs);
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*Rounding remainder*exceeds*");
	}

	[Fact]
	public void LargestRemainder_EmptyList_ReturnsEmpty()
	{
		// Act
		List<YnabSubTransactionSplit> result = YnabSplitCalculator.ApplyLargestRemainderCorrection(-1000, []);

		// Assert
		result.Should().BeEmpty();
	}

	// ── Waterfall Tests ─────────────────────────────

	[Fact]
	public void ComputeWaterfallSplits_SingleTransaction_SingleCategory()
	{
		// Arrange
		Transaction tx = MakeTransaction(16.00m);
		ReceiptWithItems rwi = new()
		{
			Receipt = MakeReceipt(1.00m),
			Items = [MakeItem("Groceries", 15.00m)],
			Adjustments = [],
		};
		Dictionary<string, string> mappings = MakeMappings("Groceries");

		// Act
		YnabSplitResult result = _calculator.ComputeWaterfallSplits(rwi, [tx], mappings);

		// Assert
		result.TransactionSplits.Should().HaveCount(1);
		YnabTransactionSplit split = result.TransactionSplits[0];
		split.TotalMilliunits.Should().Be(-16000); // negated
		split.SubTransactions.Should().HaveCount(1);
		split.SubTransactions[0].Milliunits.Should().Be(-16000);
	}

	[Fact]
	public void ComputeWaterfallSplits_SingleTransaction_MultipleCategories()
	{
		// Arrange: $10 groceries + $5 gas + $0.90 tax = $15.90
		Transaction tx = MakeTransaction(15.90m);
		ReceiptWithItems rwi = new()
		{
			Receipt = MakeReceipt(0.90m),
			Items =
			[
				MakeItem("Groceries", 10.00m),
				MakeItem("Gas", 5.00m),
			],
			Adjustments = [],
		};
		Dictionary<string, string> mappings = MakeMappings("Groceries", "Gas");

		// Act
		YnabSplitResult result = _calculator.ComputeWaterfallSplits(rwi, [tx], mappings);

		// Assert
		result.TransactionSplits.Should().HaveCount(1);
		YnabTransactionSplit split = result.TransactionSplits[0];
		split.TotalMilliunits.Should().Be(-15900);

		// Sub-transactions should sum to total
		split.SubTransactions.Sum(s => s.Milliunits).Should().Be(-15900);
		split.SubTransactions.Should().HaveCount(2);
	}

	[Fact]
	public void ComputeWaterfallSplits_SingleTransaction_RemainderCorrection()
	{
		// Arrange: 3 equal categories with $1.00 tax → each gets 0.3333... tax
		// $10 * 3 = $30 pre-tax + $1 tax = $31.00
		Transaction tx = MakeTransaction(31.00m);
		ReceiptWithItems rwi = new()
		{
			Receipt = MakeReceipt(1.00m),
			Items =
			[
				MakeItem("A", 10.00m),
				MakeItem("B", 10.00m),
				MakeItem("C", 10.00m),
			],
			Adjustments = [],
		};
		Dictionary<string, string> mappings = MakeMappings("A", "B", "C");

		// Act
		YnabSplitResult result = _calculator.ComputeWaterfallSplits(rwi, [tx], mappings);

		// Assert
		YnabTransactionSplit split = result.TransactionSplits[0];
		split.TotalMilliunits.Should().Be(-31000);

		// Each category: 10 + 0.3333 = 10.3333 → 10333 milliunits (negated: -10333)
		// Sum: -10333 * 3 = -30999, remainder = -1
		// Largest-remainder should fix this
		split.SubTransactions.Sum(s => s.Milliunits).Should().Be(-31000);
	}

	[Fact]
	public void ComputeWaterfallSplits_MultiTransaction_CategoriesSpanBoundary()
	{
		// Arrange: 2 transactions totaling $20, 2 categories of $12 and $8
		// tx1 = $11, tx2 = $9 (categories: $12 Groceries, $8 Gas, no tax)
		Transaction tx1 = MakeTransaction(11.00m);
		Transaction tx2 = MakeTransaction(9.00m);
		ReceiptWithItems rwi = new()
		{
			Receipt = MakeReceipt(0.00m),
			Items =
			[
				MakeItem("Groceries", 12.00m),
				MakeItem("Gas", 8.00m),
			],
			Adjustments = [],
		};
		Dictionary<string, string> mappings = MakeMappings("Groceries", "Gas");

		// Act
		YnabSplitResult result = _calculator.ComputeWaterfallSplits(rwi, [tx1, tx2], mappings);

		// Assert
		result.TransactionSplits.Should().HaveCount(2);

		// tx1 (larger: $11): Groceries fills entirely at $12? No, tx1 is $11 and Groceries is $12
		// So Groceries straddles: $11 in tx1, $1 in tx2
		// tx1 subs should sum to -11000
		YnabTransactionSplit split1 = result.TransactionSplits[0];
		split1.TotalMilliunits.Should().Be(-11000);
		split1.SubTransactions.Sum(s => s.Milliunits).Should().Be(-11000);

		// tx2 subs should sum to -9000
		YnabTransactionSplit split2 = result.TransactionSplits[1];
		split2.TotalMilliunits.Should().Be(-9000);
		split2.SubTransactions.Sum(s => s.Milliunits).Should().Be(-9000);
	}

	[Fact]
	public void ComputeWaterfallSplits_SignConvention_PositiveLocal_NegativeYnab()
	{
		// Arrange
		Transaction tx = MakeTransaction(10.00m);
		ReceiptWithItems rwi = new()
		{
			Receipt = MakeReceipt(0.00m),
			Items = [MakeItem("A", 10.00m)],
			Adjustments = [],
		};

		// Act
		YnabSplitResult result = _calculator.ComputeWaterfallSplits(rwi, [tx], MakeMappings("A"));

		// Assert: positive local = negative YNAB (outflow)
		result.TransactionSplits[0].TotalMilliunits.Should().BeNegative();
		result.TransactionSplits[0].SubTransactions[0].Milliunits.Should().BeNegative();
	}

	[Fact]
	public void ComputeWaterfallSplits_ZeroTax_NoTaxAllocation()
	{
		// Arrange
		Transaction tx = MakeTransaction(10.00m);
		ReceiptWithItems rwi = new()
		{
			Receipt = MakeReceipt(0.00m),
			Items = [MakeItem("A", 10.00m)],
			Adjustments = [],
		};

		// Act
		YnabSplitResult result = _calculator.ComputeWaterfallSplits(rwi, [tx], MakeMappings("A"));

		// Assert
		result.TransactionSplits[0].SubTransactions[0].Milliunits.Should().Be(-10000);
	}

	[Fact]
	public void ComputeWaterfallSplits_WithNegativeAdjustment_ReducesTotal()
	{
		// Arrange: $10 item - $1 discount = $9 total
		Transaction tx = MakeTransaction(9.00m);
		ReceiptWithItems rwi = new()
		{
			Receipt = MakeReceipt(0.00m),
			Items = [MakeItem("A", 10.00m)],
			Adjustments = [MakeAdjustment(-1.00m)],
		};

		// Act
		YnabSplitResult result = _calculator.ComputeWaterfallSplits(rwi, [tx], MakeMappings("A"));

		// Assert: total should be $9 (10 - 1)
		result.TransactionSplits[0].SubTransactions[0].Milliunits.Should().Be(-9000);
	}

	[Fact]
	public void ComputeWaterfallSplits_ThreeTransactions_WaterfallOverflow()
	{
		// Arrange: receipt with 1 big category ($30 total), 3 transactions: $15, $10, $5
		Transaction tx1 = MakeTransaction(15.00m);
		Transaction tx2 = MakeTransaction(10.00m);
		Transaction tx3 = MakeTransaction(5.00m);
		ReceiptWithItems rwi = new()
		{
			Receipt = MakeReceipt(0.00m),
			Items =
			[
				MakeItem("A", 20.00m),
				MakeItem("B", 10.00m),
			],
			Adjustments = [],
		};
		Dictionary<string, string> mappings = MakeMappings("A", "B");

		// Act
		YnabSplitResult result = _calculator.ComputeWaterfallSplits(rwi, [tx1, tx2, tx3], mappings);

		// Assert: all 3 transactions have subs that sum to their total
		result.TransactionSplits.Should().HaveCount(3);
		foreach (YnabTransactionSplit split in result.TransactionSplits)
		{
			split.SubTransactions.Sum(s => s.Milliunits).Should().Be(split.TotalMilliunits);
		}
	}

	[Fact]
	public void ComputeCategoryAllocations_ZeroPreTaxTotal_HandlesGracefully()
	{
		// Edge case: if somehow all items have 0 amount (shouldn't happen per validation, but guard anyway)
		// This test just ensures no division-by-zero
		ReceiptWithItems rwi = new()
		{
			Receipt = MakeReceipt(0.00m),
			Items = [], // Empty items but we're calling directly
			Adjustments = [],
		};
		Dictionary<string, string> mappings = new();

		// Act
		List<YnabCategoryAllocation> allocations = YnabSplitCalculator.ComputeCategoryAllocations(rwi, mappings);

		// Assert
		allocations.Should().BeEmpty();
	}

	[Fact]
	public void ComputeWaterfallSplits_ExactDivision_NoRemainder()
	{
		// Arrange: $10 + $10 + $4 tax = $24, two categories split evenly
		Transaction tx = MakeTransaction(24.00m);
		ReceiptWithItems rwi = new()
		{
			Receipt = MakeReceipt(4.00m),
			Items =
			[
				MakeItem("A", 10.00m),
				MakeItem("B", 10.00m),
			],
			Adjustments = [],
		};
		Dictionary<string, string> mappings = MakeMappings("A", "B");

		// Act
		YnabSplitResult result = _calculator.ComputeWaterfallSplits(rwi, [tx], mappings);

		// Assert: each gets $12 (10 + 2 tax), no remainder
		YnabTransactionSplit split = result.TransactionSplits[0];
		split.SubTransactions.Should().HaveCount(2);
		split.SubTransactions.Should().AllSatisfy(s => s.Milliunits.Should().Be(-12000));
		split.SubTransactions.Sum(s => s.Milliunits).Should().Be(-24000);
	}

	[Fact]
	public void ComputeWaterfallSplits_SmallAmounts_PrecisionMaintained()
	{
		// Arrange: very small amounts
		Transaction tx = MakeTransaction(0.03m);
		ReceiptWithItems rwi = new()
		{
			Receipt = MakeReceipt(0.01m),
			Items =
			[
				MakeItem("A", 0.01m),
				MakeItem("B", 0.01m),
			],
			Adjustments = [],
		};
		Dictionary<string, string> mappings = MakeMappings("A", "B");

		// Act
		YnabSplitResult result = _calculator.ComputeWaterfallSplits(rwi, [tx], mappings);

		// Assert: subs sum to total
		YnabTransactionSplit split = result.TransactionSplits[0];
		split.SubTransactions.Sum(s => s.Milliunits).Should().Be(-30);
	}
}
