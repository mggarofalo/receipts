using Application.Exceptions;
using Application.Interfaces.Services;
using Domain.Core;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Moq;

namespace Infrastructure.Tests.Services;

public class ItemTemplateServiceTests
{
	private readonly Mock<IItemTemplateRepository> _mockRepository = new();
	private readonly Mock<INormalizedDescriptionService> _mockNormalizedDescriptions = new();
	private readonly ItemTemplateService _service;

	public ItemTemplateServiceTests()
	{
		_service = new ItemTemplateService(
			_mockRepository.Object,
			new ItemTemplateMapper(),
			_mockNormalizedDescriptions.Object);
	}

	private static NormalizedDescription Canonical(Guid id, string name) =>
		new(id, name, NormalizedDescriptionStatus.Active, DateTimeOffset.UtcNow);

	// ── RECEIPTS-881: templates own a canonical entry ─────────────────────────

	[Fact]
	public async Task CreateAsync_LinksTheTemplateToItsCanonicalEntry()
	{
		Guid canonicalId = Guid.NewGuid();
		_mockNormalizedDescriptions
			.Setup(n => n.GetOrCreateForTemplateAsync("Gallon of Milk", It.IsAny<CancellationToken>()))
			.ReturnsAsync(Canonical(canonicalId, "Gallon of Milk"));

		List<ItemTemplateEntity> captured = [];
		_mockRepository
			.Setup(r => r.CreateAsync(It.IsAny<List<ItemTemplateEntity>>(), It.IsAny<CancellationToken>()))
			.Callback<List<ItemTemplateEntity>, CancellationToken>((entities, _) => captured = entities)
			.ReturnsAsync((List<ItemTemplateEntity> entities, CancellationToken _) => entities);

		await _service.CreateAsync(
			[new ItemTemplate(Guid.NewGuid(), "Gallon of Milk")],
			CancellationToken.None);

		// The link is what lets items entered from this template skip the resolver. Without it
		// the template is inert and the whole issue is undone.
		captured.Should().ContainSingle().Which.NormalizedDescriptionId.Should().Be(canonicalId);
	}

	[Fact]
	public async Task UpdateAsync_RepointsARenamedTemplateAtItsNewCanonicalEntry()
	{
		Guid renamedCanonicalId = Guid.NewGuid();
		_mockNormalizedDescriptions
			.Setup(n => n.GetOrCreateForTemplateAsync("Whole Milk", It.IsAny<CancellationToken>()))
			.ReturnsAsync(Canonical(renamedCanonicalId, "Whole Milk"));

		List<ItemTemplateEntity> captured = [];
		_mockRepository
			.Setup(r => r.UpdateAsync(It.IsAny<List<ItemTemplateEntity>>(), It.IsAny<CancellationToken>()))
			.Callback<List<ItemTemplateEntity>, CancellationToken>((entities, _) => captured = entities)
			.Returns(Task.CompletedTask);

		await _service.UpdateAsync(
			[new ItemTemplate(Guid.NewGuid(), "Whole Milk")],
			CancellationToken.None);

		// A rename changes what the template declares, so it must follow. The old canonical entry
		// is deliberately left alone — other receipt items may still be grouped under it.
		captured.Should().ContainSingle().Which.NormalizedDescriptionId.Should().Be(renamedCanonicalId);
	}

	[Fact]
	public async Task CreateAsync_StillSavesTheTemplateWhenTheRegistryIsUnavailable()
	{
		_mockNormalizedDescriptions
			.Setup(n => n.GetOrCreateForTemplateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("embedding service is down"));

		List<ItemTemplateEntity> captured = [];
		_mockRepository
			.Setup(r => r.CreateAsync(It.IsAny<List<ItemTemplateEntity>>(), It.IsAny<CancellationToken>()))
			.Callback<List<ItemTemplateEntity>, CancellationToken>((entities, _) => captured = entities)
			.ReturnsAsync((List<ItemTemplateEntity> entities, CancellationToken _) => entities);

		List<ItemTemplate> created = await _service.CreateAsync(
			[new ItemTemplate(Guid.NewGuid(), "Gallon of Milk")],
			CancellationToken.None);

		// The canonical link is a convenience, not a precondition. Refusing to save somebody's
		// template because the classifier is down would trade a working feature for a bookkeeping
		// one; an unlinked template links on its next use.
		created.Should().ContainSingle();
		captured.Should().ContainSingle().Which.NormalizedDescriptionId.Should().BeNull();
	}

	[Fact]
	public async Task CreateAsync_CancellationIsNotSwallowedAsAFailedLink()
	{
		using CancellationTokenSource cts = new();
		await cts.CancelAsync();

		_mockNormalizedDescriptions
			.Setup(n => n.GetOrCreateForTemplateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new OperationCanceledException());

		// A cancelled request must not look like "the registry was unavailable" and quietly
		// continue writing — the caller has gone away.
		Func<Task> act = async () => await _service.CreateAsync(
			[new ItemTemplate(Guid.NewGuid(), "Gallon of Milk")],
			cts.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
		_mockRepository.Verify(
			r => r.CreateAsync(It.IsAny<List<ItemTemplateEntity>>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task RestoreAsync_WhenActiveNameConflict_ThrowsDuplicateEntityException_AndDoesNotRestore()
	{
		// Arrange — an active template already owns the trashed row's name (RECEIPTS-772).
		Guid id = Guid.NewGuid();
		_mockRepository
			.Setup(r => r.GetRestoreConflictNameAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync("Gallon of Milk");

		// Act
		Func<Task> act = async () => await _service.RestoreAsync(id, CancellationToken.None);

		// Assert
		(await act.Should().ThrowAsync<DuplicateEntityException>())
			.Which.Message.Should().Contain("Gallon of Milk");
		_mockRepository.Verify(r => r.RestoreAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task RestoreAsync_WhenNoConflict_DelegatesToRepository_ReturnsTrue()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mockRepository
			.Setup(r => r.GetRestoreConflictNameAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);
		_mockRepository
			.Setup(r => r.RestoreAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		bool actual = await _service.RestoreAsync(id, CancellationToken.None);

		// Assert
		actual.Should().BeTrue();
		_mockRepository.Verify(r => r.RestoreAsync(id, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task RestoreAsync_WhenNoConflictAndNotFound_ReturnsFalse()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mockRepository
			.Setup(r => r.GetRestoreConflictNameAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);
		_mockRepository
			.Setup(r => r.RestoreAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		bool actual = await _service.RestoreAsync(id, CancellationToken.None);

		// Assert
		actual.Should().BeFalse();
	}
}
