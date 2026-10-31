using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.NormalizationAi;
using FluentAssertions;
using Infrastructure.Entities.NormalizationAi;
using Infrastructure.NormalizationAi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.Tests.NormalizationAi;

public sealed class ChatGptCredentialBindingTests : IDisposable
{
	private readonly string home = Path.Combine(Path.GetTempPath(), "receipts-binding-tests-" + Guid.NewGuid().ToString("N"));
	[Fact]
	public void DurableEvidenceRequiresSameOwnerEpochAndExplicitConsent()
	{
		Directory.CreateDirectory(home);
		var binding = new ChatGptCredentialBinding(new() { HomeDirectory = home });
		Guid epoch = Guid.NewGuid();
		binding.Matches("owner", epoch).Should().BeFalse("legacy credentials are not consent");
		binding.Publish("owner", epoch, DateTimeOffset.UtcNow);
		var restarted = new ChatGptCredentialBinding(new() { HomeDirectory = home });
		restarted.Matches("owner", epoch).Should().BeTrue();
		restarted.Matches("other-owner", epoch).Should().BeFalse();
		restarted.Matches("owner", Guid.NewGuid()).Should().BeFalse("restored or rotated DB epoch must match the volume");
		restarted.Invalidate();
		binding.Matches("owner", epoch).Should().BeFalse("restoring old DB rows cannot restore deleted volume evidence");
	}
	[Fact]
	public void NewSessionCannotBeClaimedByRestoringOldGeneration()
	{
		Directory.CreateDirectory(home);
		var binding = new ChatGptCredentialBinding(new() { HomeDirectory = home });
		Guid oldEpoch = Guid.NewGuid(), currentEpoch = Guid.NewGuid();
		binding.Publish("owner", oldEpoch, DateTimeOffset.UtcNow);
		binding.Invalidate();
		binding.Publish("owner", currentEpoch, DateTimeOffset.UtcNow);
		binding.Matches("owner", oldEpoch).Should().BeFalse();
		binding.Matches("owner", currentEpoch).Should().BeTrue();
	}
	[Fact]
	public void MissingConsentAndCorruptEvidenceFailClosed()
	{
		Directory.CreateDirectory(home);
		var binding = new ChatGptCredentialBinding(new() { HomeDirectory = home });
		Guid epoch = Guid.NewGuid();
		binding.Publish("owner", epoch, default);
		binding.Matches("owner", epoch).Should().BeFalse();
		File.WriteAllText(Path.Combine(home, ".receipts-consent.json"), "invalid");
		binding.Matches("owner", epoch).Should().BeFalse();
	}
	[Fact]
	public async Task ConnectionAdmissionPreservesValidBindingAndDeniesLegacyAndChangedOwner()
	{
		Directory.CreateDirectory(home);
		var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
		var factory = new Factory(options);
		Guid epoch = Guid.NewGuid();
		await using (var context = factory.CreateDbContext())
		{
			context.NormalizationAiConnection.Add(new() { Generation = epoch });
			await context.SaveChangesAsync();
		}
		var settings = new NormalizationAiOptions { Enabled = true, OwnerUserId = "owner" };
		var runner = new Mock<IChatGptRunner>(MockBehavior.Strict);
		var binding = new ChatGptCredentialBinding(new() { HomeDirectory = home });
		var service = new NormalizationAiService(factory, runner.Object,
			Options.Create(settings), TimeProvider.System, Mock.Of<ICommittedChangePublisher>(),
			runnerOptions: Options.Create(new PiNormalizationRunnerOptions { HomeDirectory = home }));
		(await service.GetConnectionAsync("owner", default)).CredentialsPresent.Should().BeFalse();
		runner.VerifyNoOtherCalls();
		binding.Publish("owner", epoch, DateTimeOffset.UtcNow);
		runner.Setup(x => x.GetStatusAsync(default)).ReturnsAsync(new ChatGptRunnerStatus(true));
		runner.Setup(x => x.GetModelsAsync(default)).ReturnsAsync(new[] { "one", "two" });
		(await service.GetConnectionAsync("owner", default)).Models.Should().Equal("one", "two");
		(await service.GetConnectionAsync("owner", default)).Generation.Should().Be(epoch);
		binding.Matches("owner", epoch).Should().BeTrue("status/model discovery preserves consent");
		settings.OwnerUserId = "new-owner";
		(await service.GetConnectionAsync("new-owner", default)).CredentialsPresent.Should().BeFalse();
		binding.Exists.Should().BeFalse("observed owner changes invalidate volume evidence");
		settings.OwnerUserId = "owner";
		(await service.GetConnectionAsync("owner", default)).CredentialsPresent.Should().BeFalse();
	}
	[Fact]
	public async Task BindingRequiresSuccessfulGuardedFinalizationAndOldEpochCannotBeRestored()
	{
		Directory.CreateDirectory(home);
		var factory = new Factory(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
		var binding = new ChatGptCredentialBinding(new() { HomeDirectory = home });
		var service = new NormalizationAiService(factory, Mock.Of<IChatGptRunner>(),
			Options.Create(new NormalizationAiOptions { Enabled = true, OwnerUserId = "owner" }), TimeProvider.System, Mock.Of<ICommittedChangePublisher>(),
			runnerOptions: Options.Create(new PiNormalizationRunnerOptions { HomeDirectory = home }));
		Guid change = await service.BeginConnectionChangeAsync(default);
		await service.CompleteConnectionChangeAsync(change, default);
		binding.Exists.Should().BeFalse("failure completion cannot grant consent");
		Func<Task> stale = () => service.BindConnectionChangeAsync(change, "owner", default);
		(await stale.Should().ThrowAsync<NormalizationAiException>()).Which.Code.Should().Be("connection_changed");
		change = await service.BeginConnectionChangeAsync(default);
		await service.BindConnectionChangeAsync(change, "owner", default);
		Guid boundEpoch;
		await using (var context = factory.CreateDbContext()) { boundEpoch = (await context.NormalizationAiConnection.SingleAsync()).Generation; }
		binding.Matches("owner", boundEpoch).Should().BeTrue();
		await service.BeginConnectionChangeAsync(default);
		binding.Matches("owner", boundEpoch).Should().BeFalse("begin invalidates volume evidence before rotating database state");
		await using (var context = factory.CreateDbContext())
		{
			var connection = await context.NormalizationAiConnection.SingleAsync();
			connection.Generation = boundEpoch; connection.Changing = false;
			await context.SaveChangesAsync();
		}
		(await service.GetConnectionAsync("owner", default)).CredentialsPresent.Should().BeFalse();
	}
	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
	}
	public void Dispose() { if (Directory.Exists(home)) { Directory.Delete(home, true); } }
}
