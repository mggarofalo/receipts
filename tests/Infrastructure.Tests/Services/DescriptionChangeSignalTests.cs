using System.Threading.Channels;
using FluentAssertions;
using Infrastructure.Services;

namespace Infrastructure.Tests.Services;

// RECEIPTS-790: a single shared capacity-1 channel let whichever background consumer read
// first "steal" the wake-up, so the other could miss a signal and stall until its safety
// timer. These tests pin the per-consumer fan-out behavior that replaced it.
public class DescriptionChangeSignalTests
{
	[Fact]
	public void NotifyDirty_ReachesEverySubscriber()
	{
		DescriptionChangeSignal signal = new();
		ChannelReader<bool> resolver = signal.Subscribe();
		ChannelReader<bool> refresher = signal.Subscribe();

		signal.NotifyDirty();

		resolver.TryRead(out _).Should().BeTrue("the first consumer must receive its own copy of the signal");
		refresher.TryRead(out _).Should().BeTrue("the second consumer must receive its own copy of the signal");
	}

	[Fact]
	public void NotifyDirty_OneConsumerDraining_DoesNotStealTheOthersWakeUp()
	{
		DescriptionChangeSignal signal = new();
		ChannelReader<bool> resolver = signal.Subscribe();
		ChannelReader<bool> refresher = signal.Subscribe();

		signal.NotifyDirty();

		// First consumer fully drains its own channel...
		resolver.TryRead(out _).Should().BeTrue();
		resolver.TryRead(out _).Should().BeFalse("its single pending token is consumed");

		// ...and the second consumer's token is still waiting for it.
		refresher.TryRead(out _).Should().BeTrue("the other consumer's wake-up must not have been stolen");
	}

	[Fact]
	public void NotifyDirty_Burst_CoalescesToOnePendingTokenPerConsumer()
	{
		DescriptionChangeSignal signal = new();
		ChannelReader<bool> reader = signal.Subscribe();

		signal.NotifyDirty();
		signal.NotifyDirty();
		signal.NotifyDirty();

		reader.TryRead(out _).Should().BeTrue();
		reader.TryRead(out _).Should().BeFalse("capacity-1 DropWrite coalesces a burst into a single pending token");
	}

	[Fact]
	public void NotifyDirty_WithNoSubscribers_DoesNotThrow()
	{
		DescriptionChangeSignal signal = new();

		Action act = signal.NotifyDirty;

		act.Should().NotThrow();
	}
}
