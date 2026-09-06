using API.Generated.Dtos;
using API.Validators;
using FluentAssertions;

namespace Presentation.API.Tests.Validators;

public class ItemTemplatePriceBoundsTests
{
	public static TheoryData<double?, bool> Prices => new()
	{
		{ null, true }, { 3.459, true }, { 7.1234, true }, { 99_999_999_999_990d, true },
		{ Math.BitDecrement(100_000_000_000_000d), false },
		{ 100_000_000_000_000d, false }, { 0, false }, { -1, false },
		{ double.MaxValue, false }, { double.MinValue, false },
		{ double.PositiveInfinity, false }, { double.NegativeInfinity, false }, { double.NaN, false }
	};

	[Theory]
	[MemberData(nameof(Prices))]
	public void CreateAndUpdate_RejectUnrepresentableValuesWithoutThrowing_AndPreserveNullableSubcentPrices(double? price, bool valid)
	{
		var create = new CreateItemTemplateRequestValidator().Validate(new CreateItemTemplateRequest { Name = "Milk", DefaultUnitPrice = price });
		var update = new UpdateItemTemplateRequestValidator().Validate(new UpdateItemTemplateRequest { Id = Guid.NewGuid(), Name = "Milk", DefaultUnitPrice = price });
		create.IsValid.Should().Be(valid);
		update.IsValid.Should().Be(valid);
		if (!valid)
		{
			create.Errors.Should().Contain(error => error.PropertyName == nameof(CreateItemTemplateRequest.DefaultUnitPrice));
			update.Errors.Should().Contain(error => error.PropertyName == nameof(UpdateItemTemplateRequest.DefaultUnitPrice));
		}
	}
}
