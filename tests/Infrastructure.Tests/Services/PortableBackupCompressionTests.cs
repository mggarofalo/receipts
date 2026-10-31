using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Infrastructure.Services;

namespace Infrastructure.Tests.Services;

public class PortableBackupCompressionTests : IDisposable
{
	private const long Unbounded = long.MaxValue;
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"portable-compression-{Guid.NewGuid():N}");

	public PortableBackupCompressionTests() => Directory.CreateDirectory(_directory);

	[Fact]
	public async Task CompressThenCopyAsSqlite_RoundTripsIdenticalBytes()
	{
		// Arrange
		byte[] original = SqliteLikeBytes(200_000);
		string sqlitePath = await WriteAsync("backup.db", original);

		// Act
		string compressedPath = await PortableBackupCompression.CompressToTempFileAsync(sqlitePath, CancellationToken.None);
		byte[] restored = await CopyAsync(File.OpenRead(compressedPath), Unbounded);

		// Assert
		(await File.ReadAllBytesAsync(compressedPath)).Take(2).Should().Equal(0x1F, 0x8B);
		restored.Should().Equal(original);
		File.Exists(sqlitePath).Should().BeTrue("the caller owns the source file");
	}

	[Fact]
	public async Task CopyAsSqlite_RawSqliteHeader_PassesThroughIdenticalBytes()
	{
		// Arrange
		byte[] original = SqliteLikeBytes(100_000);

		// Act
		byte[] restored = await CopyAsync(new MemoryStream(original), Unbounded);

		// Assert
		restored.Should().Equal(original);
	}

	[Theory]
	[InlineData(new byte[0])]
	[InlineData(new byte[] { 0x1F })]
	[InlineData(new byte[] { 0x53 })]
	[InlineData(new byte[] { 0x8B, 0x1F })]
	public async Task CopyAsSqlite_TinyRawStream_PassesThroughIdenticalBytes(byte[] original)
	{
		// Act
		byte[] restored = await CopyAsync(new MemoryStream(original), Unbounded);

		// Assert
		restored.Should().Equal(original);
	}

	[Fact]
	public async Task CopyAsSqlite_OneBytePerReadForwardOnlyRawStream_PassesThroughIdenticalBytes()
	{
		// Arrange
		byte[] original = SqliteLikeBytes(5_000);

		// Act
		byte[] restored = await CopyAsync(new TricklingStream(original), Unbounded);

		// Assert
		restored.Should().Equal(original);
	}

	[Fact]
	public async Task CopyAsSqlite_OneBytePerReadForwardOnlyGzipStream_Decompresses()
	{
		// Arrange
		byte[] original = SqliteLikeBytes(5_000);

		// Act
		byte[] restored = await CopyAsync(new TricklingStream(Gzip(original)), Unbounded);

		// Assert
		restored.Should().Equal(original);
	}

	[Fact]
	public async Task CopyAsSqlite_RawStreamOverCap_ThrowsAndStopsAtCap()
	{
		// Arrange
		const long cap = 1024 * 1024;
		byte[] original = new byte[(int)cap + 1];
		using MemoryStream destination = new();

		// Act
		Func<Task> copy = () => PortableBackupCompression.CopyAsSqliteAsync(new MemoryStream(original), destination, cap, CancellationToken.None);

		// Assert
		await copy.Should().ThrowAsync<InvalidOperationException>().WithMessage("SQLite file exceeds maximum allowed size*");
		destination.Length.Should().BeLessThanOrEqualTo(cap);
	}

	[Fact]
	public async Task CopyAsSqlite_GzipBombOverCap_ThrowsAndStopsAtCap()
	{
		// Arrange
		const long cap = 1024 * 1024;
		byte[] bomb = GzipZeros(megabytes: 64);
		CountingStream source = new(bomb);
		using MemoryStream destination = new();

		// Act
		Func<Task> copy = () => PortableBackupCompression.CopyAsSqliteAsync(source, destination, cap, CancellationToken.None);

		// Assert
		bomb.Length.Should().BeLessThan((int)cap, "the archive itself is small; only its expansion is hostile");
		await copy.Should().ThrowAsync<InvalidOperationException>().WithMessage("SQLite file exceeds maximum allowed size*");
		destination.Length.Should().BeLessThanOrEqualTo(cap);
		source.BytesRead.Should().BeLessThan(bomb.Length, "decompression stops once the cap is crossed");
	}

	[Fact]
	public async Task CopyAsSqlite_RawStreamExactlyAtCap_Succeeds()
	{
		// Arrange
		byte[] original = SqliteLikeBytes(100_000);

		// Act
		byte[] restored = await CopyAsync(new MemoryStream(original), original.Length);

		// Assert
		restored.Should().Equal(original);
	}

	[Fact]
	public async Task CopyAsSqlite_GzipExactlyAtCap_Succeeds()
	{
		// Arrange
		byte[] original = SqliteLikeBytes(100_000);

		// Act
		byte[] restored = await CopyAsync(new MemoryStream(Gzip(original)), original.Length);

		// Assert
		restored.Should().Equal(original);
	}

	[Fact]
	public async Task CopyAsSqlite_CorruptGzipAfterValidMagic_ThrowsInvalidDataException()
	{
		// Arrange
		byte[] corrupt = [0x1F, 0x8B, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
		using MemoryStream destination = new();

		// Act
		Func<Task> copy = () => PortableBackupCompression.CopyAsSqliteAsync(new MemoryStream(corrupt), destination, Unbounded, CancellationToken.None);

		// Assert
		await copy.Should().ThrowAsync<InvalidDataException>();
	}

	[Fact]
	public async Task CopyAsSqlite_CorruptDeflatePayload_ThrowsInvalidDataException()
	{
		// Arrange
		byte[] gzip = Gzip(SqliteLikeBytes(50_000));
		for (int i = 10; i < 40; i++)
		{
			gzip[i] = 0xFF;
		}
		using MemoryStream destination = new();

		// Act
		Func<Task> copy = () => PortableBackupCompression.CopyAsSqliteAsync(new MemoryStream(gzip), destination, Unbounded, CancellationToken.None);

		// Assert
		await copy.Should().ThrowAsync<InvalidDataException>();
	}

	[Theory]
	[InlineData("receipts-backup.db", "receipts-backup.db.gz")]
	[InlineData("tmpA1B2.tmp", "tmpA1B2.db.gz")]
	[InlineData("noextension", "noextension.db.gz")]
	public async Task CompressToTempFile_NamesOutputNextToSource(string sourceName, string expectedName)
	{
		// Arrange
		string sqlitePath = await WriteAsync(sourceName, SqliteLikeBytes(1_000));

		// Act
		string compressedPath = await PortableBackupCompression.CompressToTempFileAsync(sqlitePath, CancellationToken.None);

		// Assert
		compressedPath.Should().Be(Path.Combine(_directory, expectedName));
		File.Exists(compressedPath).Should().BeTrue();
	}

	[Fact]
	public async Task CompressToTempFile_Cancelled_LeavesNoCompressedFile()
	{
		// Arrange
		string sqlitePath = await WriteAsync("cancelled.db", SqliteLikeBytes(500_000));
		using CancellationTokenSource cancellation = new();
		await cancellation.CancelAsync();

		// Act
		Func<Task> compress = () => PortableBackupCompression.CompressToTempFileAsync(sqlitePath, cancellation.Token);

		// Assert
		await compress.Should().ThrowAsync<OperationCanceledException>();
		File.Exists(Path.Combine(_directory, "cancelled.db.gz")).Should().BeFalse();
		File.Exists(sqlitePath).Should().BeTrue();
	}

	[Fact]
	public async Task CompressToTempFile_PreexistingCompressedFile_ThrowsAndLeavesItIntact()
	{
		// Arrange
		string sqlitePath = await WriteAsync("collision.db", SqliteLikeBytes(1_000));
		byte[] unrelated = Encoding.ASCII.GetBytes("someone else's file");
		string existingPath = await WriteAsync("collision.db.gz", unrelated);

		// Act
		Func<Task> compress = () => PortableBackupCompression.CompressToTempFileAsync(sqlitePath, CancellationToken.None);

		// Assert
		await compress.Should().ThrowAsync<IOException>();
		(await File.ReadAllBytesAsync(existingPath)).Should().Equal(unrelated);
	}

	[Fact]
	public async Task CompressToTempFile_MissingSource_ThrowsAndLeavesNoCompressedFile()
	{
		// Arrange
		string sqlitePath = Path.Combine(_directory, "missing.db");

		// Act
		Func<Task> compress = () => PortableBackupCompression.CompressToTempFileAsync(sqlitePath, CancellationToken.None);

		// Assert
		await compress.Should().ThrowAsync<FileNotFoundException>();
		File.Exists(Path.Combine(_directory, "missing.db.gz")).Should().BeFalse();
	}

	private async Task<string> WriteAsync(string name, byte[] contents)
	{
		string path = Path.Combine(_directory, name);
		await File.WriteAllBytesAsync(path, contents);
		return path;
	}

	private static async Task<byte[]> CopyAsync(Stream source, long cap)
	{
		await using (source)
		{
			using MemoryStream destination = new();
			await PortableBackupCompression.CopyAsSqliteAsync(source, destination, cap, CancellationToken.None);
			return destination.ToArray();
		}
	}

	private static byte[] SqliteLikeBytes(int length)
	{
		byte[] bytes = new byte[length];
		new Random(1087).NextBytes(bytes);
		byte[] header = Encoding.ASCII.GetBytes("SQLite format 3\0");
		header.CopyTo(bytes, 0);
		return bytes;
	}

	private static byte[] GzipZeros(int megabytes)
	{
		byte[] chunk = new byte[1024 * 1024];
		using MemoryStream output = new();
		using (GZipStream gzip = new(output, CompressionLevel.Optimal, leaveOpen: true))
		{
			for (int i = 0; i < megabytes; i++)
			{
				gzip.Write(chunk);
			}
		}
		return output.ToArray();
	}

	private static byte[] Gzip(byte[] contents)
	{
		using MemoryStream output = new();
		using (GZipStream gzip = new(output, CompressionLevel.Optimal, leaveOpen: true))
		{
			gzip.Write(contents);
		}
		return output.ToArray();
	}

	public void Dispose()
	{
		Directory.Delete(_directory, recursive: true);
		GC.SuppressFinalize(this);
	}

	/// <summary>Forward-only stream that yields at most one byte per read, like a slow network body.</summary>
	private sealed class TricklingStream(byte[] contents) : Stream
	{
		private int _position;

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

		public override int Read(Span<byte> buffer)
		{
			if (buffer.IsEmpty || _position >= contents.Length)
			{
				return 0;
			}

			buffer[0] = contents[_position++];
			return 1;
		}

		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(Read(buffer.Span));

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
			=> Task.FromResult(Read(buffer.AsSpan(offset, count)));

		public override void Flush() { }
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	/// <summary>Forward-only stream that records how much of the source was consumed.</summary>
	private sealed class CountingStream(byte[] contents) : Stream
	{
		private readonly MemoryStream _inner = new(contents);

		public long BytesRead { get; private set; }
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

		public override int Read(Span<byte> buffer)
		{
			int n = _inner.Read(buffer);
			BytesRead += n;
			return n;
		}

		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(Read(buffer.Span));

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
			=> Task.FromResult(Read(buffer.AsSpan(offset, count)));

		public override void Flush() { }
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		protected override void Dispose(bool disposing)
		{
			_inner.Dispose();
			base.Dispose(disposing);
		}
	}
}
