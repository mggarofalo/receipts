using System.Buffers.Binary;
using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.Services;

public class ImageProcessingServiceTests
{
	private readonly ImageProcessingService _service = new(NullLogger<ImageProcessingService>.Instance);

	[Fact]
	public async Task PreprocessAsync_JpegFixture_ReturnsSingleChannelPngWithExactDimensions()
	{
		ImageProcessingResult result = await _service.PreprocessAsync(PngFixture.WhiteJpeg, "image/jpeg", CancellationToken.None);
		result.Width.Should().Be(8);
		result.Height.Should().Be(6);
		PngFixture.DecodeGray(result.ProcessedBytes).Should().Equal(Enumerable.Repeat((byte)255, 48));
	}

	[Fact]
	public async Task PreprocessAsync_ColorPng_UsesLuminanceAndWritesActualGrayscale()
	{
		// Red, green, blue have Rec.709 luminances below, above, below their mean.
		byte[] input = PngFixture.Encode(3, 1, [255, 0, 0, 0, 255, 0, 0, 0, 255], rgb: true);
		ImageProcessingResult result = await _service.PreprocessAsync(input, "image/png", CancellationToken.None);
		result.Width.Should().Be(3);
		result.Height.Should().Be(1);
		PngFixture.DecodeGray(result.ProcessedBytes).Should().Equal(0, 255, 0);
	}

	[Fact]
	public async Task PreprocessAsync_TransparentColor_PreservesRgbLuminanceWithoutAlphaCompositing()
	{
		byte[] rgba = [255, 0, 0, 0, 0, 255, 0, 0, 0, 0, 255, 128];
		ImageProcessingResult result = await _service.PreprocessAsync(PngFixture.Encode(3, 1, rgba, rgba: true), "image/png", CancellationToken.None);
		PngFixture.DecodeGray(result.ProcessedBytes).Should().Equal(0, 255, 0);
	}

	[Fact]
	public async Task PreprocessAsync_AnimatedPng_ProcessesOnlyFirstFrame()
	{
		ImageProcessingResult result = await _service.PreprocessAsync(PngFixture.TwoFrameAnimation(), "image/png", CancellationToken.None);
		result.Width.Should().Be(3);
		result.Height.Should().Be(1);
		PngFixture.DecodeGray(result.ProcessedBytes).Should().Equal(255, 255, 255);
	}

	[Theory]
	[InlineData(1)]
	[InlineData(-1)]
	public async Task PreprocessAsync_SlantedLines_ExpandsCanvasAndMakesLinesHorizontal(int direction)
	{
		byte[] pixels = Enumerable.Repeat((byte)255, 120 * 100).ToArray();
		foreach (int baseline in new[] { 20, 50, 80 })
		{
			for (int x = 10; x < 110; x++)
			{
				pixels[(baseline + direction * (x / 10)) * 120 + x] = 0;
			}
		}
		ImageProcessingResult result = await _service.PreprocessAsync(PngFixture.Encode(120, 100, pixels), "image/png", CancellationToken.None);
		result.Width.Should().BeGreaterThan(120);
		result.Height.Should().BeGreaterThan(100);
		byte[] output = PngFixture.DecodeGray(result.ProcessedBytes);
		// Compare actual darkest line locations at two interior columns, ignoring rotation padding.
		int leftRow = Enumerable.Range(15, 30).MinBy(y => output[y * result.Width + 35]);
		int rightRow = Enumerable.Range(15, 30).MinBy(y => output[y * result.Width + 85]);
		output[leftRow * result.Width + 35].Should().BeLessThan(100);
		output[rightRow * result.Width + 85].Should().BeLessThan(100);
		Math.Abs(leftRow - rightRow).Should().BeLessThanOrEqualTo(1,
			"deskewing must flatten the text, not double its slope");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(2)]
	[InlineData(128)]
	[InlineData(255)]
	public void DetectSkewAngle_UniformImage_ReturnsZero(byte intensity)
	{
		byte[] pixels = Enumerable.Repeat(intensity, 50 * 50).ToArray();
		ImageProcessingService.DetectSkewAngle(pixels, 50, 50).Should().Be(0,
			"a featureless image has no evidence of skew");
	}

	[Fact]
	public void DetectSkewAngle_HorizontalTextLines_ReturnsZero()
	{
		byte[] pixels = Enumerable.Repeat((byte)255, 100 * 100).ToArray();
		foreach (int baseline in new[] { 20, 50, 80 })
		{
			for (int x = 10; x < 90; x++)
			{
				pixels[baseline * 100 + x] = 0;
			}
		}
		ImageProcessingService.DetectSkewAngle(pixels, 100, 100).Should().Be(0);
	}

