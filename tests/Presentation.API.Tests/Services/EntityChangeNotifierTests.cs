using System.Security.Claims;
using API.Authentication;
using API.Hubs;
using API.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;

namespace Presentation.API.Tests.Services;

public class EntityChangeNotifierTests : IDisposable
{
	private readonly Mock<IHubContext<EntityHub, IEntityHubClient>> _hubContextMock;
	private readonly Mock<IEntityHubClient> _clientMock;
	private readonly Mock<IHttpContextAccessor> _httpContextAccessorMock;
	private readonly EntityChangeNotifier _notifier;

	public EntityChangeNotifierTests()
	{
		_clientMock = new Mock<IEntityHubClient>();
		_clientMock.Setup(c => c.EntityChanged(It.IsAny<EntityChangeNotification>()))
			.Returns(Task.CompletedTask);

		Mock<IHubClients<IEntityHubClient>> hubClientsMock = new();
		hubClientsMock.Setup(c => c.All).Returns(_clientMock.Object);

		_hubContextMock = new Mock<IHubContext<EntityHub, IEntityHubClient>>();
		_hubContextMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);

		_httpContextAccessorMock = new Mock<IHttpContextAccessor>();
		_httpContextAccessorMock.Setup(a => a.HttpContext).Returns((HttpContext?)null);

