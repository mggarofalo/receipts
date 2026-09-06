using System.Text.Json;

namespace SampleData;

public static class ReceiptArithmeticVectors
{
	public static IReadOnlyList<ReceiptArithmeticVector> Cases { get; } = JsonSerializer.Deserialize<ReceiptArithmeticVectorFile>(
		File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-data", "receipt-arithmetic.json")),
		new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Cases;

	public static ReceiptArithmeticVector Find(string id) => Cases.Single(vector => vector.Id == id);
}

public sealed record ReceiptArithmeticVectorFile(int Version, List<ReceiptArithmeticVector> Cases);
public sealed record ReceiptArithmeticLine(double Quantity, double UnitPrice, decimal Total);
public sealed record ReceiptArithmeticVector(
	string Id, List<ReceiptArithmeticLine> Lines, double Tax, List<double> Adjustments, List<double> Payments,
	decimal Subtotal, decimal AdjustmentTotal, decimal ExpectedTotal, decimal PaymentTotal,
	decimal AbsoluteDifference, bool ExpectedExceedsPayments, bool IsWithinCreationTolerance,
	bool HasVisibleDiscrepancy, decimal ReconciliationAdjustment, bool IsReconciled);
