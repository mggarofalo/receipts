using Common;
using Domain;
using Domain.Core;
using Infrastructure.Entities;
using Infrastructure.Entities.Core;
using Infrastructure.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Seeds a large, realistic sample dataset — accounts, cards, receipts, line items,
/// transactions and adjustments — for local development and demo environments.
/// </summary>
/// <remarks>
/// <para>
/// The dataset spans the last three years at roughly four receipts per week, drawn from a
/// per-store item catalog so receipts look coherent. It is gated behind the
/// <c>SampleData:Enabled</c> configuration flag and recorded in <see cref="SeedHistoryEntry"/>
/// so it is applied at most once. Generation is fully deterministic (fixed RNG seed): a
/// wipe-and-rebuild reproduces a byte-identical dataset, which suits a monthly-rebuild demo site.
/// </para>
/// <para>
/// <b>Schema coupling — keep this seeder current.</b> It builds <see cref="Domain"/> objects
/// through their validating constructors and converts them to entities with the production
/// Mapperly mappers in <c>Infrastructure.Mapping</c>. A new required domain-constructor
/// parameter breaks this file's compilation; a new entity property surfaces as a Mapperly
/// <c>RMG</c> unmapped-target warning on the existing mapper. The build runs warnings-as-errors,
/// so either kind of schema change fails the build until this seeder is updated to match.
/// </para>
/// </remarks>
public static class SampleDataSeederService
{
	private const string SampleDataSeedId = "SampleData_v1";

	/// <summary>Fixed RNG seed — keeps the generated dataset reproducible across rebuilds.</summary>
	private const int RandomSeed = 20260518;

	private const int YearsOfHistory = 3;
	private const double MinReceiptsPerWeek = 3;
	private const double MaxReceiptsPerWeek = 5;
	private const decimal MaxReceiptTotal = 400m;

	/// <summary>Fraction of receipts deliberately left out of balance, to populate the reconciliation report.</summary>
	private const double OutOfBalanceFraction = 0.02;

	/// <summary>
	/// Seeds the sample dataset if it has not already been applied. Idempotent: a recorded
	/// <see cref="SeedHistoryEntry"/> for <see cref="SampleDataSeedId"/> short-circuits the call.
	/// </summary>
	public static async Task SeedAsync(IServiceProvider services)
	{
		using IServiceScope scope = services.CreateScope();
		ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
		ILogger logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(SampleDataSeederService));

		if (await dbContext.SeedHistory.AnyAsync(s => s.SeedId == SampleDataSeedId))
		{
			logger.LogInformation("Seed operation '{SeedId}' already applied — skipping.", SampleDataSeedId);
			return;
		}

		logger.LogInformation("Generating sample dataset ({Years} years of history)...", YearsOfHistory);

		Random rng = new(RandomSeed);
		GeneratedData data = GenerateDataset(rng);

		// Bulk insert — AutoDetectChanges is needlessly expensive for a write-only seed, and
		// auditing is disabled so this one-time import does not emit a per-row audit-log entry
		// for every seeded entity (thousands of null-attributed rows). Soft-delete handling and
		// description reconciliation in SaveChangesAsync still run.
		dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
		dbContext.AuditingEnabled = false;
		dbContext.Accounts.AddRange(data.Accounts);
		dbContext.Cards.AddRange(data.Cards);
		dbContext.Receipts.AddRange(data.Receipts);
		dbContext.ReceiptItems.AddRange(data.ReceiptItems);
		dbContext.Transactions.AddRange(data.Transactions);
		dbContext.Adjustments.AddRange(data.Adjustments);
		dbContext.SeedHistory.Add(new SeedHistoryEntry
		{
			SeedId = SampleDataSeedId,
			AppliedAt = DateTimeOffset.UtcNow,
		});

		await dbContext.SaveChangesAsync();

