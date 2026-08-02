using System.Threading.Channels;
using Application.Interfaces.Services;

namespace Infrastructure.Services;

// Fan-out implementation of IDescriptionChangeSignal. A single shared capacity-1 channel would
// mean multiple consumers competing for one wake-up token — whichever read first would steal
// it, so the others could miss a signal and stall until their own safety timer. Instead every
// consumer Subscribe()s once and gets its OWN capacity-1 channel, and NotifyDirty writes to all
// of them, so a single signal reaches every consumer (RECEIPTS-790).
public class DescriptionChangeSignal : IDescriptionChangeSignal
{
	private readonly object _gate = new();
	private readonly List<Channel<bool>> _subscribers = [];

	public ChannelReader<bool> Subscribe()
	{
		// Capacity-1 + DropWrite: a burst of NotifyDirty calls coalesces into a single pending
		// token per consumer, and NotifyDirty never blocks even while a consumer is mid-cycle.
		Channel<bool> channel = Channel.CreateBounded<bool>(
			new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

		lock (_gate)
		{
			_subscribers.Add(channel);
		}

		return channel.Reader;
	}

	public void NotifyDirty()
	{
		lock (_gate)
		{
			foreach (Channel<bool> channel in _subscribers)
			{
				// Non-blocking; DropWrite means an already-pending token is simply kept.
				channel.Writer.TryWrite(true);
			}
		}
	}
}
