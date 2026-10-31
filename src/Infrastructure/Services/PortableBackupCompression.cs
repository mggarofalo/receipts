using System.IO.Compression;

namespace Infrastructure.Services;

/// <summary>
/// Gzip container around a portable SQLite backup. The SQLite contents and their
/// export_version are unchanged; compression only shrinks the file for transfer.
/// Readers sniff the gzip magic, so raw SQLite backups of every format still restore.
/// </summary>
public static class PortableBackupCompression
{
	public const string FileExtension = ".db.gz";

	private const byte GzipMagic1 = 0x1F;
	private const byte GzipMagic2 = 0x8B;

	/// <summary>
	/// Compresses <paramref name="sqlitePath"/> to a new temporary file and returns its path.
	/// The source file is left in place; the caller owns both files.
	/// </summary>
	public static async Task<string> CompressToTempFileAsync(string sqlitePath, CancellationToken cancellationToken)
	{
		string compressedPath = Path.ChangeExtension(sqlitePath, null) + FileExtension;
		await using FileStream source = new(sqlitePath, FileMode.Open, FileAccess.Read, FileShare.Read,
			bufferSize: 81920, FileOptions.SequentialScan);
		// Opened outside the cleanup scope: a pre-existing file at this path is not ours to delete.
		FileStream destination = new(compressedPath, FileMode.CreateNew, FileAccess.Write);
		try
		{
			await using (destination)
			await using (GZipStream gzip = new(destination, CompressionLevel.Optimal))
			{
				await source.CopyToAsync(gzip, cancellationToken);
			}
		}
		catch
		{
			File.Delete(compressedPath);
			throw;
		}

		return compressedPath;
	}

	/// <summary>
	/// Copies a raw or gzip-compressed backup to <paramref name="destination"/> as raw SQLite.
	/// Fails once more than <paramref name="maxSqliteBytes"/> would be written, which bounds
	/// both uncompressed uploads and decompression of a hostile archive.
	/// </summary>
	public static async Task CopyAsSqliteAsync(Stream source, Stream destination, long maxSqliteBytes, CancellationToken cancellationToken)
	{
		byte[] header = new byte[2];
		int headerLength = await source.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
		Stream prefixed = new PrefixedStream(header.AsMemory(0, headerLength), source);
		bool compressed = headerLength == 2 && header[0] == GzipMagic1 && header[1] == GzipMagic2;

		await using Stream payload = compressed ? new GZipStream(prefixed, CompressionMode.Decompress) : prefixed;
		long totalCopied = 0;
		byte[] buffer = new byte[81920];
		int bytesRead;
		while ((bytesRead = await payload.ReadAsync(buffer, cancellationToken)) > 0)
		{
			totalCopied += bytesRead;
			if (totalCopied > maxSqliteBytes)
			{
				throw new InvalidOperationException($"SQLite file exceeds maximum allowed size of {maxSqliteBytes / (1024 * 1024)} MB.");
			}
			await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
		}
	}

	/// <summary>Replays already-sniffed header bytes before the rest of a forward-only stream.</summary>
	private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
	{
		private ReadOnlyMemory<byte> _prefix = prefix;

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

		public override int Read(Span<byte> buffer)
		{
			if (_prefix.IsEmpty)
			{
				return inner.Read(buffer);
			}

			int n = Math.Min(buffer.Length, _prefix.Length);
			_prefix.Span[..n].CopyTo(buffer);
			_prefix = _prefix[n..];
			return n;
		}

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
			=> ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			if (_prefix.IsEmpty)
			{
				return inner.ReadAsync(buffer, cancellationToken);
			}

			int n = Math.Min(buffer.Length, _prefix.Length);
			_prefix.Span[..n].CopyTo(buffer.Span);
			_prefix = _prefix[n..];
			return ValueTask.FromResult(n);
		}

		public override void Flush() { }
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}
