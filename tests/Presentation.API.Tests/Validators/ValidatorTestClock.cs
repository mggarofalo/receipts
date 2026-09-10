using Application.Validation;

namespace Presentation.API.Tests.Validators;

internal static class ValidatorTestClock
{
	internal static readonly DateOnly Today = new(2040, 5, 6);
	internal static readonly AdmissionDatePolicy Policy = new(
		new FixedTimeProvider(new DateTimeOffset(2040, 5, 6, 12, 0, 0, TimeSpan.Zero)));

	private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => utcNow;
	}
}