	[Fact]
	public void DetectSkewAngle_SlantedTextLines_FindsTheirDirection()
	{
		byte[] pixels = Enumerable.Repeat((byte)255, 120 * 100).ToArray();
		foreach (int baseline in new[] { 15, 40, 65 })
		{
			for (int x = 10; x < 110; x++)
			{
				pixels[(baseline + x / 10) * 120 + x] = 0;
			}
		}
		ImageProcessingService.DetectSkewAngle(pixels, 120, 100).Should().BeInRange(5, 6.5);
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(2, 2)]
	[InlineData(4, 4)]
	[InlineData(5, 255)]
	[InlineData(250, 255)]
	[InlineData(251, 251)]
	[InlineData(252, 252)]
	[InlineData(255, 255)]
	public void ApplyAdaptiveThreshold_NearBlackWhiteBoundaries_PreserveOnlySkippedValues(byte input, byte expected)
	{
		byte[] pixels = Enumerable.Repeat(input, 30 * 30).ToArray();
		ImageProcessingService.ApplyAdaptiveThreshold(pixels, 30, 30);
		pixels.Should().Equal(Enumerable.Repeat(expected, 30 * 30));
	}

	[Fact]
	public void ApplyAdaptiveThreshold_AtLocalMean_IsWhiteAndEdgesUseOnlyAvailablePixels()
	{
		byte[] pixels = [10, 20, 30];
		ImageProcessingService.ApplyAdaptiveThreshold(pixels, 3, 1);
		pixels.Should().Equal(0, 255, 255);
	}

	[Theory]
	[InlineData("threshold")]
	[InlineData("skew")]
	public void PixelOperations_Cancelled_DoNotProcess(string operation)
	{
		byte[] pixels = [10, 20, 30];
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();
		Action act = operation == "threshold"
			? () => ImageProcessingService.ApplyAdaptiveThreshold(pixels, 3, 1, cancellation.Token)
			: () => ImageProcessingService.DetectSkewAngle(pixels, 3, 1, cancellation.Token);
		act.Should().Throw<OperationCanceledException>();
		pixels.Should().Equal(10, 20, 30);
	}

	[Fact]
	public async Task PreprocessAsync_Cancelled_RejectsBeforeInspectingInvalidData()
	{
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();
		Func<Task> act = () => _service.PreprocessAsync([], "image/png", cancellation.Token);
		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task PreprocessAsync_ValidGifMasqueradingAsPng_RejectsActualFormat()
	{
		byte[] gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
		Func<Task> act = () => _service.PreprocessAsync(gif, "image/png", CancellationToken.None);
		await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a supported image format*");
	}

	[Fact]
	public async Task PreprocessAsync_CorruptData_Rejects()
	{
		Func<Task> act = () => _service.PreprocessAsync([0, 1, 2, 3], "image/jpeg", CancellationToken.None);
		await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a supported image format*");
	}

	[Fact]
	public async Task PreprocessAsync_RecognizablePngWithoutPixels_RejectsDecode()
	{
		Func<Task> act = () => _service.PreprocessAsync(PngFixture.HeaderOnly(80, 120), "image/png", CancellationToken.None);
		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	[Theory]
	[InlineData(8001, 100, "dimensions")]
	[InlineData(100, 8001, "dimensions")]
	[InlineData(5000, 5000, "pixels")]
	[InlineData(10000, 10000, "dimensions")]
	public async Task PreprocessAsync_OversizedPngHeader_RejectsBeforePixelDecode(int width, int height, string reason)
	{
		// No IDAT pixels exist: the limit must be enforced from declared dimensions alone.
		Func<Task> act = () => _service.PreprocessAsync(PngFixture.HeaderOnly(width, height), "image/png", CancellationToken.None);
		(await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(reason).And.Contain("maximum allowed");
	}

	[Fact]
	public async Task PreprocessAsync_OversizedJpegHeader_RejectsBeforePixelDecode()
	{
		byte[] jpeg = PngFixture.WhiteJpeg;
		int offset = Array.FindIndex(jpeg, value => value == 0xC0);
		jpeg[offset - 1].Should().Be(0xFF);
		BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(offset + 4, 2), 10000);
		BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(offset + 6, 2), 10000);
		Func<Task> act = () => _service.PreprocessAsync(jpeg, "image/jpeg", CancellationToken.None);
		await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*dimensions*maximum allowed*");
	}

	[Theory]
	[InlineData(8000, 1)]
	[InlineData(1, 8000)]
	[InlineData(1200, 900)]
	public async Task PreprocessAsync_SupportedDimensions_PreserveUnskewedCanvas(int width, int height)
	{
		byte[] png = PngFixture.Encode(width, height, Enumerable.Repeat((byte)255, width * height).ToArray());
		ImageProcessingResult result = await _service.PreprocessAsync(png, "image/png", CancellationToken.None);
		result.Width.Should().Be(width);
		result.Height.Should().Be(height);
		PngFixture.DecodeGray(result.ProcessedBytes).Should().Equal(Enumerable.Repeat((byte)255, width * height));
	}
}
