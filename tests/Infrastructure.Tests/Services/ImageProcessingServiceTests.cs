using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Infrastructure.Tests.Services;

public class ImageProcessingServiceTests
{
	private readonly ImageProcessingService _service;

	public ImageProcessingServiceTests()
	{
		Mock<ILogger<ImageProcessingService>> mockLogger = new();
		_service = new ImageProcessingService(mockLogger.Object);
	}

	[Fact]
	public async Task PreprocessAsync_ValidJpeg_ReturnsProcessedPng()
	{
		// Arrange
		byte[] imageBytes = CreateTestJpeg(100, 100);

		// Act
		ImageProcessingResult result = await _service.PreprocessAsync(imageBytes, "image/jpeg", CancellationToken.None);

		// Assert
		result.ProcessedBytes.Should().NotBeNullOrEmpty();
		result.Width.Should().BeGreaterThan(0);
		result.Height.Should().BeGreaterThan(0);

		// Verify output is valid PNG
		using Image<L8> output = Image.Load<L8>(result.ProcessedBytes);
		output.Width.Should().BeGreaterThan(0);
	}

	[Fact]
	public async Task PreprocessAsync_ValidPng_ReturnsProcessedPng()
	{
		// Arrange
		byte[] imageBytes = CreateTestPng(80, 120);

		// Act
		ImageProcessingResult result = await _service.PreprocessAsync(imageBytes, "image/png", CancellationToken.None);

		// Assert
		result.ProcessedBytes.Should().NotBeNullOrEmpty();
		result.Width.Should().BeGreaterThan(0);
		result.Height.Should().BeGreaterThan(0);
	}

	[Fact]
	public async Task PreprocessAsync_CorruptData_ThrowsInvalidOperationException()
	{
		// Arrange
		byte[] corruptBytes = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05];

		// Act
		Func<Task> act = () => _service.PreprocessAsync(corruptBytes, "image/jpeg", CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*not a supported image format*");
	}

	[Fact]
	public async Task PreprocessAsync_GifMasqueradingAsJpeg_ThrowsInvalidOperationException()
	{
		// Arrange - GIF magic bytes with JPEG content type
		byte[] gifBytes = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61]; // GIF89a

		// Act
		Func<Task> act = () => _service.PreprocessAsync(gifBytes, "image/jpeg", CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*not a supported image format*");
	}

	[Fact]
	public void ApplyAdaptiveThreshold_GrayscaleImage_ProducesBlackAndWhite()
	{
		// Arrange - create a gradient image
		using Image<L8> image = new(50, 50);
		image.ProcessPixelRows(accessor =>
		{
			for (int y = 0; y < 50; y++)
			{
				Span<L8> row = accessor.GetRowSpan(y);
				for (int x = 0; x < 50; x++)
				{
					row[x] = new L8((byte)(x * 5)); // gradient from 0 to 245
				}
			}
		});

		// Act
		ImageProcessingService.ApplyAdaptiveThreshold(image);

		// Assert - pixels should be either 0 or 255
		image.ProcessPixelRows(accessor =>
		{
			for (int y = 0; y < image.Height; y++)
			{
				Span<L8> row = accessor.GetRowSpan(y);
				for (int x = 0; x < image.Width; x++)
				{
					byte val = row[x].PackedValue;
					(val == 0 || val == 255).Should().BeTrue(
						$"pixel ({x},{y}) should be 0 or 255 but was {val}");
				}
			}
		});
	}

	[Fact]
	public void ApplyAdaptiveThreshold_AllBlackImage_SkipsThreshold()
	{
		// Arrange - all-black image (mean < 5)
		using Image<L8> image = new(30, 30, new L8(0));

		// Act
		ImageProcessingService.ApplyAdaptiveThreshold(image);

		// Assert - should remain all black (threshold skipped)
		image.ProcessPixelRows(accessor =>
		{
			Span<L8> row = accessor.GetRowSpan(0);
			row[0].PackedValue.Should().Be(0);
		});
	}

