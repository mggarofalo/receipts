using API.Generated.Dtos;
using API.Mapping.Core;
using Common;
using Domain;
using Domain.Core;

namespace Presentation.API.Tests.Mapping.Core;

public class ReceiptMapperTests
{
	private readonly ReceiptMapper _mapper = new();

	[Fact]
	public void ToDomain_FromCreateRequest_MapsAllPropertiesWithEmptyId()
	{
		// Arrange
		CreateReceiptRequest request = new()
		{
			Location = "Grocery Store",
			Date = new DateOnly(2025, 3, 15),
			TaxAmount = 5.75
		};

		// Act
		Receipt actual = _mapper.ToDomain(request);

		// Assert
		Assert.Equal(Guid.Empty, actual.Id);
		Assert.Equal("Grocery Store", actual.Location);
		Assert.Equal(new DateOnly(2025, 3, 15), actual.Date);
		Assert.Equal(5.75m, actual.TaxAmount.Amount);
		Assert.Equal(Currency.USD, actual.TaxAmount.Currency);
	}

	[Fact]
	public void ToDomain_FromUpdateRequest_MapsAllPropertiesIncludingId()
	{
		// Arrange
		Guid expectedId = Guid.NewGuid();
		UpdateReceiptRequest request = new()
		{
			Id = expectedId,
			Location = "Updated Store",
			Date = new DateOnly(2025, 6, 20),
			TaxAmount = 12.50
		};

		// Act
		Receipt actual = _mapper.ToDomain(request);

		// Assert
		Assert.Equal(expectedId, actual.Id);
		Assert.Equal("Updated Store", actual.Location);
		Assert.Equal(new DateOnly(2025, 6, 20), actual.Date);
		Assert.Equal(12.50m, actual.TaxAmount.Amount);
		Assert.Equal(Currency.USD, actual.TaxAmount.Currency);
	}

	[Fact]
	public void ToResponse_MapsAllProperties()
	{
		// Arrange
		Guid expectedId = Guid.NewGuid();
		Receipt receipt = new(
			expectedId,
			"Electronics Store",
			new DateOnly(2025, 4, 10),
			new Money(8.99m, Currency.USD)
		);

		// Act
		ReceiptResponse actual = _mapper.ToResponse(receipt);

		// Assert
		Assert.Equal(expectedId, actual.Id);
		Assert.Equal("Electronics Store", actual.Location);
		Assert.Equal(new DateOnly(2025, 4, 10), actual.Date);
		Assert.Equal((double)8.99m, actual.TaxAmount);
	}

	[Fact]
	public void ToResponse_FlattensMoneyAmountToDouble()
	{
		// Arrange
		Guid expectedId = Guid.NewGuid();
		Receipt receipt = new(
			expectedId,
			"Test Store",
			new DateOnly(2025, 1, 1),
			new Money(15.7531m, Currency.USD)
		);

		// Act
		ReceiptResponse actual = _mapper.ToResponse(receipt);

		// Assert
		Assert.Equal((double)15.7531m, actual.TaxAmount);
	}

	[Fact]
	public void ToDomain_FromCreateRequest_MapsPaidFromEnvelopeAndOccasion()
	{
		// Arrange
		Guid envelopeId = Guid.NewGuid(), occasionId = Guid.NewGuid();
		CreateReceiptRequest request = new()
		{
			Location = "Store",
			Date = new DateOnly(2025, 3, 15),
			TaxAmount = 0,
			PaidFromEnvelopeId = envelopeId,
			OccasionId = occasionId,
		};

		// Act
		Receipt actual = _mapper.ToDomain(request);

		// Assert
		Assert.Equal(envelopeId, actual.PaidFromEnvelopeId);
		Assert.Equal(occasionId, actual.OccasionId);
	}

	[Fact]
	public void ToDomain_FromUpdateRequest_MapsPaidFromEnvelopeAndOccasion()
	{
		// Arrange
		Guid envelopeId = Guid.NewGuid(), occasionId = Guid.NewGuid();
		UpdateReceiptRequest request = new()
		{
			Id = Guid.NewGuid(),
			Location = "Store",
			Date = new DateOnly(2025, 3, 15),
			TaxAmount = 0,
			PaidFromEnvelopeId = envelopeId,
			OccasionId = occasionId,
		};

		// Act
		Receipt actual = _mapper.ToDomain(request);

		// Assert
		Assert.Equal(envelopeId, actual.PaidFromEnvelopeId);
		Assert.Equal(occasionId, actual.OccasionId);
	}

	[Fact]
	public void ToResponse_ExposesPaidFromEnvelopeAndOccasion()
	{
		// Arrange
		Guid envelopeId = Guid.NewGuid(), occasionId = Guid.NewGuid();
		Receipt receipt = new(Guid.NewGuid(), "Store", new DateOnly(2025, 4, 10), new Money(0m))
		{
			PaidFromEnvelopeId = envelopeId,
			OccasionId = occasionId,
		};

		// Act
		ReceiptResponse actual = _mapper.ToResponse(receipt);

		// Assert
		Assert.Equal(envelopeId, actual.PaidFromEnvelopeId);
		Assert.Equal(occasionId, actual.OccasionId);
	}
}
