using Application.Interfaces.Services;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Infrastructure.Services;

public class ImageProcessingService(ILogger<ImageProcessingService> logger) : IImageProcessingService
{
	private const int ThresholdKernelSize = 15;
	private const double DeskewMaxAngle = 10.0;
	private const double DeskewStepDegrees = 0.5;
	private const double DeskewMinAngle = 0.5;
	// Per-dimension caps (defense in depth). Lowered from 10_000 so a square image at the
	// per-dimension limit no longer slips past into the megapixel guard on its own.
	private const int MaxPixelWidth = 8_000;
	private const int MaxPixelHeight = 8_000;

	// PRIMARY guard against decompression bombs: a solid-color 10000x10000 PNG is only tens of
	// KB compressed but decodes to 100 megapixels, and ApplyAdaptiveThreshold then allocates a
	// long[height+1, width+1] integral array (~800 MB for 10000x10000) and spins billions of
	// iterations. Cap the TOTAL decoded pixel count (width * height) and reject before any decode
	// or allocation. 24 MP comfortably covers legitimate receipt photos (a 12 MP phone camera is
	// ~4032x3024 = 12.2 MP) while rejecting abusive dimensions.
	private const long MaxImagePixels = 24_000_000;

	// 256 MB upper bound for ImageSharp memory allocations
	private const int MemoryBudgetMegabytes = 256;

	private static readonly HashSet<Type> AllowedFormatTypes =
	[
		typeof(JpegFormat),
		typeof(PngFormat),
	];

	private static readonly Configuration BoundedConfiguration = CreateBoundedConfiguration();