		// Use a very long flush interval so the timer never fires during tests
		_notifier = new EntityChangeNotifier(_hubContextMock.Object, _httpContextAccessorMock.Object, TimeSpan.FromHours(1));
	}

	public void Dispose()
	{
		_notifier.Dispose();
		GC.SuppressFinalize(this);
	}

	private static DefaultHttpContext CreateJwtHttpContext(string userId, string? connectionId = null)
	{
		var claims = new List<Claim>
		{
			new(ClaimTypes.NameIdentifier, userId),
		};
		var identity = new ClaimsIdentity(claims, "Bearer");
		var context = new DefaultHttpContext
		{
			User = new ClaimsPrincipal(identity),
		};
		if (connectionId is not null)
		{
			context.Request.Headers["X-SignalR-Connection-Id"] = connectionId;
		}
		return context;
	}

	private static DefaultHttpContext CreateApiKeyHttpContext(string userId, string? connectionId = null)
	{
		var claims = new List<Claim>
		{
			new(ClaimTypes.NameIdentifier, userId),
		};
		var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationDefaults.AuthenticationScheme);
		var context = new DefaultHttpContext
		{
			User = new ClaimsPrincipal(identity),
		};
		if (connectionId is not null)
		{
			context.Request.Headers["X-SignalR-Connection-Id"] = connectionId;
		}
		return context;
	}

	[Fact]
	public async Task NotifyCreated_EnqueuesAndFlushSendsNotificationWithCount1()
	{
		// Arrange
		Guid id = Guid.NewGuid();

		// Act
		await _notifier.NotifyCreated("receipt", id);
		await _notifier.FlushAsync();

		// Assert
		_clientMock.Verify(c => c.EntityChanged(
			It.Is<EntityChangeNotification>(n =>
				n.EntityType == "receipt" &&
				n.ChangeType == "created" &&
				n.Count == 1)),
			Times.Once);
	}

	[Fact]
	public async Task NotifyUpdated_EnqueuesAndFlushSendsNotificationWithCount1()
	{
		// Arrange
		Guid id = Guid.NewGuid();

		// Act
		await _notifier.NotifyUpdated("account", id);
		await _notifier.FlushAsync();

		// Assert
		_clientMock.Verify(c => c.EntityChanged(
			It.Is<EntityChangeNotification>(n =>
				n.EntityType == "account" &&
				n.ChangeType == "updated" &&
				n.Count == 1)),
			Times.Once);
	}

	[Fact]
	public async Task NotifyDeleted_EnqueuesAndFlushSendsNotificationWithCount1()
	{
		// Arrange
		Guid id = Guid.NewGuid();

		// Act
		await _notifier.NotifyDeleted("category", id);
		await _notifier.FlushAsync();

		// Assert
		_clientMock.Verify(c => c.EntityChanged(
			It.Is<EntityChangeNotification>(n =>
				n.EntityType == "category" &&
				n.ChangeType == "deleted" &&
				n.Count == 1)),
			Times.Once);
	}

	[Fact]
	public async Task NotifyBulkChanged_With3Ids_SendsOneNotificationWithCount3()
	{
		// Arrange
		List<Guid> ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

		// Act
		await _notifier.NotifyBulkChanged("transaction", "created", ids);
		await _notifier.FlushAsync();

		// Assert
		_clientMock.Verify(c => c.EntityChanged(
			It.Is<EntityChangeNotification>(n =>
				n.EntityType == "transaction" &&
				n.ChangeType == "created" &&
				n.Count == 3)),
			Times.Once);
		_clientMock.Verify(c => c.EntityChanged(It.IsAny<EntityChangeNotification>()), Times.Once);
	}

	[Fact]
	public async Task NotifyBulkChanged_WithEmptyIds_SendsNothingOnFlush()
	{
		// Act
		await _notifier.NotifyBulkChanged("receipt", "deleted", []);
		await _notifier.FlushAsync();

		// Assert
		_clientMock.Verify(c => c.EntityChanged(It.IsAny<EntityChangeNotification>()), Times.Never);
	}

	[Fact]
	public async Task NotifyAllChanged_SendsNotificationWithCount1AndNullId()
	{
		// Act
		await _notifier.NotifyAllChanged("category", "updated");
		await _notifier.FlushAsync();

		// Assert
		_clientMock.Verify(c => c.EntityChanged(
			It.Is<EntityChangeNotification>(n =>
				n.EntityType == "category" &&
				n.ChangeType == "updated" &&
				n.Id == null &&
				n.Count == 1)),
			Times.Once);
	}

	[Fact]
	public async Task MultipleDifferentPairs_ProduceSeparateNotificationsOnFlush()
	{
		// Arrange
		Guid id1 = Guid.NewGuid();
		Guid id2 = Guid.NewGuid();

		// Act
		await _notifier.NotifyCreated("receipt", id1);
		await _notifier.NotifyDeleted("category", id2);
		await _notifier.FlushAsync();

		// Assert
		_clientMock.Verify(c => c.EntityChanged(
			It.Is<EntityChangeNotification>(n =>
				n.EntityType == "receipt" &&
				n.ChangeType == "created" &&
				n.Count == 1)),
			Times.Once);
		_clientMock.Verify(c => c.EntityChanged(
			It.Is<EntityChangeNotification>(n =>
				n.EntityType == "category" &&
				n.ChangeType == "deleted" &&
				n.Count == 1)),
			Times.Once);
		_clientMock.Verify(c => c.EntityChanged(It.IsAny<EntityChangeNotification>()), Times.Exactly(2));
	}

	[Fact]
	public async Task Dispose_FlushesPendingNotifications()
	{
		// Arrange — use a separate notifier for this test so Dispose actually flushes
		var clientMock = new Mock<IEntityHubClient>();
		clientMock.Setup(c => c.EntityChanged(It.IsAny<EntityChangeNotification>()))
			.Returns(Task.CompletedTask);
		Mock<IHubClients<IEntityHubClient>> hubClientsMock = new();
		hubClientsMock.Setup(c => c.All).Returns(clientMock.Object);
		Mock<IHubContext<EntityHub, IEntityHubClient>> hubContextMock = new();
		hubContextMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);
		Mock<IHttpContextAccessor> httpContextAccessorMock = new();
		httpContextAccessorMock.Setup(a => a.HttpContext).Returns((HttpContext?)null);
		var notifier = new EntityChangeNotifier(hubContextMock.Object, httpContextAccessorMock.Object, TimeSpan.FromHours(1));

		Guid id = Guid.NewGuid();
		await notifier.NotifyCreated("receipt", id);

		// Act
		notifier.Dispose();

		// Assert
		clientMock.Verify(c => c.EntityChanged(
			It.Is<EntityChangeNotification>(n =>
				n.EntityType == "receipt" &&
				n.ChangeType == "created" &&
				n.Count == 1)),
			Times.Once);
	}

	[Fact]
	public async Task FlushAsync_WithNoPendingItems_SendsNothing()
	{
		// Act
		await _notifier.FlushAsync();

		// Assert
		_clientMock.Verify(c => c.EntityChanged(It.IsAny<EntityChangeNotification>()), Times.Never);
	}

	[Fact]
	public async Task NotifyCreated_WithJwtContext_SetsOriginFields()
	{
		// Arrange
		var context = CreateJwtHttpContext("user-123", "conn-abc");
		_httpContextAccessorMock.Setup(a => a.HttpContext).Returns(context);

		// Act
		await _notifier.NotifyCreated("receipt", Guid.NewGuid());
		await _notifier.FlushAsync();

		// Assert
		_clientMock.Verify(c => c.EntityChanged(
			It.Is<EntityChangeNotification>(n =>
				n.UserId == "user-123" &&
				n.AuthMethod == "jwt" &&
				n.ConnectionId == "conn-abc")),
			Times.Once);
	}

	[Fact]
	public async Task NotifyCreated_WithApiKeyContext_SetsAuthMethodToApikey()
	{
		// Arrange
		var context = CreateApiKeyHttpContext("user-456");
		_httpContextAccessorMock.Setup(a => a.HttpContext).Returns(context);

		// Act
		await _notifier.NotifyCreated("account", Guid.NewGuid());
		await _notifier.FlushAsync();

		// Assert
		_clientMock.Verify(c => c.EntityChanged(
			It.Is<EntityChangeNotification>(n =>
				n.UserId == "user-456" &&
				n.AuthMethod == "apikey" &&
				n.ConnectionId == null)),
			Times.Once);
	}

	[Fact]
	public async Task NotifyCreated_WithNoHttpContext_SetsNullOriginFields()
	{
		// Arrange — default mock already returns null HttpContext

		// Act
		await _notifier.NotifyCreated("receipt", Guid.NewGuid());
		await _notifier.FlushAsync();

		// Assert
		_clientMock.Verify(c => c.EntityChanged(
			It.Is<EntityChangeNotification>(n =>
				n.UserId == null &&
				n.AuthMethod == null &&
				n.ConnectionId == null)),
			Times.Once);
	}

	[Fact]
	public async Task NotifyCreated_WithUnauthenticatedIdentity_SetsAuthMethodToNull()
	{
		// Arrange — HttpContext exists but identity has no AuthenticationType
		var context = new DefaultHttpContext();
		_httpContextAccessorMock.Setup(a => a.HttpContext).Returns(context);

		// Act
		await _notifier.NotifyCreated("receipt", Guid.NewGuid());
		await _notifier.FlushAsync();

		// Assert
		_clientMock.Verify(c => c.EntityChanged(
			It.Is<EntityChangeNotification>(n =>
				n.UserId == null &&
				n.AuthMethod == null)),
			Times.Once);
	}

	// RECEIPTS-794: a send failure mid-flush must be logged and must NOT permanently drop
	// notifications that were already dequeued.
	private static (Mock<IHubContext<EntityHub, IEntityHubClient>> HubContext, List<EntityChangeNotification> Delivered) BuildFailableHub(Func<bool> shouldFail)
	{
		List<EntityChangeNotification> delivered = [];
		var clientMock = new Mock<IEntityHubClient>();
		clientMock
			.Setup(c => c.EntityChanged(It.IsAny<EntityChangeNotification>()))
			.Returns((EntityChangeNotification n) =>
			{
				if (shouldFail())
				{
					return Task.FromException(new InvalidOperationException("hub transport failure"));
				}

				delivered.Add(n);
				return Task.CompletedTask;
			});

		Mock<IHubClients<IEntityHubClient>> hubClientsMock = new();
		hubClientsMock.Setup(c => c.All).Returns(clientMock.Object);
		Mock<IHubContext<EntityHub, IEntityHubClient>> hubContextMock = new();
		hubContextMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);

		return (hubContextMock, delivered);
	}

	private EntityChangeNotifier CreateNotifier(Mock<IHubContext<EntityHub, IEntityHubClient>> hubContext, Mock<ILogger<EntityChangeNotifier>> logger)
	{
		// Very long flush interval so the timer never fires; tests drive FlushAsync directly.
		return new EntityChangeNotifier(hubContext.Object, _httpContextAccessorMock.Object, TimeSpan.FromHours(1), logger.Object);
	}

	[Fact]
	public async Task FlushAsync_WhenSendThrows_LogsErrorAndDoesNotThrow()
	{
		// Arrange
		bool fail = true;
		var (hubContext, _) = BuildFailableHub(() => fail);
		var loggerMock = new Mock<ILogger<EntityChangeNotifier>>();
		using EntityChangeNotifier notifier = CreateNotifier(hubContext, loggerMock);
		await notifier.NotifyCreated("receipt", Guid.NewGuid());

		// Act — the hub throws while sending; FlushAsync must swallow and log, not propagate.
		Func<Task> act = notifier.FlushAsync;

		// Assert
		await act.Should().NotThrowAsync();
		loggerMock.Verify(
			x => x.Log(
				LogLevel.Error,
				It.IsAny<EventId>(),
				It.IsAny<It.IsAnyType>(),
				It.IsAny<Exception>(),
				It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
			Times.AtLeastOnce);
	}

	[Fact]
	public async Task FlushAsync_WhenSendThrows_RequeuesSoNotificationIsDeliveredOnRetry()
	{
		// Arrange
		bool fail = true;
		var (hubContext, delivered) = BuildFailableHub(() => fail);
		var loggerMock = new Mock<ILogger<EntityChangeNotifier>>();
		using EntityChangeNotifier notifier = CreateNotifier(hubContext, loggerMock);
		await notifier.NotifyCreated("receipt", Guid.NewGuid());

		// Act — first flush fails (nothing delivered), then the hub recovers and we flush again.
		await notifier.FlushAsync();
		delivered.Should().BeEmpty("the send failed, so nothing was delivered yet");

		fail = false;
		await notifier.FlushAsync();

		// Assert — the notification was re-enqueued on failure and delivered on the retry,
		// with its original aggregated count, rather than being permanently dropped.
		delivered.Should().ContainSingle();
		delivered[0].EntityType.Should().Be("receipt");
		delivered[0].ChangeType.Should().Be("created");
		delivered[0].Count.Should().Be(1);
	}

	[Fact]
	public async Task FlushAsync_WhenSendThrows_PreservesNotYetAttemptedBuckets()
	{
		// Arrange — two distinct pending notifications; the first send throws.
		bool fail = true;
		var (hubContext, delivered) = BuildFailableHub(() => fail);
		var loggerMock = new Mock<ILogger<EntityChangeNotifier>>();
		using EntityChangeNotifier notifier = CreateNotifier(hubContext, loggerMock);
		await notifier.NotifyCreated("receipt", Guid.NewGuid());
		await notifier.NotifyDeleted("category", Guid.NewGuid());

		// Act — failing flush must re-enqueue BOTH the failed bucket and the one never attempted.
		await notifier.FlushAsync();
		fail = false;
		await notifier.FlushAsync();

		// Assert — both notifications survive and are delivered on retry.
		delivered.Should().HaveCount(2);
		delivered.Should().ContainSingle(n => n.EntityType == "receipt" && n.ChangeType == "created");
		delivered.Should().ContainSingle(n => n.EntityType == "category" && n.ChangeType == "deleted");
	}

	[Fact]
	public async Task SameEntityAndChange_DifferentOrigin_ProducesSeparateNotifications()
	{
		// Arrange — first call with user A
		var contextA = CreateJwtHttpContext("user-A", "conn-1");
		_httpContextAccessorMock.Setup(a => a.HttpContext).Returns(contextA);
		await _notifier.NotifyCreated("receipt", Guid.NewGuid());

		// Second call with user B
		var contextB = CreateJwtHttpContext("user-B", "conn-2");
		_httpContextAccessorMock.Setup(a => a.HttpContext).Returns(contextB);
		await _notifier.NotifyCreated("receipt", Guid.NewGuid());

		// Act
		await _notifier.FlushAsync();

		// Assert — two separate notifications for the same entity/change but different origins
		_clientMock.Verify(c => c.EntityChanged(
			It.Is<EntityChangeNotification>(n =>
				n.EntityType == "receipt" &&
				n.ChangeType == "created" &&
				n.UserId == "user-A")),
			Times.Once);
		_clientMock.Verify(c => c.EntityChanged(
			It.Is<EntityChangeNotification>(n =>
				n.EntityType == "receipt" &&
				n.ChangeType == "created" &&
				n.UserId == "user-B")),
			Times.Once);
		_clientMock.Verify(c => c.EntityChanged(It.IsAny<EntityChangeNotification>()), Times.Exactly(2));
	}
}
