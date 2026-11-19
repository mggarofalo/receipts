using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Repositories;

public class YnabOAuthConnectionRepositoryTests
{
	private static readonly DateTimeOffset ConnectedAt = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
	private static readonly DateTimeOffset ExpiresAt = ConnectedAt.AddHours(2);
	private static readonly DateTimeOffset Later = ConnectedAt.AddHours(1);

	private readonly IDbContextFactory<ApplicationDbContext> _contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
	private readonly YnabOAuthConnectionRepository _repository;

	public YnabOAuthConnectionRepositoryTests()
	{
		_repository = new YnabOAuthConnectionRepository(_contextFactory);
	}

	private Task<YnabOAuthConnectionEntity> SaveFirstConnectionAsync() =>
		_repository.SaveConnectedAsync("protected-1", ExpiresAt, "admin-1", ConnectedAt, CancellationToken.None);

	private async Task<int> CountRowsAsync()
	{
		await using ApplicationDbContext context = _contextFactory.CreateDbContext();
		return await context.YnabOAuthConnections.CountAsync();
	}

	[Fact]
	public async Task GetAsync_NoConnection_ReturnsNull()
	{
		// Act
		YnabOAuthConnectionEntity? row = await _repository.GetAsync(CancellationToken.None);

		// Assert
		row.Should().BeNull();
	}

	[Fact]
	public async Task SaveConnectedAsync_NoConnection_CreatesTheSingleConnectedRow()
	{
		// Act
		YnabOAuthConnectionEntity saved = await SaveFirstConnectionAsync();

		// Assert
		YnabOAuthConnectionEntity? row = await _repository.GetAsync(CancellationToken.None);
		row.Should().NotBeNull();
		row!.Id.Should().Be(YnabOAuthConnectionRepository.SingletonId);
		row.Status.Should().Be(YnabOAuthConnectionStatus.Connected);
		row.ProtectedTokens.Should().Be("protected-1");
		row.AccessTokenExpiresAtUtc.Should().Be(ExpiresAt);
		row.ConnectedAtUtc.Should().Be(ConnectedAt);
		row.ConnectedByUserId.Should().Be("admin-1");
		row.RefreshedAtUtc.Should().BeNull();
		row.StatusChangedAtUtc.Should().Be(ConnectedAt);
		row.ConnectionId.Should().NotBe(Guid.Empty).And.Be(saved.ConnectionId);
		row.ConcurrencyStamp.Should().NotBe(Guid.Empty).And.Be(saved.ConcurrencyStamp);
		(await CountRowsAsync()).Should().Be(1);
	}

	[Fact]
	public async Task SaveConnectedAsync_ExistingConnection_ReplacesItUnderANewConnectionId()
	{
		// Arrange
		YnabOAuthConnectionEntity first = await SaveFirstConnectionAsync();
		await _repository.TryReplaceTokensAsync(first.ConcurrencyStamp, "protected-1b", ExpiresAt, Later, CancellationToken.None);

		// Act
		YnabOAuthConnectionEntity second = await _repository.SaveConnectedAsync(
			"protected-2", ExpiresAt.AddDays(1), "admin-2", ConnectedAt.AddDays(1), CancellationToken.None);

		// Assert
		YnabOAuthConnectionEntity? row = await _repository.GetAsync(CancellationToken.None);
		row!.ConnectionId.Should().Be(second.ConnectionId).And.NotBe(first.ConnectionId);
		row.ConcurrencyStamp.Should().NotBe(first.ConcurrencyStamp);
		row.Status.Should().Be(YnabOAuthConnectionStatus.Connected);
		row.ProtectedTokens.Should().Be("protected-2");
		row.AccessTokenExpiresAtUtc.Should().Be(ExpiresAt.AddDays(1));
		row.ConnectedAtUtc.Should().Be(ConnectedAt.AddDays(1));
		row.ConnectedByUserId.Should().Be("admin-2");
		row.RefreshedAtUtc.Should().BeNull("the refresh belonged to the earlier connection");
		(await CountRowsAsync()).Should().Be(1);
	}

