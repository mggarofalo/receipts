using Receipts.JevProductionParity;

namespace Infrastructure.Tests.Jev;

public class EvaluationPolicyTests
{
	[Fact]
	public void PilotPolicyKeepsFrozenReservationArithmetic()
	{
		EvaluationPolicy actual = Policies.Pilot;
		Assert.Equal(48, actual.MaximumAttempts);
		Assert.Equal(132_120_576, actual.MaximumReservationNanodollars);
		Assert.Equal(4_882_120_576, actual.CombinedMaximumReservationNanodollars);
		Assert.Equal(65_536 * 42, EvaluationPolicy.PerAttemptNanodollars);
	}

	[Theory]
	[InlineData(0, 0, 0L, 5_000_000_000L, 0.7, 0.7)]
	[InlineData(49, 98, 0L, 5_000_000_000L, 0.7, 0.7)]
	[InlineData(4, 7, 0L, 5_000_000_000L, 0.7, 0.7)]
	[InlineData(4, 9, 0L, 5_000_000_000L, 0.7, 0.7)]
	[InlineData(4, 8, -1L, 5_000_000_000L, 0.7, 0.7)]
	[InlineData(4, 8, 0L, 5_000_000_001L, 0.7, 0.7)]
	[InlineData(4, 8, 4_990_000_000L, 5_000_000_000L, 0.7, 0.7)]
	[InlineData(24, 48, 4_750_000_000L, 4_882_120_575L, 0.7, 0.7)]
	[InlineData(4, 8, 0L, 5_000_000_000L, 0.69, 0.8)]
	[InlineData(4, 8, 0L, 5_000_000_000L, 0.9, 1.01)]
	[InlineData(4, 8, 0L, 5_000_000_000L, double.NaN, 0.8)]
	[InlineData(4, 8, 0L, 5_000_000_000L, 0.9, double.PositiveInfinity)]
	public void InvalidPoliciesAreRejected(int cases, int attempts, long carry, long ceiling, double global, double selected)
	{
		Assert.Throws<ArgumentException>(() => EvaluationPolicy.Create(cases, attempts, carry, ceiling, global, selected));
	}

	[Fact]
	public void BoundaryPolicyIsAccepted()
	{
		EvaluationPolicy actual = EvaluationPolicy.Create(24, 48, 4_750_000_000, 4_882_120_576, 1, 0.7);
		Assert.Equal(actual.CeilingNanodollars, actual.CombinedMaximumReservationNanodollars);
	}
}
