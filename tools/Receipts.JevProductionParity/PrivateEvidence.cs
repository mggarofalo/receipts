namespace Receipts.JevProductionParity;

/// <summary>Private evidence lives outside the checkout and is readable only by its owner.</summary>
public static class PrivateEvidence
{
	/// <summary>Rejects any path inside the repository, so private evidence cannot be committed.</summary>
	public static void RequireOutside(string repository, params string[] paths)
	{
		string repo = Path.GetFullPath(repository).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		foreach (string path in paths)
		{
			if (Path.GetFullPath(path).StartsWith(repo, StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException("Private path required.");
			}
		}
	}

	/// <summary>Creates a new owner-only file. Never replaces existing evidence.</summary>
	public static FileStream CreateNew(string path) => Open(path, FileMode.CreateNew, FileAccess.Write);

	/// <summary>Opens an exclusive handle; a file it creates is owner-only (0600 on Unix).</summary>
	public static FileStream Open(string path, FileMode mode, FileAccess access)
	{
		FileStreamOptions options = new() { Mode = mode, Access = access, Share = FileShare.None };
		if (!OperatingSystem.IsWindows())
		{
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		}

		return new(path, options);
	}
}
