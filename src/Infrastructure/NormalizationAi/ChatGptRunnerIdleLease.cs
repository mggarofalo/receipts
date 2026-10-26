using System.Diagnostics;
using System.Globalization;
using Application.NormalizationAi;

namespace Infrastructure.NormalizationAi;

/// <summary>Holds both the API runner lock and the provider-child lifetime guard.
/// Missing/ambiguous process identity is busy, never an expired lease.</summary>
public sealed class ChatGptRunnerIdleLease : IDisposable
{
	private readonly FileStream runner;
	private readonly IDisposable child;
	private ChatGptRunnerIdleLease(FileStream runner, IDisposable child) { this.runner = runner; this.child = child; }
	public static ChatGptRunnerIdleLease Acquire(PiNormalizationRunnerOptions options)
	{
		string home = Path.GetFullPath(options.HomeDirectory);
		Directory.CreateDirectory(home);
		FileStream? runner = null;
		try
		{
			runner = new(Path.Combine(home, ".runner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			IDisposable child = AcquireChild(home);
			try
			{
				foreach (string permit in Directory.EnumerateFiles(home, ".permit-*")) { File.Delete(permit); }
				return new(runner, child);
			}
			catch { child.Dispose(); throw; }
		}
		catch (IOException) { runner?.Dispose(); throw new ChatGptRunnerException("busy", requestMayHaveStarted: false); }
		catch { runner?.Dispose(); throw; }
	}

	// Caller already holds .runner.lock. Also used before spawning a new provider child.
	internal static IDisposable AcquireChild(string home)
	{
		string path = Path.Combine(home, ".node.lock");
		try
		{
			if (File.Exists(path))
			{
				string[] identity = File.ReadAllText(path).Split('|');
				string processNamespace = OperatingSystem.IsLinux() ? new FileInfo("/proc/self/ns/pid").LinkTarget ?? "unknown" : "host";
				if (identity.Length != 3 || !string.Equals(identity[0], System.Net.Dns.GetHostName(), StringComparison.OrdinalIgnoreCase)
					|| identity[1] != processNamespace || processNamespace == "unknown"
					|| !int.TryParse(identity[2], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
				{ throw new ChatGptRunnerException("busy", requestMayHaveStarted: false); }
				try
				{
					using Process process = Process.GetProcessById(pid);
					if (!process.HasExited) { throw new ChatGptRunnerException("busy", requestMayHaveStarted: false); }
				}
				catch (ArgumentException) { /* OS confirms this PID no longer exists. */ }
				File.Delete(path);
			}
			FileStream marker = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
			try
			{
				string processNamespace = OperatingSystem.IsLinux() ? new FileInfo("/proc/self/ns/pid").LinkTarget ?? "unknown" : "host";
				byte[] bytes = System.Text.Encoding.UTF8.GetBytes(System.Net.Dns.GetHostName() + "|" + processNamespace + "|" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
				marker.Write(bytes); marker.Flush();
				return new ChildLease(marker, path);
			}
			catch { marker.Dispose(); throw; }
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
		{ throw new ChatGptRunnerException("busy", requestMayHaveStarted: false); }
	}
	public void Dispose() { try { child.Dispose(); } finally { runner.Dispose(); } }
	private sealed class ChildLease(FileStream marker, string path) : IDisposable
	{
		public void Dispose() { marker.Dispose(); File.Delete(path); }
	}
}
