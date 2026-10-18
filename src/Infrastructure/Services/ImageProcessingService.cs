using System.Buffers.Binary;
using Application.Interfaces.Services;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Infrastructure.Services;

public class ImageProcessingService(ILogger<ImageProcessingService> logger) : IImageProcessingService
{
	private const int ThresholdKernelSize = 15;
	private const double DeskewMaxAngle = 10.0;
	private const double DeskewStepDegrees = 0.5;
	private const double DeskewMinAngle = 0.5;
	private const int MaxPixelDimension = 8_000;
	private const long MaxImagePixels = 24_000_000;

	public Task<ImageProcessingResult> PreprocessAsync(byte[] imageBytes, string contentType, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		ReadOnlySpan<byte> bytes = imageBytes;
		bool png = bytes.StartsWith<byte>([137, 80, 78, 71, 13, 10, 26, 10]);
		bool jpeg = bytes.StartsWith<byte>([255, 216, 255]);
		if (!png && !jpeg)
		{
			throw new InvalidOperationException("The uploaded file is not a supported image format. Only JPEG and PNG are accepted.");
		}

		// Inspect a PNG's declared dimensions even if the rest of its stream is truncated.
		// The codec validates the complete header and data afterwards; this is only an allocation guard.
		if (png && bytes.Length >= 24 && bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
		{
			ValidateDimensions(BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4)),
				BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4)));
		}

		using SKData data = SKData.CreateCopy(imageBytes);
		using SKCodec? codec = SKCodec.Create(data);
		if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png))
		{
			throw InvalidImage();
		}
		ValidateDimensions((uint)codec.Info.Width, (uint)codec.Info.Height);
		ct.ThrowIfCancellationRequested();
		byte[] pixels;
		int width = codec.Info.Width;
		int height = codec.Info.Height;
		// Explicit frame zero and unpremultiplied color preserve the existing first-frame/alpha behavior.
		using (SKBitmap decoded = new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul)))
		{
			SKCodecResult result = codec.GetPixels(decoded.Info, decoded.GetPixels(), decoded.RowBytes, new SKCodecOptions(0));
			if (result != SKCodecResult.Success)
			{
				throw InvalidImage();
			}
			pixels = ToGrayscale(decoded, ct);
		}

		ApplyAdaptiveThreshold(pixels, width, height, ct);
		double skewAngle = DetectSkewAngle(pixels, width, height, ct);
		using SKBitmap grayscale = CreateGrayscale(pixels, width, height);
		if (Math.Abs(skewAngle) > DeskewMinAngle)
		{
			using SKBitmap rotated = Rotate(grayscale, -skewAngle);
			pixels = ToGrayscale(rotated, ct);
			using SKBitmap output = CreateGrayscale(pixels, rotated.Width, rotated.Height);
			logger.LogInformation("Deskewed image by {Angle:F1} degrees", -skewAngle);
			return Task.FromResult(Encode(output, ct));
		}
		return Task.FromResult(Encode(grayscale, ct));
	}

	private static InvalidOperationException InvalidImage() => new("The uploaded file is not a valid image or is corrupted.");

	private static void ValidateDimensions(uint width, uint height)
	{
		if (width == 0 || height == 0)
		{
			throw InvalidImage();
		}
		if (width > MaxPixelDimension || height > MaxPixelDimension)
		{
			throw new InvalidOperationException($"Image dimensions ({width}x{height}) exceed the maximum allowed ({MaxPixelDimension}x{MaxPixelDimension}).");
		}
		long totalPixels = (long)width * height;
		if (totalPixels > MaxImagePixels)
		{
			throw new InvalidOperationException($"Image resolution ({width}x{height} = {totalPixels:N0} pixels) exceeds the maximum allowed ({MaxImagePixels:N0} pixels).");
		}
	}

	private static byte[] ToGrayscale(SKBitmap image, CancellationToken ct)
	{
		byte[] pixels = new byte[image.Width * image.Height];
		Span<byte> source = image.GetPixelSpan();
		for (int y = 0; y < image.Height; y++)
		{
			ct.ThrowIfCancellationRequested();
			for (int x = 0; x < image.Width; x++)
			{
				int offset = y * image.RowBytes + x * 4;
				// Rec.709 luminance, matching the previous L8 conversion; alpha is not composited.
				pixels[y * image.Width + x] = (byte)Math.Clamp((int)MathF.Round(
					0.2126f * source[offset] + 0.7152f * source[offset + 1] + 0.0722f * source[offset + 2]), 0, 255);
			}
		}
		return pixels;
	}

	private static SKBitmap CreateGrayscale(byte[] pixels, int width, int height)
	{
		SKBitmap image = new(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
		pixels.CopyTo(image.GetPixelSpan());
		return image;
	}

	private static SKBitmap Rotate(SKBitmap source, double angle)
	{
		double radians = angle * Math.PI / 180;
		int width = (int)Math.Ceiling(Math.Abs(source.Width * Math.Cos(radians)) + Math.Abs(source.Height * Math.Sin(radians)));
		int height = (int)Math.Ceiling(Math.Abs(source.Height * Math.Cos(radians)) + Math.Abs(source.Width * Math.Sin(radians)));
		SKBitmap rotated = new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
		try
		{
			using SKCanvas canvas = new(rotated);
			canvas.Clear(SKColors.Black);
			canvas.Translate(width / 2f, height / 2f);
			canvas.RotateDegrees((float)angle);
			using SKImage image = SKImage.FromBitmap(source);
			canvas.DrawImage(image, -source.Width / 2f, -source.Height / 2f, new SKSamplingOptions(SKCubicResampler.CatmullRom));
			return rotated;
		}
		catch
		{
			rotated.Dispose();
			throw;
		}
	}

	private static ImageProcessingResult Encode(SKBitmap bitmap, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		using SKImage image = SKImage.FromBitmap(bitmap);
		using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100)
			?? throw new InvalidOperationException("The processed image could not be encoded.");
		ct.ThrowIfCancellationRequested();
		return new(encoded.ToArray(), bitmap.Width, bitmap.Height);
	}

	internal static void ApplyAdaptiveThreshold(byte[] pixels, int width, int height, CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		int halfKernel = ThresholdKernelSize / 2;
		long[,] integral = new long[height + 1, width + 1];
		for (int y = 0; y < height; y++)
		{
			ct.ThrowIfCancellationRequested();
			long rowSum = 0;
			for (int x = 0; x < width; x++)
			{
				rowSum += pixels[y * width + x];
				integral[y + 1, x + 1] = integral[y, x + 1] + rowSum;
			}
		}
		double meanPixel = (double)integral[height, width] / (width * height);
		if (meanPixel < 5 || meanPixel > 250)
		{
			return;
		}
		for (int y = 0; y < height; y++)
		{
			ct.ThrowIfCancellationRequested();
			for (int x = 0; x < width; x++)
			{
				int y1 = Math.Max(0, y - halfKernel);
				int y2 = Math.Min(height - 1, y + halfKernel);
				int x1 = Math.Max(0, x - halfKernel);
				int x2 = Math.Min(width - 1, x + halfKernel);
				int area = (y2 - y1 + 1) * (x2 - x1 + 1);
				long sum = integral[y2 + 1, x2 + 1] - integral[y1, x2 + 1] - integral[y2 + 1, x1] + integral[y1, x1];
				pixels[y * width + x] = pixels[y * width + x] >= (double)sum / area ? (byte)255 : (byte)0;
			}
		}
	}

	internal static double DetectSkewAngle(byte[] pixels, int width, int height, CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		// A uniform image has no orientation. Cropping an all-black rectangle otherwise creates
		// artificial projection variance, while no black pixels used to select the first (-10 degree) candidate.
		bool uniform = true;
		for (int y = 0; y < height; y++)
		{
			ct.ThrowIfCancellationRequested();
			for (int x = 0; x < width; x++)
			{
				uniform &= pixels[y * width + x] == pixels[0];
			}
		}
		if (uniform)
		{
			return 0;
		}
		double bestAngle = 0;
		double bestVariance = 0;
		int[] projection = new int[height];
		int steps = (int)(2 * DeskewMaxAngle / DeskewStepDegrees) + 1;
		for (int i = 0; i < steps; i++)
		{
			Array.Clear(projection);
			double angle = -DeskewMaxAngle + i * DeskewStepDegrees;
			double radians = angle * Math.PI / 180.0;
			double sinA = Math.Sin(radians);
			double cosA = Math.Cos(radians);
			for (int y = 0; y < height; y++)
			{
				ct.ThrowIfCancellationRequested();
				for (int x = 0; x < width; x++)
				{
					if (pixels[y * width + x] == 0)
					{
						int projY = (int)Math.Round(-x * sinA + y * cosA);
						if (projY >= 0 && projY < height)
						{
							projection[projY]++;
						}
					}
				}
			}
			double mean = projection.Sum() / (double)height;
			double variance = 0;
			foreach (int count in projection)
			{
				double diff = count - mean;
				variance += diff * diff;
			}
			variance /= height;
			if (variance > bestVariance || (variance == bestVariance && Math.Abs(angle) < Math.Abs(bestAngle)))
			{
				bestVariance = variance;
				bestAngle = angle;
			}
		}
		return bestAngle;
	}
}
