namespace Receipts.JevProductionParity;

/// <summary>
/// Frozen paired-run limits. Every case may need one baseline plus one selected-path attempt;
/// each attempt reserves 65,536 input tokens at USD 0.042/million. Construct only via <see cref="Create"/>.
/// </summary>
public sealed record EvaluationPolicy
{
	public const long PerAttemptNanodollars = 2_752_512;
	public const int MaximumCases = 48;
	public const long MaximumCeilingNanodollars = 5_000_000_000;
	public const double BaselineGate = 0.7;

	private EvaluationPolicy(int cases, long priorCarryNanodollars, long ceilingNanodollars,
		double autoWriteGlobalGate, double autoWriteSelectedGate)
	{
		Cases = cases;
		MaximumAttempts = 2 * cases;
		PriorCarryNanodollars = priorCarryNanodollars;
		CeilingNanodollars = ceilingNanodollars;
		AutoWriteGlobalGate = autoWriteGlobalGate;
		AutoWriteSelectedGate = autoWriteSelectedGate;
	}

	public int Cases { get; }
	public int MaximumAttempts { get; }
	public long PriorCarryNanodollars { get; }
	public long CeilingNanodollars { get; }
	public double AutoWriteGlobalGate { get; }
	public double AutoWriteSelectedGate { get; }
	public long MaximumReservationNanodollars => MaximumAttempts * PerAttemptNanodollars;
	public long CombinedMaximumReservationNanodollars => PriorCarryNanodollars + MaximumReservationNanodollars;

	public static EvaluationPolicy Create(int cases, int maximumAttempts, long priorCarryNanodollars,
		long ceilingNanodollars, double autoWriteGlobalGate, double autoWriteSelectedGate)
	{
		if (cases is < 1 or > MaximumCases || maximumAttempts != 2 * cases || priorCarryNanodollars < 0 ||
			ceilingNanodollars > MaximumCeilingNanodollars ||
			priorCarryNanodollars + (maximumAttempts * PerAttemptNanodollars) > ceilingNanodollars ||
			!IsGate(autoWriteGlobalGate) || !IsGate(autoWriteSelectedGate))
		{
			throw new ArgumentException("Invalid evaluation policy.");
		}

		return new(cases, priorCarryNanodollars, ceilingNanodollars, autoWriteGlobalGate, autoWriteSelectedGate);
	}

	private static bool IsGate(double gate) => double.IsFinite(gate) && gate is >= BaselineGate and <= 1;
}
