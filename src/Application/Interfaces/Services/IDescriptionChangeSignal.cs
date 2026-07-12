using System.Threading.Channels;

namespace Application.Interfaces.Services;

/// <summary>
/// Broadcasts "descriptions may have changed" hints from write paths (SaveChanges,
/// manual refresh) to every background consumer that reacts to them.
/// </summary>
/// <remarks>
/// Each consumer calls <see cref="Subscribe"/> exactly once to obtain its OWN wake-up
/// channel; <see cref="NotifyDirty"/> fans a signal out to all subscribers so a single
/// notification reaches every consumer. This replaces the earlier single shared channel,
/// where whichever consumer read first "stole" the wake-up and the others could miss a
/// signal — including a manual-refresh request (RECEIPTS-790).
/// </remarks>
public interface IDescriptionChangeSignal
{
	/// <summary>Signals every subscriber that description-related state may have changed.</summary>
	void NotifyDirty();

	/// <summary>
	/// Registers a new consumer and returns its private wake-up reader. Bursts coalesce into a
	/// single pending token per consumer, and <see cref="NotifyDirty"/> never blocks.
	/// </summary>
	ChannelReader<bool> Subscribe();
}
