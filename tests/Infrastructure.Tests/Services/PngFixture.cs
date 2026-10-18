using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FluentAssertions;

namespace Infrastructure.Tests.Services;

// Codec-independent fixtures and PNG inspection: production's native codec is never the test oracle.
internal static class PngFixture
{
	// Fixed 8x6 white RGB JPEG, generated once with Windows GDI+, independent of SkiaSharp.
	public static byte[] WhiteJpeg => Convert.FromBase64String("/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAAGAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD9U6KKKAP/2Q==");

	public static byte[] HeaderOnly(int width, int height) => Encode(width, height, null);

	public static byte[] Encode(int width, int height, byte[]? pixels, bool rgb = false, bool rgba = false)
	{
		using MemoryStream result = new();
		result.Write([137, 80, 78, 71, 13, 10, 26, 10]);
		byte[] header = new byte[13];
		BinaryPrimitives.WriteInt32BigEndian(header, width);
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
		header[8] = 8;
		header[9] = rgba ? (byte)6 : rgb ? (byte)2 : (byte)0;
		WriteChunk(result, "IHDR", header);
		if (pixels is not null)
		{
			int rowLength = width * (rgba ? 4 : rgb ? 3 : 1);
			pixels.Length.Should().Be(rowLength * height);
			using MemoryStream compressed = new();
			using (ZLibStream zip = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
			{
				for (int y = 0; y < height; y++)
				{
					zip.WriteByte(0);
					zip.Write(pixels.AsSpan(y * rowLength, rowLength));
				}
			}
			WriteChunk(result, "IDAT", compressed.ToArray());
		}
		WriteChunk(result, "IEND", []);
		return result.ToArray();
	}

	public static byte[] TwoFrameAnimation()
	{
		byte[] first = Encode(3, 1, [255, 255, 255]);
		byte[] second = Encode(3, 1, [0, 0, 0]);
		using MemoryStream output = new();
		output.Write(first.AsSpan(0, 8));
		WriteChunk(output, "IHDR", first.AsSpan(16, 13).ToArray());
		byte[] animation = new byte[8];
		BinaryPrimitives.WriteInt32BigEndian(animation, 2);
		WriteChunk(output, "acTL", animation);
		byte[] frame = new byte[26];
		BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4), 3);
		BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(8), 1);
		frame[21] = frame[23] = 1;
		WriteChunk(output, "fcTL", frame);
		// Encode emits IHDR then a single IDAT, whose data starts at byte 41.
		int firstLength = BinaryPrimitives.ReadInt32BigEndian(first.AsSpan(33));
		WriteChunk(output, "IDAT", first.AsSpan(41, firstLength).ToArray());
		BinaryPrimitives.WriteInt32BigEndian(frame, 1);
		WriteChunk(output, "fcTL", frame);
		int secondLength = BinaryPrimitives.ReadInt32BigEndian(second.AsSpan(33));
		byte[] frameData = new byte[4 + secondLength];
		BinaryPrimitives.WriteInt32BigEndian(frameData, 2);
		second.AsSpan(41, secondLength).CopyTo(frameData.AsSpan(4));
		WriteChunk(output, "fdAT", frameData);
		WriteChunk(output, "IEND", []);
		return output.ToArray();
	}

	public static byte[] DecodeGray(byte[] png)
	{
		png.Take(8).Should().Equal(137, 80, 78, 71, 13, 10, 26, 10);
		Encoding.ASCII.GetString(png, 12, 4).Should().Be("IHDR");
		png[24].Should().Be(8, "the PNG stores eight-bit samples");
		png[25].Should().Be(0, "the PNG itself must be grayscale, not merely convertible to gray by a decoder");
		png[28].Should().Be(0, "this test decoder handles noninterlaced PNG");
		int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
		int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
		using MemoryStream compressed = new();
		for (int offset = 8; offset < png.Length;)
		{
			int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
			string type = Encoding.ASCII.GetString(png, offset + 4, 4);
			if (type == "IDAT")
			{
				compressed.Write(png, offset + 8, length);
			}

			offset += length + 12;
		}
		compressed.Position = 0;
		using ZLibStream zip = new(compressed, CompressionMode.Decompress);
		byte[] filtered = new byte[height * (width + 1)];
		zip.ReadExactly(filtered);
		zip.ReadByte().Should().Be(-1);
		byte[] pixels = new byte[width * height];
		for (int y = 0; y < height; y++)
		{
			int filter = filtered[y * (width + 1)];
			for (int x = 0; x < width; x++)
			{
				int left = x == 0 ? 0 : pixels[y * width + x - 1];
				int above = y == 0 ? 0 : pixels[(y - 1) * width + x];
				int upperLeft = x == 0 || y == 0 ? 0 : pixels[(y - 1) * width + x - 1];
				int predictor = filter switch
				{
					0 => 0,
					1 => left,
					2 => above,
					3 => (left + above) / 2,
					4 => Paeth(left, above, upperLeft),
					_ => throw new InvalidDataException($"Unknown PNG filter {filter}"),
				};
				pixels[y * width + x] = unchecked((byte)(filtered[y * (width + 1) + x + 1] + predictor));
			}
		}
		return pixels;
	}

	private static int Paeth(int left, int above, int upperLeft)
	{
		int value = left + above - upperLeft;
		int leftDistance = Math.Abs(value - left);
		int aboveDistance = Math.Abs(value - above);
		int upperLeftDistance = Math.Abs(value - upperLeft);
		return leftDistance <= aboveDistance && leftDistance <= upperLeftDistance ? left
			: aboveDistance <= upperLeftDistance ? above : upperLeft;
	}

	private static void WriteChunk(Stream stream, string type, byte[] data)
	{
		byte[] number = new byte[4];
		BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
		stream.Write(number);
		byte[] name = Encoding.ASCII.GetBytes(type);
		stream.Write(name);
		stream.Write(data);
		uint crc = 0xFFFFFFFF;
		foreach (byte value in name.Concat(data))
		{
			crc ^= value;
			for (int bit = 0; bit < 8; bit++)
			{
				crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
			}
		}
		BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xFFFFFFFF);
		stream.Write(number);
	}
}
