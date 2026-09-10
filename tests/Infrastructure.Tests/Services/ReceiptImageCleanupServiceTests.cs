using Application.Interfaces.Services;
using Application.Models.Images;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Infrastructure.Tests.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Infrastructure.Tests.Services;

public class ReceiptImageCleanupServiceTests
{
	[Fact]
	public async Task ProcessCleanupAsync_UsesDurableActiveAndDeletedReferences_GraceCutoff_AndLogsCounts()
	{
		IDbContextFactory<ApplicationDbContext> factory = DbContextHelpers.CreateInMemoryContextFactory();
		await using (ApplicationDbContext seed = factory.CreateDbContext())
		{
			seed.Receipts.AddRange(
				new ReceiptEntity { Id = Guid.NewGuid(), Location = "Live", Date = new DateOnly(2026, 1, 1), OriginalImagePath = "live/o.jpg", ProcessedImagePath = "live/p.png" },
				new ReceiptEntity { Id = Guid.NewGuid(), Location = "Trash", Date = new DateOnly(2026, 1, 1), OriginalImagePath = "trash/o.jpg", ProcessedImagePath = "trash/p.png", DeletedAt = DateTimeOffset.UtcNow });
			await seed.SaveChangesAsync();
		}
		Mock<IImageStorageService> storage = new();
		IReadOnlySet<string>? observedPaths = null;
		DateTimeOffset observedCutoff = default;
		storage.Setup(x => x.CleanupUnreferencedAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
			.Callback<IReadOnlySet<string>, DateTimeOffset, CancellationToken>((paths, cutoff, _) =>
			{
				observedPaths = paths;
				observedCutoff = cutoff;
			})
			.ReturnsAsync(new ImageCleanupResult(2, 1));
		ServiceCollection services = new();
		services.AddSingleton(factory);
		services.AddSingleton(storage.Object);
		await using ServiceProvider provider = services.BuildServiceProvider();
		FakeTimeProvider time = new(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));
		Mock<ILogger<ReceiptImageCleanupService>> logger = new();
		ReceiptImageCleanupService service = new(provider.GetRequiredService<IServiceScopeFactory>(), time, logger.Object);

		ImageCleanupResult result = await service.ProcessCleanupAsync(CancellationToken.None);

		result.Should().Be(new ImageCleanupResult(2, 1));
		observedPaths.Should().BeEquivalentTo(["live/o.jpg", "live/p.png", "trash/o.jpg", "trash/p.png"]);
		observedCutoff.Should().Be(time.GetUtcNow() - ReceiptImageCleanupService.OrphanGracePeriod);
		logger.Verify(x => x.Log(LogLevel.Information, It.IsAny<EventId>(),
			It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("Cleaned 2", StringComparison.Ordinal)),
			It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
	}
}