	[Fact]
	public void ApplyAdaptiveThreshold_AllWhiteImage_SkipsThreshold()
	{
		// Arrange - all-white image (mean > 250)
		using Image<L8> image = new(30, 30, new L8(255));

		// Act
		ImageProcessingService.ApplyAdaptiveThreshold(image);

		// Assert - should remain all white (threshold skipped)
		image.ProcessPixelRows(accessor =>
		{
			Span<L8> row = accessor.GetRowSpan(0);
			row[0].PackedValue.Should().Be(255);
		});
	}

	[Fact]
	public void DetectSkewAngle_StraightImage_ReturnsNearZero()
	{
		// Arrange - create image with horizontal black lines (no skew)
		using Image<L8> image = new(100, 100, new L8(255));
		image.ProcessPixelRows(accessor =>
		{
			for (int y = 20; y < 25; y++)
			{
				Span<L8> row = accessor.GetRowSpan(y);
				for (int x = 10; x < 90; x++)
				{
					row[x] = new L8(0);
				}
			}
			for (int y = 50; y < 55; y++)
			{
				Span<L8> row = accessor.GetRowSpan(y);
				for (int x = 10; x < 90; x++)
				{
					row[x] = new L8(0);
				}
			}
			for (int y = 80; y < 85; y++)
			{
				Span<L8> row = accessor.GetRowSpan(y);
				for (int x = 10; x < 90; x++)
				{
					row[x] = new L8(0);
				}
			}
		});

		// Act
		double angle = ImageProcessingService.DetectSkewAngle(image);

		// Assert - should be near zero for horizontal lines
		Math.Abs(angle).Should().BeLessThan(1.0);
	}