		logger.LogInformation(
			"Sample data seeded: {Accounts} accounts, {Cards} cards, {Receipts} receipts, "
			+ "{Items} line items, {Transactions} transactions, {Adjustments} adjustments.",
			data.Accounts.Count, data.Cards.Count, data.Receipts.Count,
			data.ReceiptItems.Count, data.Transactions.Count, data.Adjustments.Count);
	}

	#region Dataset generation

	private static GeneratedData GenerateDataset(Random rng)
	{
		// Mappers are stateless — one instance each is enough for the whole run.
		AccountMapper accountMapper = new();
		CardMapper cardMapper = new();
		ReceiptMapper receiptMapper = new();
		ReceiptItemMapper receiptItemMapper = new();
		TransactionMapper transactionMapper = new();
		AdjustmentMapper adjustmentMapper = new();

		GeneratedData data = new();

		// --- Accounts and cards -------------------------------------------------
		List<Card> cards = [];
		foreach ((string accountName, string[] cardSpecs) in AccountSpecs)
		{
			Account account = new(NextGuid(rng), accountName);
			data.Accounts.Add(accountMapper.ToEntity(account));

			foreach (string cardSpec in cardSpecs)
			{
				string[] parts = cardSpec.Split('|');
				Card card = new(NextGuid(rng), parts[1], parts[0], account.Id);
				cards.Add(card);
				data.Cards.Add(cardMapper.ToEntity(card));
			}
		}

		// --- Receipts over the history window -----------------------------------
		DateOnly today = DateOnly.FromDateTime(DateTime.Today);
		DateOnly start = today.AddYears(-YearsOfHistory);
		int storeWeightTotal = Stores.Sum(s => s.Weight);

		for (DateOnly weekStart = start; weekStart <= today; weekStart = weekStart.AddDays(7))
		{
			int receiptsThisWeek = (int)Math.Round(MinReceiptsPerWeek
				+ (rng.NextDouble() * (MaxReceiptsPerWeek - MinReceiptsPerWeek)));

			for (int i = 0; i < receiptsThisWeek; i++)
			{
				DateOnly date = weekStart.AddDays(rng.Next(0, 7));
				if (date > today)
				{
					continue;
				}

				Store store = PickWeighted(rng, storeWeightTotal);
				Card card = cards[rng.Next(cards.Count)];
				BuildReceipt(rng, data, store, card, date,
					receiptMapper, receiptItemMapper, transactionMapper, adjustmentMapper);
			}
		}

		return data;
	}

	private static void BuildReceipt(
		Random rng,
		GeneratedData data,
		Store store,
		Card card,
		DateOnly date,
		ReceiptMapper receiptMapper,
		ReceiptItemMapper receiptItemMapper,
		TransactionMapper transactionMapper,
		AdjustmentMapper adjustmentMapper)
	{
		// Pick a distinct subset of the store's catalog (capped at the catalog size).
		int requested = rng.Next(store.MinItems, store.MaxItems + 1);
		int itemCount = Math.Min(requested, store.Catalog.Count);
		List<CatalogItem> chosen = [.. store.Catalog];
		Shuffle(rng, chosen);

		List<ReceiptItem> items = [];
		decimal subtotal = 0m;
		for (int i = 0; i < itemCount; i++)
		{
			CatalogItem catalog = chosen[i];

			decimal quantity = catalog.FractionalQuantity
				? Math.Round((decimal)(6 + (rng.NextDouble() * 14)), 3)
				: rng.Next(1, catalog.MaxQuantity + 1);

			// Vary the unit price +/-12% so repeated items are not identical run to run.
			decimal unitPrice = Math.Round(
				catalog.BasePrice * (decimal)(0.88 + (rng.NextDouble() * 0.24)), 2,
				MidpointRounding.AwayFromZero);
			if (unitPrice <= 0m)
			{
				unitPrice = 0.01m;
			}

			decimal total = Math.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);

			// Keep the receipt within the target spend ceiling.
			if (subtotal + total > MaxReceiptTotal && items.Count >= store.MinItems)
			{
				break;
			}

			items.Add(new ReceiptItem(
				NextGuid(rng),
				catalog.Code,
				catalog.Description,
				quantity,
				new Money(unitPrice),
				new Money(total),
				catalog.Category,
				catalog.Subcategory));
			subtotal += total;
		}

		decimal tax = Math.Round(subtotal * store.TaxRate, 2, MidpointRounding.AwayFromZero);
		Receipt receipt = new(NextGuid(rng), store.Location, date, new Money(tax));

		ReceiptEntity receiptEntity = receiptMapper.ToEntity(receipt);
		data.Receipts.Add(receiptEntity);
		foreach (ReceiptItem item in items)
		{
			ReceiptItemEntity itemEntity = receiptItemMapper.ToEntity(item);
			itemEntity.ReceiptId = receipt.Id; // FK is intentionally not mapped — set it here.
			data.ReceiptItems.Add(itemEntity);
		}

		// --- Adjustments --------------------------------------------------------
		decimal adjustmentTotal = 0m;
		foreach (Adjustment adjustment in BuildAdjustments(rng, store, subtotal))
		{
			adjustmentTotal += adjustment.Amount.Amount;
			AdjustmentEntity adjustmentEntity = adjustmentMapper.ToEntity(adjustment);
			adjustmentEntity.ReceiptId = receipt.Id;
			data.Adjustments.Add(adjustmentEntity);
		}

		// --- Transaction --------------------------------------------------------
		// A balanced receipt's transaction equals item subtotal + tax + signed adjustments
		// (see ReportService.GetOutOfBalanceAsync). A small fraction is left out of balance
		// on purpose so the reconciliation report has content.
		decimal transactionAmount = subtotal + tax + adjustmentTotal;
		if (rng.NextDouble() < OutOfBalanceFraction)
		{
			decimal drift = Math.Round((decimal)(1 + (rng.NextDouble() * 14)), 2);
			transactionAmount += rng.Next(2) == 0 ? drift : -drift;
		}

		if (transactionAmount <= 0m)
		{
			transactionAmount = 0.01m; // Transaction requires a non-zero amount.
		}

		Transaction transaction = new(NextGuid(rng), card.Id, new Money(transactionAmount), date);
		TransactionEntity transactionEntity = transactionMapper.ToEntity(transaction);
		transactionEntity.ReceiptId = receipt.Id; // FK is intentionally not mapped — set it here.
		data.Transactions.Add(transactionEntity);
	}

	private static IEnumerable<Adjustment> BuildAdjustments(Random rng, Store store, decimal subtotal)
	{
		switch (store.Adjustment)
		{
			case AdjustmentStyle.Tip:
				// Restaurants: a tip of 15–22% of the subtotal.
				decimal tip = Math.Round(subtotal * (decimal)(0.15 + (rng.NextDouble() * 0.07)), 2);
				if (tip > 0m)
				{
					yield return new Adjustment(NextGuid(rng), AdjustmentType.Tip, new Money(tip), "Gratuity");
				}

				break;

			case AdjustmentStyle.OccasionalDiscount:
				// Retail: ~25% of trips carry a coupon or promotional discount (a negative amount).
				if (rng.NextDouble() < 0.25)
				{
					decimal discount = Math.Round(subtotal * (decimal)(0.08 + (rng.NextDouble() * 0.17)), 2);
					if (discount > 0m)
					{
						bool coupon = rng.Next(2) == 0;
						yield return new Adjustment(
							NextGuid(rng),
							coupon ? AdjustmentType.Coupon : AdjustmentType.Discount,
							new Money(-discount),
							coupon ? "Coupon" : "Promotional discount");
					}
				}

				break;

			case AdjustmentStyle.None:
			default:
				break;
		}
	}

	#endregion

	#region Helpers

	/// <summary>Builds a Guid deterministically from the seeded RNG so rebuilds are reproducible.</summary>
	private static Guid NextGuid(Random rng)
	{
		byte[] bytes = new byte[16];
		rng.NextBytes(bytes);
		return new Guid(bytes);
	}

	private static void Shuffle<T>(Random rng, IList<T> list)
	{
		for (int i = list.Count - 1; i > 0; i--)
		{
			int j = rng.Next(i + 1);
			(list[i], list[j]) = (list[j], list[i]);
		}
	}

	private static Store PickWeighted(Random rng, int weightTotal)
	{
		int roll = rng.Next(weightTotal);
		foreach (Store store in Stores)
		{
			roll -= store.Weight;
			if (roll < 0)
			{
				return store;
			}
		}

		return Stores[^1];
	}

	#endregion

	#region Catalog data

	/// <summary>Accumulates generated entities during a single seed run.</summary>
	private sealed class GeneratedData
	{
		public List<AccountEntity> Accounts { get; } = [];
		public List<CardEntity> Cards { get; } = [];
		public List<ReceiptEntity> Receipts { get; } = [];
		public List<ReceiptItemEntity> ReceiptItems { get; } = [];
		public List<TransactionEntity> Transactions { get; } = [];
		public List<AdjustmentEntity> Adjustments { get; } = [];
	}

	private enum AdjustmentStyle
	{
		None,
		Tip,
		OccasionalDiscount,
	}

	private sealed record CatalogItem(
		string Description,
		string Code,
		decimal BasePrice,
		string Category,
		string? Subcategory,
		int MaxQuantity = 3,
		bool FractionalQuantity = false);

	private sealed record Store(
		string Location,
		int Weight,
		int MinItems,
		int MaxItems,
		decimal TaxRate,
		AdjustmentStyle Adjustment,
		IReadOnlyList<CatalogItem> Catalog);

	// Account name -> cards, each card as "Display Name|CARD-CODE".
	private static readonly (string Account, string[] Cards)[] AccountSpecs =
	[
		("Joint Checking", ["Everyday Debit|JC-DEBIT", "Household Visa|JC-VISA"]),
		("Personal Credit", ["Cash Rewards|PC-CASH", "Everyday Mastercard|PC-MC"]),
		("Travel Rewards", ["Travel Visa Signature|TR-VISA"]),
	];

	// Categories/subcategories match the names seeded by the SeedDevelopmentData migration so
	// generated line items line up with the in-app suggestion lists.
	private static readonly IReadOnlyList<Store> Stores =
	[
		new Store("Hillside Market", Weight: 34, MinItems: 8, MaxItems: 28, TaxRate: 0.03m,
			AdjustmentStyle.None,
		[
			new("Bananas", "PRD-BAN", 0.59m, "Groceries", "Produce", 6),
			new("Gala Apples", "PRD-APL", 1.29m, "Groceries", "Produce", 5),
			new("Roma Tomatoes", "PRD-TOM", 1.79m, "Groceries", "Produce", 4),
			new("Romaine Lettuce", "PRD-LET", 2.49m, "Groceries", "Produce", 2),
			new("Baby Spinach", "PRD-SPN", 3.99m, "Groceries", "Produce", 2),
			new("Yellow Onions", "PRD-ONI", 0.99m, "Groceries", "Produce", 4),
			new("Avocados", "PRD-AVO", 1.49m, "Groceries", "Produce", 5),
			new("Strawberries", "PRD-STR", 4.49m, "Groceries", "Produce", 2),
			new("Gallon of Milk", "DRY-MLK", 4.99m, "Groceries", "Dairy", 2),
			new("Large Eggs (dozen)", "DRY-EGG", 3.79m, "Groceries", "Dairy", 2),
			new("Cheddar Cheese Block", "DRY-CHD", 5.49m, "Groceries", "Dairy", 2),
			new("Greek Yogurt", "DRY-YOG", 1.25m, "Groceries", "Dairy", 6),
			new("Salted Butter", "DRY-BTR", 4.29m, "Groceries", "Dairy", 2),
			new("Loaf of Bread", "BKY-BRD", 3.49m, "Groceries", "Bakery", 2),
			new("Bagels (6 ct)", "BKY-BGL", 4.19m, "Groceries", "Bakery", 2),
			new("Blueberry Muffins", "BKY-MUF", 5.99m, "Groceries", "Bakery", 1),
			new("Chicken Breast", "MTS-CHK", 8.99m, "Groceries", "Meat & Seafood", 2),
			new("Ground Beef (1 lb)", "MTS-BEF", 6.49m, "Groceries", "Meat & Seafood", 3),
			new("Atlantic Salmon Fillet", "MTS-SAL", 11.99m, "Groceries", "Meat & Seafood", 1),
			new("Sliced Deli Turkey", "MTS-TRK", 7.49m, "Groceries", "Meat & Seafood", 1),
			new("Breakfast Cereal", "GRC-CRL", 4.79m, "Groceries", null, 2),
			new("Spaghetti Pasta", "GRC-PAS", 1.99m, "Groceries", null, 3),
			new("Marinara Sauce", "GRC-SAU", 3.29m, "Groceries", null, 2),
			new("White Rice (2 lb)", "GRC-RIC", 3.99m, "Groceries", null, 2),
			new("Olive Oil", "GRC-OIL", 9.99m, "Groceries", null, 1),
			new("Orange Juice", "GRC-OJ", 4.49m, "Groceries", null, 2),
			new("Ground Coffee", "GRC-COF", 8.99m, "Groceries", null, 1),
			new("Paper Towels (2 ct)", "GRC-PTL", 5.99m, "Groceries", null, 2),
			new("Dish Soap", "GRC-DSH", 3.49m, "Groceries", null, 1),
			new("Potato Chips", "GRC-CHP", 4.29m, "Groceries", null, 3),
		]),
		new Store("BulkMart Warehouse Club", Weight: 12, MinItems: 6, MaxItems: 18, TaxRate: 0.04m,
			AdjustmentStyle.OccasionalDiscount,
		[
			new("Bottled Water (40 ct)", "BM-WTR", 6.49m, "Groceries", null, 2),
			new("Paper Towels (12 ct)", "BM-PTL", 21.99m, "Groceries", null, 1),
			new("Bath Tissue (30 ct)", "BM-TIS", 24.99m, "Groceries", null, 1),
			new("Rotisserie Chicken", "BM-CHK", 5.99m, "Groceries", "Meat & Seafood", 2),
			new("Ground Coffee (3 lb)", "BM-COF", 17.99m, "Groceries", null, 1),
			new("Olive Oil (2 L)", "BM-OIL", 19.99m, "Groceries", null, 1),
			new("Mixed Nuts (2.5 lb)", "BM-NUT", 15.99m, "Groceries", null, 1),
			new("Eggs (24 ct)", "BM-EGG", 6.99m, "Groceries", "Dairy", 2),
			new("Cheese Block (2 lb)", "BM-CHS", 11.99m, "Groceries", "Dairy", 1),
			new("Frozen Berries (4 lb)", "BM-BER", 12.99m, "Groceries", null, 1),
			new("Laundry Detergent", "BM-DET", 18.99m, "Groceries", null, 1),
			new("Dish Soap (2 pk)", "BM-DSH", 9.99m, "Groceries", null, 1),
			new("Trash Bags (200 ct)", "BM-TRB", 22.99m, "Groceries", null, 1),
			new("AA Batteries (48 ct)", "BM-BAT", 17.99m, "Shopping", "Electronics", 1),
			new("Printer Paper (case)", "BM-PPR", 39.99m, "Shopping", null, 1),
			new("Mens Crew Socks (12 pk)", "BM-SOK", 14.99m, "Shopping", "Clothing", 2),
			new("Hand Soap (3 pk)", "BM-HSP", 8.49m, "Groceries", null, 2),
			new("Granola Bars (60 ct)", "BM-GRN", 13.99m, "Groceries", null, 1),
		]),
		new Store("QuickFuel Station", Weight: 16, MinItems: 5, MaxItems: 7, TaxRate: 0m,
			AdjustmentStyle.None,
		[
			new("Regular Unleaded Gas", "GAS-REG", 3.39m, "Transportation", "Gas", 1, FractionalQuantity: true),
			new("Premium Unleaded Gas", "GAS-PRM", 3.99m, "Transportation", "Gas", 1, FractionalQuantity: true),
			new("Diesel Fuel", "GAS-DSL", 3.79m, "Transportation", "Gas", 1, FractionalQuantity: true),
			new("Automatic Car Wash", "GAS-WSH", 12.00m, "Transportation", null, 1),
			new("Bottled Water", "GAS-WTR", 1.99m, "Groceries", null, 2),
			new("Energy Drink", "GAS-NRG", 3.49m, "Groceries", null, 2),
			new("Snack Bar", "GAS-SNK", 2.29m, "Groceries", null, 3),
			new("Motor Oil (1 qt)", "GAS-OIL", 7.99m, "Transportation", null, 2),
		]),
		new Store("The Daily Grind Coffee", Weight: 14, MinItems: 5, MaxItems: 9, TaxRate: 0.07m,
			AdjustmentStyle.None,
		[
			new("Caffe Latte", "DG-LAT", 4.95m, "Dining", "Coffee Shop", 2),
			new("Cappuccino", "DG-CAP", 4.75m, "Dining", "Coffee Shop", 2),
			new("Drip Coffee", "DG-DRP", 2.95m, "Dining", "Coffee Shop", 3),
			new("Cold Brew", "DG-CLD", 4.50m, "Dining", "Coffee Shop", 2),
			new("Espresso Shot", "DG-ESP", 2.50m, "Dining", "Coffee Shop", 2),
			new("Chai Tea Latte", "DG-CHA", 4.65m, "Dining", "Coffee Shop", 2),
			new("Hot Chocolate", "DG-HOT", 3.95m, "Dining", "Coffee Shop", 2),
			new("Butter Croissant", "DG-CRS", 3.75m, "Dining", "Coffee Shop", 2),
			new("Blueberry Muffin", "DG-MUF", 3.50m, "Dining", "Coffee Shop", 2),
			new("Everything Bagel", "DG-BGL", 3.25m, "Dining", "Coffee Shop", 2),
			new("Avocado Toast", "DG-TST", 7.95m, "Dining", "Coffee Shop", 1),
			new("Bag of Whole Beans", "DG-BEAN", 16.95m, "Dining", "Coffee Shop", 1),
		]),
		new Store("The Copper Skillet", Weight: 10, MinItems: 5, MaxItems: 11, TaxRate: 0.08m,
			AdjustmentStyle.Tip,
		[
			new("House Burger", "CS-BRG", 15.50m, "Dining", "Sit-Down Restaurant", 2),
			new("Grilled Salmon", "CS-SAL", 24.00m, "Dining", "Sit-Down Restaurant", 2),
			new("Ribeye Steak", "CS-STK", 32.00m, "Dining", "Sit-Down Restaurant", 2),
			new("Margherita Pizza", "CS-PIZ", 16.00m, "Dining", "Sit-Down Restaurant", 1),
			new("Caesar Salad", "CS-CSR", 9.50m, "Dining", "Sit-Down Restaurant", 2),
			new("Soup of the Day", "CS-SUP", 7.00m, "Dining", "Sit-Down Restaurant", 2),
			new("Mozzarella Sticks", "CS-MOZ", 8.50m, "Dining", "Sit-Down Restaurant", 1),
			new("Garlic Bread", "CS-GRL", 5.50m, "Dining", "Sit-Down Restaurant", 1),
			new("Iced Tea", "CS-TEA", 3.00m, "Dining", "Sit-Down Restaurant", 4),
			new("Soft Drink", "CS-SOD", 3.25m, "Dining", "Sit-Down Restaurant", 4),
			new("Draft Beer", "CS-BER", 6.50m, "Dining", "Sit-Down Restaurant", 3),
			new("Glass of Wine", "CS-WIN", 9.00m, "Dining", "Sit-Down Restaurant", 3),
			new("Cheesecake", "CS-CKE", 8.00m, "Dining", "Sit-Down Restaurant", 2),
			new("Chocolate Lava Cake", "CS-LAV", 8.50m, "Dining", "Sit-Down Restaurant", 2),
		]),
		new Store("Volt Electronics", Weight: 7, MinItems: 5, MaxItems: 12, TaxRate: 0.07m,
			AdjustmentStyle.OccasionalDiscount,
		[
			new("USB-C Charging Cable", "VE-USBC", 14.99m, "Shopping", "Electronics", 3),
			new("Wireless Mouse", "VE-MOU", 24.99m, "Shopping", "Electronics", 2),
			new("Mechanical Keyboard", "VE-KEY", 79.99m, "Shopping", "Electronics", 1),
			new("Bluetooth Headphones", "VE-HDP", 59.99m, "Shopping", "Electronics", 1),
			new("HDMI Cable (6 ft)", "VE-HDMI", 11.99m, "Shopping", "Electronics", 2),
			new("USB Flash Drive 128GB", "VE-FLSH", 17.99m, "Shopping", "Electronics", 2),
			new("Phone Wall Charger", "VE-CHG", 19.99m, "Shopping", "Electronics", 2),
			new("Laptop Sleeve", "VE-SLV", 22.99m, "Shopping", "Electronics", 1),
			new("Webcam 1080p", "VE-CAM", 44.99m, "Shopping", "Electronics", 1),
			new("Surge Protector", "VE-SRG", 18.99m, "Shopping", "Electronics", 1),
			new("AA Batteries (8 ct)", "VE-BAT", 8.99m, "Shopping", "Electronics", 3),
			new("Screen Cleaning Kit", "VE-CLN", 9.99m, "Shopping", "Electronics", 1),
			new("Portable Power Bank", "VE-PWR", 34.99m, "Shopping", "Electronics", 1),
		]),
		new Store("Trendline Apparel", Weight: 7, MinItems: 5, MaxItems: 14, TaxRate: 0.07m,
			AdjustmentStyle.OccasionalDiscount,
		[
			new("Cotton T-Shirt", "TA-TEE", 14.99m, "Shopping", "Clothing", 3),
			new("Slim-Fit Jeans", "TA-JEN", 49.99m, "Shopping", "Clothing", 2),
			new("Hooded Sweatshirt", "TA-HOD", 39.99m, "Shopping", "Clothing", 1),
			new("Crew Socks (3 pk)", "TA-SOK", 11.99m, "Shopping", "Clothing", 2),
			new("Leather Belt", "TA-BLT", 29.99m, "Shopping", "Clothing", 1),
			new("Knit Beanie", "TA-BNE", 16.99m, "Shopping", "Clothing", 2),
			new("Flannel Shirt", "TA-FLN", 34.99m, "Shopping", "Clothing", 1),
			new("Chino Shorts", "TA-SHT", 27.99m, "Shopping", "Clothing", 2),
			new("Athletic Joggers", "TA-JOG", 32.99m, "Shopping", "Clothing", 1),
			new("Polo Shirt", "TA-POL", 24.99m, "Shopping", "Clothing", 2),
			new("Rain Jacket", "TA-RJK", 64.99m, "Shopping", "Clothing", 1),
			new("Baseball Cap", "TA-CAP", 18.99m, "Shopping", "Clothing", 2),
			new("Canvas Sneakers", "TA-SNK", 44.99m, "Shopping", "Clothing", 1),
			new("Wool Scarf", "TA-SCF", 22.99m, "Shopping", "Clothing", 1),
		]),
	];

	#endregion
}