	[Fact]
	public async Task SaveConnectedAsync_ReconnectRequiredRow_BecomesConnectedAgain()
	{
		// Arrange
		YnabOAuthConnectionEntity first = await SaveFirstConnectionAsync();
		await _repository.MarkReconnectRequiredAsync(first.ConnectionId, Later, CancellationToken.None);

		// Act
		await _repository.SaveConnectedAsync("protected-2", ExpiresAt, "admin-1", Later, CancellationToken.None);

		// Assert
		YnabOAuthConnectionEntity? row = await _repository.GetAsync(CancellationToken.None);
		row!.Status.Should().Be(YnabOAuthConnectionStatus.Connected);
		row.ProtectedTokens.Should().Be("protected-2");
		row.StatusChangedAtUtc.Should().Be(Later);
	}

	[Fact]
	public async Task TryReplaceTokensAsync_CurrentStamp_ReplacesTokensAndKeepsTheConnection()
	{
		// Arrange
		YnabOAuthConnectionEntity first = await SaveFirstConnectionAsync();

		// Act
		bool replaced = await _repository.TryReplaceTokensAsync(
			first.ConcurrencyStamp, "protected-2", ExpiresAt.AddHours(1), Later, CancellationToken.None);

		// Assert
		replaced.Should().BeTrue();
		YnabOAuthConnectionEntity? row = await _repository.GetAsync(CancellationToken.None);
		row!.ProtectedTokens.Should().Be("protected-2");
		row.AccessTokenExpiresAtUtc.Should().Be(ExpiresAt.AddHours(1));
		row.RefreshedAtUtc.Should().Be(Later);
		row.ConcurrencyStamp.Should().NotBe(first.ConcurrencyStamp);
		row.ConnectionId.Should().Be(first.ConnectionId);
		row.ConnectedAtUtc.Should().Be(ConnectedAt);
		row.ConnectedByUserId.Should().Be("admin-1");
		row.Status.Should().Be(YnabOAuthConnectionStatus.Connected);
	}

	[Fact]
	public async Task TryReplaceTokensAsync_StaleStamp_ReturnsFalseAndChangesNothing()
	{
		// Arrange — a second writer replaces the tokens first, so the first reader's stamp is stale.
		YnabOAuthConnectionEntity first = await SaveFirstConnectionAsync();
		await _repository.TryReplaceTokensAsync(first.ConcurrencyStamp, "protected-winner", ExpiresAt, Later, CancellationToken.None);
		YnabOAuthConnectionEntity? afterWinner = await _repository.GetAsync(CancellationToken.None);

		// Act
		bool replaced = await _repository.TryReplaceTokensAsync(
			first.ConcurrencyStamp, "protected-loser", ExpiresAt.AddHours(5), Later.AddMinutes(1), CancellationToken.None);

		// Assert
		replaced.Should().BeFalse();
		YnabOAuthConnectionEntity? row = await _repository.GetAsync(CancellationToken.None);
		row.Should().BeEquivalentTo(afterWinner);
		row!.ProtectedTokens.Should().Be("protected-winner");
	}

	[Fact]
	public async Task TryReplaceTokensAsync_NoRow_ReturnsFalseAndCreatesNothing()
	{
		// Act
		bool replaced = await _repository.TryReplaceTokensAsync(
			Guid.NewGuid(), "protected-2", ExpiresAt, Later, CancellationToken.None);

		// Assert
		replaced.Should().BeFalse();
		(await CountRowsAsync()).Should().Be(0);
	}

	[Fact]
	public async Task TryReplaceTokensAsync_ReconnectRequiredRow_ReturnsFalseAndKeepsTokensCleared()
	{
		// Arrange — the stamp is current, so only the status can be what refuses the write.
		YnabOAuthConnectionEntity first = await SaveFirstConnectionAsync();
		await _repository.MarkReconnectRequiredAsync(first.ConnectionId, Later, CancellationToken.None);
		YnabOAuthConnectionEntity? marked = await _repository.GetAsync(CancellationToken.None);

		// Act
		bool replaced = await _repository.TryReplaceTokensAsync(
			marked!.ConcurrencyStamp, "protected-2", ExpiresAt, Later, CancellationToken.None);

		// Assert
		replaced.Should().BeFalse();
		YnabOAuthConnectionEntity? row = await _repository.GetAsync(CancellationToken.None);
		row!.Status.Should().Be(YnabOAuthConnectionStatus.ReconnectRequired);
		row.ProtectedTokens.Should().BeNull();
	}

