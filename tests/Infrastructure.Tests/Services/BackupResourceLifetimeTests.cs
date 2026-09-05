using FluentAssertions;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Infrastructure.Tests.Services;

public class BackupResourceLifetimeTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Export_SourceCreationFailureOrCancellation_DoesNotLeaveOwnedArtifact(bool cancel)
	{
		using CancellationTokenSource cancellation = new();
		if (cancel)
		{
			cancellation.Cancel();
		}
		InvalidOperationException expected = new("Source unavailable");
		Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
		factory.Setup(row => row.CreateDbContextAsync(It.IsAny<CancellationToken>()))
			.Returns((CancellationToken token) => cancel ? Task.FromCanceled<ApplicationDbContext>(token) : Task.FromException<ApplicationDbContext>(expected));
		PathLogger logger = new();
		BackupService service = new(factory.Object, logger);
		try
		{
			Func<Task> export = () => service.ExportToSqliteAsync(cancellation.Token);
			if (cancel)
			{
				await export.Should().ThrowAsync<OperationCanceledException>();
			}
			else
			{
				var failure = await export.Should().ThrowAsync<InvalidOperationException>();
				failure.Which.Should().BeSameAs(expected);
			}
			if (logger.Path is { } path)
			{
				foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
				{
					File.Exists(path + suffix).Should().BeFalse();
				}
			}
		}
		finally
		{
			DeleteOwned(logger.Path);
		}
	}

	[Fact]
	public async Task Export_Success_ReturnsCallerOwnedReadableAndDeletableFile()
	{
		DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase($"backup-lifetime-{Guid.NewGuid():N}").Options;
		Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
		factory.Setup(row => row.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(options));
		PathLogger logger = new();
		BackupService service = new(factory.Object, logger);
		try
		{
			string path = await service.ExportToSqliteAsync();
			path.Should().Be(logger.Path);
			using (FileStream exclusive = new(path, FileMode.Open, FileAccess.Read, FileShare.None))
			{
				exclusive.Length.Should().BeGreaterThan(0);
			}
			File.Delete(path);
			File.Exists(path).Should().BeFalse("the caller owns cleanup after a successful export; no global SQLite pool reset is needed");
		}
		finally
		{
			DeleteOwned(logger.Path);
		}
	}

	private static void DeleteOwned(string? path)
	{
		if (path is not null)
		{
			foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
			{
				File.Delete(path + suffix);
			}
		}
	}

	private sealed class PathLogger : ILogger<BackupService>
	{
		public string? Path { get; private set; }
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(LogLevel logLevel) => true;
		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (state is IEnumerable<KeyValuePair<string, object?>> values)
			{
				Path ??= values.FirstOrDefault(row => row.Key == "Path").Value as string;
			}
		}
	}
}