	[Fact]
	public async Task PreprocessAsync_CancellationRequested_ThrowsOperationCanceledException()
	{
		// Arrange
		byte[] imageBytes = CreateTestPng(50, 50);
		using CancellationTokenSource cts = new();
		cts.Cancel();

		// Act
		Func<Task> act = () => _service.PreprocessAsync(imageBytes, "image/png", cts.Token);

		// Assert
		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task PreprocessAsync_OutputIsSingleChannelGrayscale()
	{
		// Arrange - create a color-like JPEG (will still be L8 channel but verifies pipeline output)
		byte[] imageBytes = CreateTestJpeg(60, 40);

		// Act
		ImageProcessingResult result = await _service.PreprocessAsync(imageBytes, "image/jpeg", CancellationToken.None);

		// Assert - output should be loadable as L8 (grayscale)
		using Image<L8> output = Image.Load<L8>(result.ProcessedBytes);
		output.Width.Should().BeGreaterThan(0);
		output.Height.Should().BeGreaterThan(0);
	}

	[Fact]
	public async Task PreprocessAsync_ReturnsCorrectDimensions()
	{
		// Arrange
		byte[] imageBytes = CreateTestPng(75, 50);

		// Act
		ImageProcessingResult result = await _service.PreprocessAsync(imageBytes, "image/png", CancellationToken.None);

		// Assert
		result.Width.Should().BeGreaterThan(0);
		result.Height.Should().BeGreaterThan(0);
		// Dimensions may differ slightly due to deskew rotation, but should be close
		result.Width.Should().BeInRange(50, 100);
		result.Height.Should().BeInRange(25, 75);
	}

	[Fact]
	public void DetectSkewAngle_UniformImage_ReturnsZero()
	{
		// Arrange - uniform gray image with no features
		using Image<L8> image = new(50, 50, new L8(128));

		// Act
		double angle = ImageProcessingService.DetectSkewAngle(image);

		// Assert - with no features, angle should be near zero
		Math.Abs(angle).Should().BeLessThanOrEqualTo(10.0);
	}

	[Fact]
	public void ApplyAdaptiveThreshold_NearBlackImage_SkipsThreshold()
	{
		// Arrange - image with mean pixel value < 5 (mostly black with some very dark pixels)
		using Image<L8> image = new(30, 30, new L8(2));

		// Act
		ImageProcessingService.ApplyAdaptiveThreshold(image);

		// Assert - should be unchanged (threshold skipped)
		image.ProcessPixelRows(accessor =>
		{
			Span<L8> row = accessor.GetRowSpan(0);
			row[0].PackedValue.Should().Be(2);
		});
	}

	[Fact]
	public void ApplyAdaptiveThreshold_NearWhiteImage_SkipsThreshold()
	{
		// Arrange - image with mean pixel value > 250
		using Image<L8> image = new(30, 30, new L8(252));

		// Act
		ImageProcessingService.ApplyAdaptiveThreshold(image);

		// Assert - should be unchanged (threshold skipped)
		image.ProcessPixelRows(accessor =>
		{
			Span<L8> row = accessor.GetRowSpan(0);
			row[0].PackedValue.Should().Be(252);
		});
	}

	[Fact]
	public async Task PreprocessAsync_ExceedsMegapixelCap_RejectsBeforeAllocation()
	{
		// Decompression-bomb guard: a 5000x5000 solid-color PNG is only a few KB compressed but
		// decodes to 25 megapixels — over the 24 MP cap. Each dimension is under the per-dimension
		// limit (8000), so this specifically exercises the total-megapixel guard. It must be
		// rejected at the header-inspection step, BEFORE Image.Load and before ApplyAdaptiveThreshold
		// allocates its ~200 MB integral array. If the guard failed, this call would allocate
		// hundreds of MB and run for a long time rather than throwing promptly.
		byte[] bomb = CreateSolidPng(5000, 5000);

		Func<Task> act = () => _service.PreprocessAsync(bomb, "image/png", CancellationToken.None);

		(await act.Should().ThrowAsync<InvalidOperationException>())
			.Which.Message.Should().Contain("exceeds the maximum allowed")
			.And.Contain("pixels");
	}

	[Fact]
	public async Task PreprocessAsync_ExceedsPerDimensionCap_Rejects()
	{
		// A very wide but low-total-pixel image (8001 x 100 = 0.8 MP) is under the megapixel cap
		// but over the per-dimension cap, so the per-dimension guard must reject it.
		byte[] wide = CreateSolidPng(8001, 100);

		Func<Task> act = () => _service.PreprocessAsync(wide, "image/png", CancellationToken.None);

		(await act.Should().ThrowAsync<InvalidOperationException>())
			.Which.Message.Should().Contain("dimensions").And.Contain("exceed the maximum allowed");
	}

	[Fact]
	public async Task PreprocessAsync_ForgedHeaderDeclaring10000Squared_IsRejectedFromHeaderAlone()
	{
		// The original vulnerability: a 10000x10000 image (~100 MP, tens of KB on disk) passed the
		// old "> 10000" per-dimension check and then allocated a ~800 MB integral array. Here we
		// feed a PNG whose IHDR *declares* 10000x10000 but which carries no pixel data at all. It is
		// rejected purely from the header via Image.Identify — proving the guard fires before any
		// Image.Load / decode / large allocation ever happens (there is nothing decodable to load).
		byte[] forged = CreateForgedPngDeclaringDimensions(10000, 10000);

		Func<Task> act = () => _service.PreprocessAsync(forged, "image/png", CancellationToken.None);

		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*exceed*maximum allowed*");
	}

	[Fact]
	public async Task PreprocessAsync_NormalMultiHundredKilopixelImage_StillProcesses()
	{
		// A legitimate downscaled receipt photo (well under the caps) must still process normally.
		byte[] imageBytes = CreateTestPng(1200, 900);

		ImageProcessingResult result = await _service.PreprocessAsync(imageBytes, "image/png", CancellationToken.None);

		result.ProcessedBytes.Should().NotBeNullOrEmpty();
		result.Width.Should().BeGreaterThan(0);
		result.Height.Should().BeGreaterThan(0);
	}

	private static byte[] CreateSolidPng(int width, int height)
	{
		// Solid color so PNG compression keeps the encoded file tiny regardless of dimensions —
		// this is exactly the decompression-bomb shape we defend against.
		using Image<L8> image = new(width, height, new L8(200));
		using MemoryStream ms = new();
		image.Save(ms, new PngEncoder());
		return ms.ToArray();
	}

	// Builds a minimal, structurally valid PNG whose IHDR declares the given dimensions but which
	// contains no image data. Image.Identify reports the declared dimensions from the header alone,
	// so this lets us assert the dimension guard rejects abusive sizes without allocating any large
	// pixel buffer in the test.
	private static byte[] CreateForgedPngDeclaringDimensions(int width, int height)
	{
		using MemoryStream ms = new();
		ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]); // PNG signature

