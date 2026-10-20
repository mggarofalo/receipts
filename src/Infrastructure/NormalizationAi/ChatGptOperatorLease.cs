using Application.NormalizationAi;

namespace Infrastructure.NormalizationAi;

/// <summary>
/// Serializes an operator's complete database-fence/authentication/database-completion
/// sequence across processes. The OS releases the lease if the operator process crashes.
/// This is separate from the runner lock, so an operator can fence then cancel an active request.
/// </summary>
public sealed class ChatGptOperatorLease : IDisposable
{
	private readonly FileStream lease;

	private ChatGptOperatorLease(FileStream lease) => this.lease = lease;

	public static ChatGptOperatorLease Acquire(PiNormalizationRunnerOptions options)
	{
		string home = Path.GetFullPath(options.HomeDirectory);
		Directory.CreateDirectory(home);
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}

		try
		{
			return new(new FileStream(Path.Combine(home, ".operator.lock"), FileMode.OpenOrCreate,
				FileAccess.ReadWrite, FileShare.None));
		}
		catch (IOException) { throw new ChatGptRunnerException("operator_busy"); }
	}

	public void Dispose() => lease.Dispose();
}
