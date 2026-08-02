using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Infrastructure.Tests.Services;

/// <summary>
/// The candidate aggregation is Postgres-specific raw SQL (DISTINCT ON, schema-qualified tables),
/// so the InMemory provider cannot execute it. These tests pin the parts that are provider-agnostic:
/// the service takes its context from the injected factory and does not swallow query failures.
/// The aggregation semantics themselves are covered by
/// <c>Infrastructure.IntegrationTests.Services.ItemTemplateHistoryCandidateServiceTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class ItemTemplateHistoryCandidateServiceTests
{
	private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;

	public ItemTemplateHistoryCandidateServiceTests()
	{
		(_contextFactory, MockCurrentUserAccessor accessor) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		accessor.UserId = "test-user";
	}

	[Fact]
	public async Task GetHistoryCandidatesAsync_ResolvesContextFromFactory()
	{
		// Arrange — wrap the real in-memory factory so we can observe the call without
		// mocking the DbContext itself (EF cannot execute against a mocked context).
		Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
		factoryMock.Setup(f => f.CreateDbContext()).Returns(() => _contextFactory.CreateDbContext());

		ItemTemplateHistoryCandidateService service = new(factoryMock.Object);

		// Act — InMemory rejects SqlQueryRaw, which is expected here.
		try
		{
			await service.GetHistoryCandidatesAsync(0, 50, 2, CancellationToken.None);
		}
		catch (InvalidOperationException)
		{
			// Expected: InMemory does not support raw SQL queries.
		}

		// Assert
		factoryMock.Verify(f => f.CreateDbContext(), Times.Once);
	}

	[Fact]
	public async Task GetHistoryCandidatesAsync_PropagatesQueryFailure()
	{
		// Arrange
		ItemTemplateHistoryCandidateService service = new(_contextFactory);

		// Act
		Func<Task> act = async () => await service.GetHistoryCandidatesAsync(0, 50, 2, CancellationToken.None);

		// Assert — the service must not swallow provider failures into an empty page.
		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	[Fact]
	public async Task GetHistoryCandidatesAsync_PropagatesFactoryFailure()
	{
		// Arrange
		Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
		factoryMock.Setup(f => f.CreateDbContext()).Throws(new InvalidOperationException("no connection"));

		ItemTemplateHistoryCandidateService service = new(factoryMock.Object);

		// Act
		Func<Task> act = async () => await service.GetHistoryCandidatesAsync(0, 50, 2, CancellationToken.None);

		// Assert
		(await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("no connection");
	}
}
