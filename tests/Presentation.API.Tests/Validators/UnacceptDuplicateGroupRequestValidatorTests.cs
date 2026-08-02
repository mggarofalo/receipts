using API.Generated.Dtos;
using API.Validators;
using FluentAssertions;
using FluentValidation.Results;

namespace Presentation.API.Tests.Validators;

public class UnacceptDuplicateGroupRequestValidatorTests
{
	private readonly UnacceptDuplicateGroupRequestValidator _validator = new();

	[Fact]
	public void Valid_Request_Passes()
	{
		// Arrange
		UnacceptDuplicateGroupRequest request = new()
		{
			ReceiptIds = [Guid.NewGuid(), Guid.NewGuid()],
		};

		// Act
		ValidationResult result = _validator.Validate(request);

		// Assert
		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void Single_ReceiptId_Fails()
	{
		UnacceptDuplicateGroupRequest request = new() { ReceiptIds = [Guid.NewGuid()] };

		ValidationResult result = _validator.Validate(request);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e => e.ErrorMessage == UnacceptDuplicateGroupRequestValidator.ReceiptIdsTooFew);
	}

	[Fact]
	public void SameReceiptIdRepeated_Fails()
	{
		Guid receiptId = Guid.NewGuid();
		UnacceptDuplicateGroupRequest request = new() { ReceiptIds = [receiptId, receiptId] };

		ValidationResult result = _validator.Validate(request);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e => e.ErrorMessage == UnacceptDuplicateGroupRequestValidator.ReceiptIdsTooFew);
	}

	[Fact]
	public void EmptyGuid_Fails()
	{
		UnacceptDuplicateGroupRequest request = new() { ReceiptIds = [Guid.NewGuid(), Guid.Empty] };

		ValidationResult result = _validator.Validate(request);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e => e.ErrorMessage == UnacceptDuplicateGroupRequestValidator.ReceiptIdMustNotBeEmpty);
	}

	[Fact]
	public void MoreThanTheAcceptCap_Passes()
	{
		// The load-bearing asymmetry. Accepted groups are connected components, and components merge
		// whenever an acceptance bridges two of them — so a group can exceed the accept cap without
		// any single accept call doing so. Undo posts every member, so capping it the same way made
		// such a group permanently un-undoable. Un-accepting is a single linear DELETE that creates
		// no rows, so the quadratic rationale behind the accept cap does not apply here.
		int overTheAcceptCap = AcceptDuplicateGroupRequestValidator.MaxReceiptIds + 2;
		UnacceptDuplicateGroupRequest request = new()
		{
			ReceiptIds = [.. Enumerable.Range(0, overTheAcceptCap).Select(_ => Guid.NewGuid())],
		};

		ValidationResult result = _validator.Validate(request);

		result.IsValid.Should().BeTrue(
			"a group larger than the accept cap must still be undoable");
	}

	[Fact]
	public void TheSameRequestSize_IsRejectedByTheAcceptValidator()
	{
		// Pins the asymmetry from the other side: if someone later "harmonises" the two validators by
		// adding a cap here, this pair of tests documents why that breaks undo.
		int overTheAcceptCap = AcceptDuplicateGroupRequestValidator.MaxReceiptIds + 2;
		List<Guid> ids = [.. Enumerable.Range(0, overTheAcceptCap).Select(_ => Guid.NewGuid())];

		ValidationResult unaccept = _validator.Validate(new UnacceptDuplicateGroupRequest { ReceiptIds = ids });
		ValidationResult accept = new AcceptDuplicateGroupRequestValidator()
			.Validate(new AcceptDuplicateGroupRequest { ReceiptIds = ids });

		unaccept.IsValid.Should().BeTrue();
		accept.IsValid.Should().BeFalse("accepting stores one row per pair, so it stays capped");
	}
}
