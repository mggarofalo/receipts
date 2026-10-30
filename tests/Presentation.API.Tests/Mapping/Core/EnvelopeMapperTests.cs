using API.Generated.Dtos;
using API.Mapping.Core;
using Domain.Envelopes;
using FluentAssertions;

namespace Presentation.API.Tests.Mapping.Core;

public class EnvelopeMapperTests
{
	private readonly EnvelopeMapper _mapper = new();

	[Fact]
	public void ToResponse_Envelope_MapsAllFields()
	{
		// Arrange
		Envelope envelope = new(Guid.NewGuid(), "Groceries", false);

		// Act
		EnvelopeResponse actual = _mapper.ToResponse(envelope);

		// Assert
		actual.Should().BeEquivalentTo(new { envelope.Id, Name = "Groceries", IsActive = false });
	}

	[Fact]
	public void ToResponse_Occasion_MapsAllFields()
	{
		// Arrange
		Occasion occasion = new(Guid.NewGuid(), "Birthday");

		// Act
		OccasionResponse actual = _mapper.ToResponse(occasion);

		// Assert
		actual.Should().BeEquivalentTo(new { occasion.Id, Name = "Birthday", IsActive = true });
	}

	[Fact]
	public void ToDomain_CreateRequests_HaveEmptyIdAndRequestedState()
	{
		// Act
		Envelope envelope = _mapper.ToDomain(new CreateEnvelopeRequest { Name = "Groceries", IsActive = false });
		Occasion occasion = _mapper.ToDomain(new CreateOccasionRequest { Name = "Birthday", IsActive = true });

		// Assert
		envelope.Should().BeEquivalentTo(new Envelope(Guid.Empty, "Groceries", false));
		occasion.Should().BeEquivalentTo(new Occasion(Guid.Empty, "Birthday", true));
	}

	[Fact]
	public void ToDomain_UpdateRequests_UseRouteId()
	{
		// Arrange
		Guid id = Guid.NewGuid();

		// Act
		Envelope envelope = _mapper.ToDomain(id, new UpdateEnvelopeRequest { Name = "Groceries", IsActive = true });
		Occasion occasion = _mapper.ToDomain(id, new UpdateOccasionRequest { Name = "Birthday", IsActive = false });

		// Assert
		envelope.Should().BeEquivalentTo(new Envelope(id, "Groceries", true));
		occasion.Should().BeEquivalentTo(new Occasion(id, "Birthday", false));
	}
}