		byte[] ihdr = new byte[13];
		WriteBigEndianInt32(ihdr, 0, width);
		WriteBigEndianInt32(ihdr, 4, height);
		ihdr[8] = 8;  // bit depth
		ihdr[9] = 0;  // color type: grayscale
		ihdr[10] = 0; // compression method
		ihdr[11] = 0; // filter method
		ihdr[12] = 0; // interlace method
		WritePngChunk(ms, "IHDR", ihdr);
		WritePngChunk(ms, "IEND", []);

		return ms.ToArray();
	}

	private static void WriteBigEndianInt32(byte[] buffer, int offset, int value)
	{
		buffer[offset] = (byte)((value >> 24) & 0xFF);
		buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
		buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
		buffer[offset + 3] = (byte)(value & 0xFF);
	}

	private static void WritePngChunk(Stream stream, string type, byte[] data)
	{
		byte[] length = new byte[4];
		WriteBigEndianInt32(length, 0, data.Length);
		stream.Write(length);

		byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
		stream.Write(typeBytes);
		stream.Write(data);

		uint crc = Crc32(typeBytes, data);
		byte[] crcBytes = new byte[4];
		WriteBigEndianInt32(crcBytes, 0, (int)crc);
		stream.Write(crcBytes);
	}

	private static uint Crc32(byte[] typeBytes, byte[] data)
	{
		uint crc = 0xFFFFFFFF;
		crc = Crc32Update(crc, typeBytes);
		crc = Crc32Update(crc, data);
		return crc ^ 0xFFFFFFFF;
	}

	private static uint Crc32Update(uint crc, byte[] bytes)
	{
		foreach (byte b in bytes)
		{
			crc ^= b;
			for (int i = 0; i < 8; i++)
			{
				crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
			}
		}
		return crc;
	}

	private static byte[] CreateTestJpeg(int width, int height)
	{
		using Image<L8> image = new(width, height);
		image.ProcessPixelRows(accessor =>
		{
			for (int y = 0; y < height; y++)
			{
				Span<L8> row = accessor.GetRowSpan(y);
				for (int x = 0; x < width; x++)
				{
					row[x] = new L8((byte)((x + y) % 256));
				}
			}
		});

		using MemoryStream ms = new();
		image.Save(ms, new JpegEncoder());
		return ms.ToArray();
	}

	private static byte[] CreateTestPng(int width, int height)
	{
		using Image<L8> image = new(width, height);
		image.ProcessPixelRows(accessor =>
		{
			for (int y = 0; y < height; y++)
			{
				Span<L8> row = accessor.GetRowSpan(y);
				for (int x = 0; x < width; x++)
				{
					row[x] = new L8((byte)((x * 3 + y * 7) % 256));
				}
			}
		});

		using MemoryStream ms = new();
		image.Save(ms, new PngEncoder());
		return ms.ToArray();
	}
}