	[Fact]
	public async Task MarkReconnectRequiredAsync_CurrentConnection_ClearsTokensAndChangesStatus()
	{
		// Arrange
		YnabOAuthConnectionEntity first = await SaveFirstConnectionAsync();

		// Act
		bool marked = await _repository.MarkReconnectRequiredAsync(first.ConnectionId, Later, CancellationToken.None);

		// Assert
		marked.Should().BeTrue();
		YnabOAuthConnectionEntity? row = await _repository.GetAsync(CancellationToken.None);
		row!.Status.Should().Be(YnabOAuthConnectionStatus.ReconnectRequired);
		row.ProtectedTokens.Should().BeNull();
		row.AccessTokenExpiresAtUtc.Should().BeNull();
		row.StatusChangedAtUtc.Should().Be(Later);
		row.ConcurrencyStamp.Should().NotBe(first.ConcurrencyStamp);
		row.ConnectionId.Should().Be(first.ConnectionId);
	}

	[Fact]
	public async Task MarkReconnectRequiredAsync_DifferentConnectionId_ChangesNothing()
	{
		// Arrange — the caller is holding the id of a connection that has since been replaced.
		YnabOAuthConnectionEntity first = await SaveFirstConnectionAsync();
		YnabOAuthConnectionEntity second = await _repository.SaveConnectedAsync(
			"protected-2", ExpiresAt, "admin-1", Later, CancellationToken.None);

		// Act
		bool marked = await _repository.MarkReconnectRequiredAsync(first.ConnectionId, Later.AddMinutes(1), CancellationToken.None);

		// Assert
		marked.Should().BeFalse();
		YnabOAuthConnectionEntity? row = await _repository.GetAsync(CancellationToken.None);
		row!.Status.Should().Be(YnabOAuthConnectionStatus.Connected);
		row.ProtectedTokens.Should().Be("protected-2");
		row.ConcurrencyStamp.Should().Be(second.ConcurrencyStamp);
		row.StatusChangedAtUtc.Should().Be(Later);
	}

	[Fact]
	public async Task MarkReconnectRequiredAsync_AlreadyReconnectRequired_ReturnsFalseAndKeepsTheFirstTimestamp()
	{
		// Arrange
		YnabOAuthConnectionEntity first = await SaveFirstConnectionAsync();
		await _repository.MarkReconnectRequiredAsync(first.ConnectionId, Later, CancellationToken.None);

		// Act
		bool marked = await _repository.MarkReconnectRequiredAsync(first.ConnectionId, Later.AddHours(3), CancellationToken.None);

		// Assert
		marked.Should().BeFalse();
		YnabOAuthConnectionEntity? row = await _repository.GetAsync(CancellationToken.None);
		row!.StatusChangedAtUtc.Should().Be(Later);
	}

	[Fact]
	public async Task MarkReconnectRequiredAsync_NoRow_ReturnsFalse()
	{
		// Act
		bool marked = await _repository.MarkReconnectRequiredAsync(Guid.NewGuid(), Later, CancellationToken.None);

		// Assert
		marked.Should().BeFalse();
		(await CountRowsAsync()).Should().Be(0);
	}

	[Fact]
	public async Task DeleteAsync_ExistingConnection_RemovesTheRow()
	{
		// Arrange
		await SaveFirstConnectionAsync();

		// Act
		bool deleted = await _repository.DeleteAsync(CancellationToken.None);

		// Assert
		deleted.Should().BeTrue();
		(await _repository.GetAsync(CancellationToken.None)).Should().BeNull();
		(await CountRowsAsync()).Should().Be(0);
	}

	[Fact]
	public async Task DeleteAsync_NoRow_ReturnsFalse()
	{
		// Act
		bool deleted = await _repository.DeleteAsync(CancellationToken.None);

		// Assert
		deleted.Should().BeFalse();
	}
}