	private static Configuration CreateBoundedConfiguration()
	{
		Configuration config = Configuration.Default.Clone();
		config.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
		{
			MaximumPoolSizeMegabytes = MemoryBudgetMegabytes,
		});
		return config;
	}

	public Task<ImageProcessingResult> PreprocessAsync(byte[] imageBytes, string contentType, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();

		// Validate actual image format via magic bytes, not Content-Type header
		IImageFormat? detectedFormat;
		try
		{
			detectedFormat = Image.DetectFormat(imageBytes);
		}
		catch (UnknownImageFormatException)
		{
			detectedFormat = null;
		}

		if (detectedFormat is null || !AllowedFormatTypes.Contains(detectedFormat.GetType()))
		{
			throw new InvalidOperationException(
				"The uploaded file is not a supported image format. Only JPEG and PNG are accepted.");
		}

		// Check image dimensions BEFORE full decode to reject oversized images cheaply.
		// Image.Identify only reads the header (no full-frame allocation), so these guards run
		// before Image.Load and before ApplyAdaptiveThreshold's integral-array allocation.
		DecoderOptions identifyOptions = new() { Configuration = BoundedConfiguration };
		ImageInfo info = Image.Identify(identifyOptions, imageBytes);

		if (info.Width > MaxPixelWidth || info.Height > MaxPixelHeight)
		{
			throw new InvalidOperationException(
				$"Image dimensions ({info.Width}x{info.Height}) exceed the maximum allowed ({MaxPixelWidth}x{MaxPixelHeight}).");
		}

		// Cap total decoded pixel count (width * height) to prevent decompression-bomb DoS,
		// where a tiny compressed file decodes to a huge pixel buffer. Use long arithmetic so
		// the multiplication cannot overflow for large declared dimensions.
		long totalPixels = (long)info.Width * info.Height;
		if (totalPixels > MaxImagePixels)
		{
			throw new InvalidOperationException(
				$"Image resolution ({info.Width}x{info.Height} = {totalPixels:N0} pixels) exceeds the maximum allowed ({MaxImagePixels:N0} pixels).");
		}

		DecoderOptions decoderOptions = new()
		{
			Configuration = BoundedConfiguration,
			MaxFrames = 1,
		};

		Image<L8> image;
		try
		{
			image = Image.Load<L8>(decoderOptions, imageBytes);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to load image for preprocessing");
			throw new InvalidOperationException("The uploaded file is not a valid image or is corrupted.", ex);
		}

		using (image)
		{
			// Step 1: Already grayscale (loaded as L8)

			// Step 2: Adaptive threshold (15x15 local mean)
			ApplyAdaptiveThreshold(image);

			ct.ThrowIfCancellationRequested();

			// Step 3: Deskew via projection profile
			double skewAngle = DetectSkewAngle(image);
			if (Math.Abs(skewAngle) > DeskewMinAngle)
			{
				image.Mutate(ctx => ctx.Rotate((float)-skewAngle));
				logger.LogInformation("Deskewed image by {Angle:F1} degrees", -skewAngle);
			}

			ct.ThrowIfCancellationRequested();

			// Step 4: Encode as PNG
			using MemoryStream ms = new();
			image.Save(ms, new PngEncoder());

			return Task.FromResult(new ImageProcessingResult(
				ms.ToArray(), image.Width, image.Height));
		}
	}

	internal static void ApplyAdaptiveThreshold(Image<L8> image)
	{
		int width = image.Width;
		int height = image.Height;
		int halfKernel = ThresholdKernelSize / 2;

		// Build integral image for fast local mean computation
		long[,] integral = new long[height + 1, width + 1];

		image.ProcessPixelRows(accessor =>
		{
			for (int y = 0; y < height; y++)
			{
				Span<L8> row = accessor.GetRowSpan(y);
				long rowSum = 0;
				for (int x = 0; x < width; x++)
				{
					rowSum += row[x].PackedValue;
					integral[y + 1, x + 1] = integral[y, x + 1] + rowSum;
				}
			}
		});

		// Compute mean pixel value to detect all-black/white images
		long totalSum = integral[height, width];
		double meanPixel = (double)totalSum / (width * height);

		// Skip thresholding if image is nearly all-black or all-white
		if (meanPixel < 5 || meanPixel > 250)
		{
			return;
		}

		// Apply threshold using integral image
		image.ProcessPixelRows(accessor =>
		{
			for (int y = 0; y < height; y++)
			{
				Span<L8> row = accessor.GetRowSpan(y);
				for (int x = 0; x < width; x++)
				{
					int y1 = Math.Max(0, y - halfKernel);
					int y2 = Math.Min(height - 1, y + halfKernel);
					int x1 = Math.Max(0, x - halfKernel);
					int x2 = Math.Min(width - 1, x + halfKernel);

					int area = (y2 - y1 + 1) * (x2 - x1 + 1);
					long sum = integral[y2 + 1, x2 + 1]
						- integral[y1, x2 + 1]
						- integral[y2 + 1, x1]
						+ integral[y1, x1];

					double localMean = (double)sum / area;
					row[x] = new L8(row[x].PackedValue >= localMean ? (byte)255 : (byte)0);
				}
			}
		});
	}

	internal static double DetectSkewAngle(Image<L8> image)
	{
		int width = image.Width;
		int height = image.Height;

		// Extract pixel data for projection computation
		byte[] pixels = new byte[width * height];
		image.ProcessPixelRows(accessor =>
		{
			for (int y = 0; y < height; y++)
			{
				Span<L8> row = accessor.GetRowSpan(y);
				for (int x = 0; x < width; x++)
				{
					pixels[y * width + x] = row[x].PackedValue;
				}
			}
		});

		double bestAngle = 0;
		double bestVariance = -1;

		int steps = (int)(2 * DeskewMaxAngle / DeskewStepDegrees) + 1;
		for (int i = 0; i < steps; i++)
		{
			double angle = -DeskewMaxAngle + i * DeskewStepDegrees;
			double radians = angle * Math.PI / 180.0;
			double sinA = Math.Sin(radians);
			double cosA = Math.Cos(radians);

			// Compute horizontal projection profile
			int[] projection = new int[height];
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					if (pixels[y * width + x] == 0)
					{
						// Rotate point and accumulate at projected row
						int projY = (int)Math.Round(-x * sinA + y * cosA);
						if (projY >= 0 && projY < height)
						{
							projection[projY]++;
						}
					}
				}
			}

			// Compute variance of projection
			double mean = 0;
			for (int y = 0; y < height; y++)
			{
				mean += projection[y];
			}
			mean /= height;

			double variance = 0;
			for (int y = 0; y < height; y++)
			{
				double diff = projection[y] - mean;
				variance += diff * diff;
			}
			variance /= height;

			if (variance > bestVariance)
			{
				bestVariance = variance;
				bestAngle = angle;
			}
		}

		return bestAngle;
	}
}
