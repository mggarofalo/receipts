using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Taxonomy;
using Common;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class TaxonomyTests(PostgresFixture fixture)
{
	private const string Actor = "taxonomy-test-admin";
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }
	private TaxonomyService Service() => new(new Factory(fixture), TimeProvider.System);
	private async Task<TaxonomyRevision> StartAsync()
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();
		if (!await db.Users.AnyAsync(user => user.Id == Actor))
		{
			db.Users.Add(new ApplicationUser { Id = Actor, UserName = Actor });
			IdentityRole? role = await db.Roles.FirstOrDefaultAsync(role => role.Name == AppRoles.Admin);
			if (role == null) { role = new IdentityRole { Id = Guid.NewGuid().ToString(), Name = AppRoles.Admin }; db.Roles.Add(role); }
			db.UserRoles.Add(new() { UserId = Actor, RoleId = role.Id });
			await db.SaveChangesAsync();
		}
		return (await Service().ReadAsync(null))!;
	}
	private static TaxonomyChangeCommand Command(Guid revision, params TaxonomyEdit[] edits) => new(revision, Guid.NewGuid(), "Taxonomy acceptance", edits);

	[Fact]
	public async Task CommittedChangesAreVisibleToOtherReadersAndRollbackReplayAndNoopStaySilent()
	{
		TaxonomyRevision start = await StartAsync();
		Guid category = Guid.NewGuid();
		TaxonomyChangeCommand command = Command(start.Id, new TaxonomyEdit("create", category, Name: Guid.NewGuid().ToString()));
		Mock<ICommittedChangePublisher> publisher = new(MockBehavior.Strict);
		publisher.Setup(x => x.PublishAsync(It.Is<CommittedEntityChange>(x => x.EntityType == CommittedEntityType.Taxonomy && x.ChangeType == CommittedChangeType.Updated)))
			.Returns(async () =>
			{
				// A separate connection must see the committed head before notification.
				TaxonomyRevision visible = (await Service().ReadAsync(null))!;
				visible.Nodes.Should().Contain(x => x.Id == category);
			});
		TaxonomyService failing = new(new FailingFactory(fixture), TimeProvider.System, publisher.Object);
		Func<Task> rollback = () => failing.ChangeAsync(Actor, command);
		await rollback.Should().ThrowAsync<InvalidOperationException>();
		publisher.VerifyNoOtherCalls();
		TaxonomyService service = new(new Factory(fixture), TimeProvider.System, publisher.Object);
		TaxonomyRevision created = await service.ChangeAsync(Actor, command);
		await service.ChangeAsync(Actor, command);
		await service.ChangeAsync(Actor, Command(created.Id, new TaxonomyEdit("move", category)));
		publisher.Verify(x => x.PublishAsync(It.IsAny<CommittedEntityChange>()), Times.Once);
		TaxonomyRevision renamed = await service.ChangeAsync(Actor, Command(created.Id, new TaxonomyEdit("rename", category, Name: Guid.NewGuid().ToString())));
		Guid parent = Guid.NewGuid();
		TaxonomyRevision expanded = await service.ChangeAsync(Actor, Command(renamed.Id, new TaxonomyEdit("create", parent, Name: Guid.NewGuid().ToString())));
		TaxonomyRevision moved = await service.ChangeAsync(Actor, Command(expanded.Id, new TaxonomyEdit("move", category, parent)));
		await service.ChangeAsync(Actor, Command(moved.Id, new TaxonomyEdit("archive", category)));
		publisher.Verify(x => x.PublishAsync(It.IsAny<CommittedEntityChange>()), Times.Exactly(5));
		publisher.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task PublishedSnapshotsRejectAppendedNodesIncludingTheEmptyGenesis()
	{
		TaxonomyRevision start = await StartAsync();
		TaxonomyRevision published = await Service().ChangeAsync(Actor, Command(start.Id,
			new TaxonomyEdit("create", Guid.NewGuid(), Name: Guid.NewGuid().ToString())));
		await using ApplicationDbContext db = fixture.CreateDbContext();
		db.Database.HasPendingModelChanges().Should().BeFalse();
		long genesisStamp = await db.TaxonomyRevisions
			.Where(row => row.Id == Infrastructure.Configurations.TaxonomyConfiguration.GenesisId)
			.Select(row => EF.Property<long>(row, "WriteTransaction")).SingleAsync();
		genesisStamp.Should().Be(0);
		long publishedStamp = await db.TaxonomyRevisions.Where(row => row.Id == published.Id)
			.Select(row => EF.Property<long>(row, "WriteTransaction")).SingleAsync();
		publishedStamp.Should().BeGreaterThan(0);
		Guid category = Guid.NewGuid();
		await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO taxonomy.\"Identities\" (\"Id\") VALUES ({category})");
		foreach (Guid revision in new[] { published.Id, start.Id, Infrastructure.Configurations.TaxonomyConfiguration.GenesisId }.Distinct())
		{
			Func<Task> append = () => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO taxonomy.\"Nodes\" (\"RevisionId\", \"CategoryId\", \"Name\", \"Archived\") VALUES ({revision}, {category}, 'Injected historical node', false)");
			await append.Should().ThrowAsync<Npgsql.PostgresException>().Where(error => error.MessageText.Contains("sealed"));
		}
		(await Service().ReadAsync(start.Id)).Should().BeEquivalentTo(start);
	}

	[Fact]
	public async Task FullSnapshotsRetainHistoricalAncestryAcrossMultipleBranchesAndDefinitions()
	{
		TaxonomyRevision start = await StartAsync();
		Guid root = Guid.NewGuid(), other = Guid.NewGuid(), child = Guid.NewGuid(), leaf = Guid.NewGuid();
		string suffix = Guid.NewGuid().ToString();
		TaxonomyRevision first = await Service().ChangeAsync(Actor, Command(start.Id, new TaxonomyEdit("create", root, Name: "Grocery" + suffix),
			new TaxonomyEdit("create", other, Name: "Chilled" + suffix), new TaxonomyEdit("create", child, root, "Dairy"), new TaxonomyEdit("create", leaf, child, "Milk")));
		TaxonomyRevision second = await Service().ChangeAsync(Actor, Command(first.Id, new TaxonomyEdit("move", child, other), new TaxonomyEdit("rename", root, Name: "Food" + suffix), new TaxonomyEdit("define", child, Definition: "Dairy products")));
		TaxonomyRevision historical = (await Service().ReadAsync(first.Id))!;
		historical.Nodes.Single(node => node.Id == child).ParentId.Should().Be(root);
		historical.Nodes.Single(node => node.Id == root).Name.Should().Be("Grocery" + suffix);
		second.Nodes.Single(node => node.Id == child).ParentId.Should().Be(other);
		second.Nodes.Single(node => node.Id == leaf).ParentId.Should().Be(child);
		second.Sequence.Should().Be(first.Sequence + 1);
		second.Actor.Should().Be(Actor);
	}

	[Fact]
	public async Task ReplayNoopChangedBodyAndStaleHeadNeverDuplicateHistory()
	{
		TaxonomyRevision start = await StartAsync(); Guid node = Guid.NewGuid();
		TaxonomyChangeCommand create = Command(start.Id, new TaxonomyEdit("create", node, Name: Guid.NewGuid().ToString()));
		TaxonomyRevision first = await Service().ChangeAsync(Actor, create);
		(await Service().ChangeAsync(Actor, create)).Should().BeEquivalentTo(first);
		Func<Task> changed = () => Service().ChangeAsync(Actor, create with { Reason = "Different" });
		(await changed.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		Func<Task> stale = () => Service().ChangeAsync(Actor, Command(start.Id, new TaxonomyEdit("archive", node)));
		(await stale.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		TaxonomyRevision noop = await Service().ChangeAsync(Actor, Command(first.Id, new TaxonomyEdit("move", node)));
		noop.Id.Should().Be(first.Id); noop.AffectedCount.Should().Be(0);
		(await Service().ReadAsync(null))!.Sequence.Should().Be(first.Sequence);
	}

	[Fact]
	public async Task CycleFailureRollsBackBatchAndArchivedChildCannotRestoreAlone()
	{
		TaxonomyRevision start = await StartAsync(); Guid root = Guid.NewGuid(), child = Guid.NewGuid();
		TaxonomyRevision first = await Service().ChangeAsync(Actor, Command(start.Id, new TaxonomyEdit("create", root, Name: Guid.NewGuid().ToString()), new TaxonomyEdit("create", child, root, "Child")));
		Guid rejected = Guid.NewGuid();
		Func<Task> invalid = () => Service().ChangeAsync(Actor, Command(first.Id, new TaxonomyEdit("create", rejected, Name: "Rolled back"), new TaxonomyEdit("move", root, child)));
		(await invalid.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(400);
		(await Service().ReadAsync(null))!.Nodes.Should().NotContain(node => node.Id == rejected);
		TaxonomyRevision archived = await Service().ChangeAsync(Actor, Command(first.Id, new TaxonomyEdit("archive", root)));
		archived.AffectedCount.Should().Be(2);
		Func<Task> restore = () => Service().ChangeAsync(Actor, Command(archived.Id, new TaxonomyEdit("restore", child)));
		(await restore.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(400);
	}

	[Fact]
	public async Task ConcurrentWritersHaveOneWinnerAndHistoricalReadRemainsComplete()
	{
		TaxonomyRevision start = await StartAsync();
		TaxonomyChangeCommand a = Command(start.Id, new TaxonomyEdit("create", Guid.NewGuid(), Name: Guid.NewGuid().ToString()));
		TaxonomyChangeCommand b = Command(start.Id, new TaxonomyEdit("create", Guid.NewGuid(), Name: Guid.NewGuid().ToString()));
		async Task<int> Write(TaxonomyChangeCommand command)
		{ try { await Service().ChangeAsync(Actor, command); return 200; } catch (TaxonomyException error) { return error.Status; } }
		Task<TaxonomyRevision?> history = Service().ReadAsync(start.Id);
		int[] results = await Task.WhenAll(Write(a), Write(b));
		results.Should().BeEquivalentTo([200, 409]);
		(await history).Should().BeEquivalentTo(start);
		TaxonomyRevision current = (await Service().ReadAsync(null))!;
		current.Sequence.Should().Be(start.Sequence + 1);
		current.Nodes.Count.Should().Be(start.Nodes.Count + 1);
	}

	private sealed class FailingContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
	{
		public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
		{
			await base.SaveChangesAsync(cancellationToken);
			throw new InvalidOperationException("Injected failure after persistence, before outer commit.");
		}
	}
	private sealed class FailingFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => new FailingContext(fixture.CreateOptions()); }

	[Fact]
	public async Task FailureAfterSaveRollsBackSnapshotIdentityHeadAndOperationAndAllowsRetry()
	{
		TaxonomyRevision start = await StartAsync(); Guid category = Guid.NewGuid();
		TaxonomyChangeCommand command = Command(start.Id, new TaxonomyEdit("create", category, Name: Guid.NewGuid().ToString()));
		TaxonomyService failing = new(new FailingFactory(fixture), TimeProvider.System);
		Func<Task> actual = () => failing.ChangeAsync(Actor, command);
		await actual.Should().ThrowAsync<InvalidOperationException>();
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			(await db.TaxonomyIdentities.AnyAsync(row => row.Id == category)).Should().BeFalse();
			(await db.TaxonomyOperations.AnyAsync(row => row.Actor == Actor && row.OperationId == command.OperationId)).Should().BeFalse();
			(await db.TaxonomyHeads.SingleAsync()).RevisionId.Should().Be(start.Id);
			(await db.TaxonomyRevisions.CountAsync(row => row.Sequence > start.Sequence)).Should().Be(0);
		}
		(await Service().ChangeAsync(Actor, command)).Nodes.Should().Contain(node => node.Id == category);
	}

	[Fact]
	public async Task OrdinaryActorCannotMutateAndDatabaseRejectsHistoryRewrite()
	{
		TaxonomyRevision start = await StartAsync();
		Func<Task> forbidden = () => Service().ChangeAsync("ordinary-user", Command(start.Id, new TaxonomyEdit("create", Guid.NewGuid(), Name: "Forbidden")));
		(await forbidden.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(403);
		await using ApplicationDbContext db = fixture.CreateDbContext();
		Func<Task> rewrite = () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE taxonomy.\"Revisions\" SET \"Reason\" = 'rewrite' WHERE \"Id\" = {start.Id}");
		await rewrite.Should().ThrowAsync<Exception>();
		(await Service().ReadAsync(null))!.Id.Should().Be(start.Id);
	}
}
