using AssetRipper.TextureDecoder.Astc;
using AssetRipper.TextureDecoder.Rgb.Formats;

namespace AssetRipper.TextureDecoder.Tests;

public sealed class AstcTests
{
	private const double MaxMeanDeviation4x4 = 0.2;
	private const double MaxMeanDeviation4x4HDR = 0.4;
	private const double MaxStandardDeviation4x4 = 1;

	private const double MaxMeanDeviation5x5 = 0.2;
	private const double MaxMeanDeviation5x5HDR = 0.4;
	private const double MaxStandardDeviation5x5 = 1;

	private const double MaxMeanDeviation6x6 = 0.2;
	private const double MaxMeanDeviation6x6HDR = 0.4;
	private const double MaxStandardDeviation6x6 = 1;

	private const double MaxMeanDeviation8x8 = 0.2;
	private const double MaxMeanDeviation8x8HDR = 0.4;
	private const double MaxStandardDeviation8x8 = 2;

	private const double MaxMeanDeviation10x10 = 0.2;
	private const double MaxMeanDeviation10x10HDR = 0.5;
	private const double MaxStandardDeviation10x10 = 2.5;

	private const double MaxMeanDeviation12x12 = 0.3;
	private const double MaxMeanDeviation12x12HDR = 0.5;
	private const double MaxStandardDeviation12x12 = 3;

	[Test]
	public void DecompressAstc4x4() => AssertCorrectDecompression<AndroidTextures.Logo_06>(4, 4, MaxMeanDeviation4x4, MaxStandardDeviation4x4);

	[Test]
	public void DecompressAstc5x5() => AssertCorrectDecompression<AndroidTextures.Logo_05>(5, 5, MaxMeanDeviation5x5, MaxStandardDeviation5x5);

	[Test]
	public void DecompressAstc6x6() => AssertCorrectDecompression<AndroidTextures.Logo_04>(6, 6, MaxMeanDeviation6x6, MaxStandardDeviation6x6);

	[Test]
	public void DecompressAstc8x8() => AssertCorrectDecompression<AndroidTextures.Logo_03>(8, 8, MaxMeanDeviation8x8, MaxStandardDeviation8x8);

	[Test]
	public void DecompressAstc10x10() => AssertCorrectDecompression<AndroidTextures.Logo_02>(10, 10, MaxMeanDeviation10x10, MaxStandardDeviation10x10);

	[Test]
	public void DecompressAstc12x12() => AssertCorrectDecompression<AndroidTextures.Logo_01>(12, 12, MaxMeanDeviation12x12, MaxStandardDeviation12x12);

	[Test]
	public void DecompressAstc4x4HDR() => AssertCorrectDecompression<AndroidTextures.Logo_22>(4, 4, MaxMeanDeviation4x4HDR, MaxStandardDeviation4x4);

	[Test]
	public void DecompressAstc5x5HDR() => AssertCorrectDecompression<AndroidTextures.Logo_21>(5, 5, MaxMeanDeviation5x5HDR, MaxStandardDeviation5x5);

	[Test]
	public void DecompressAstc6x6HDR() => AssertCorrectDecompression<AndroidTextures.Logo_20>(6, 6, MaxMeanDeviation6x6HDR, MaxStandardDeviation6x6);

	[Test]
	public void DecompressAstc8x8HDR() => AssertCorrectDecompression<AndroidTextures.Logo_19>(8, 8, MaxMeanDeviation8x8HDR, MaxStandardDeviation8x8);

	[Test]
	public void DecompressAstc10x10HDR() => AssertCorrectDecompression<AndroidTextures.Logo_18>(10, 10, MaxMeanDeviation10x10HDR, MaxStandardDeviation10x10);

	[Test]
	public void DecompressAstc12x12HDR() => AssertCorrectDecompression<AndroidTextures.Logo_17>(12, 12, MaxMeanDeviation12x12HDR, MaxStandardDeviation12x12);

	private static void AssertCorrectDecompression<T>(int blockWidth, int blockHeight, double maxMeanDeviation, double maxStandardDeviation) where T : ITexture
	{
		ReadOnlySpan<byte> data = T.Data;
		int bytesRead = AstcDecoder.DecodeASTC<ColorBGRA<byte>, byte>(data, T.Width, T.Height, blockWidth, blockHeight, out byte[] decompressedData);
		if (!T.Mips)
		{
			Assert.That(bytesRead, Is.EqualTo(data.Length));
		}
		ByteArrayDeviation.AssertMinimalDeviation(decompressedData, AndroidTextures.Logo_00.Data, maxMeanDeviation, maxStandardDeviation);
	}

	[TestCase(new byte[16] { 83, 1, 147, 104, 198, 173, 55, 116, 182, 66, 105, 11, 102, 43, 148, 125 }, 8, 8, TestName = "GetBits Should not throw in DecodeIntseq")]
	[TestCase(new byte[16] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 8, 8, TestName = "Zero blocks should not throw", Description = "Blocks of all zeros are valid")]
	[TestCase(new byte[16] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, 8, 8, TestName = "0xFF blocks should not throw", Description = "Blocks with all bits set are invalid, but should be handled gracefully")]
	[TestCase(new byte[16] { 0x60, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 12, 12, TestName = "Invalid oversized dual-plane weight grid should not throw")]
	public void DecodesBlockWithoutThrowing(byte[] block, int blockWidth, int blockHeight)
	{
		Assert.DoesNotThrow(() => AstcDecoder.DecodeASTC<ColorBGRA<byte>, byte>(block, blockWidth, blockHeight, blockWidth, blockHeight, out _));
	}

	[Test]
	public void InvalidOversizedWeightGridProducesErrorColor()
	{
		byte[] block = [0x60, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
		int bytesRead = AstcDecoder.DecodeASTC<ColorRGBA<byte>, byte>(block, 12, 12, 12, 12, out byte[] output);
		byte[] expected = Enumerable.Range(0, 12 * 12).SelectMany(_ => new byte[] { 255, 0, 255, 255 }).ToArray();
		Assert.That(bytesRead, Is.EqualTo(16));
		Assert.That(output, Is.EqualTo(expected));
	}

	[TestCase(new byte[] { 0xFC, 0xFD, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x09, 0x09, 0x09, 0x09, 0x09, 0x09, 0xFF, 0xFF }, TestName = "ASTC UNORM16 void extent rounds to FP16")]
	[TestCase(new byte[] { 0x42, 0x00, 0x12, 0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x27, 0x27, 0x27, 0x27 }, TestName = "ASTC LDR luminance rounds to FP16")]
	public void DecodesLdrBlockToHalfPrecision(byte[] block)
	{
		byte[] texel = [0x84, 0x28, 0x84, 0x28, 0x84, 0x28, 0x00, 0x3C];
		byte[] expected = new byte[16 * texel.Length];
		for (int i = 0; i < 16; i++)
		{
			texel.CopyTo(expected, i * texel.Length);
		}
		AssertBlock(block, expected);
	}

	[Test]
	public void DecodesUnormVoidExtentBoundaries()
	{
		byte[] block = [0xFC, 0xFD, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 1, 0, 0xFF, 0xFF, 9, 9];
		byte[] texel = [0, 0, 0, 1, 0, 0x3C, 0x84, 0x28];
		byte[] expected = new byte[16 * texel.Length];
		for (int i = 0; i < 16; i++)
		{
			texel.CopyTo(expected, i * texel.Length);
		}
		AssertBlock(block, expected);

		AstcDecoder.DecodeASTC<ColorRGBA<ushort>, ushort>(block, 4, 4, 4, 4, out byte[] integerOutput);
		Assert.That(integerOutput.AsSpan(0, 8).ToArray(), Is.EqualTo(block.AsSpan(8, 8).ToArray()));
	}

	// Each 4x4 block repeats the same row. Expected RGBA16F bytes come from astcenc 5.7.1 (-dH, DDS).
	[TestCase(new byte[] { 0x42, 0x40, 0x00, 0x21, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x27, 0x27, 0x27, 0x27 }, new byte[] { 0x00, 0x40, 0x00, 0x40, 0x00, 0x40, 0x00, 0x3C, 0x60, 0x42, 0x60, 0x42, 0x60, 0x42, 0x00, 0x3C, 0x20, 0x45, 0x20, 0x45, 0x20, 0x45, 0x00, 0x3C, 0x00, 0x48, 0x00, 0x48, 0x00, 0x48, 0x00, 0x3C }, TestName = "ASTC HDR mode 2 large-range luminance")]
	[TestCase(new byte[] { 0x42, 0x60, 0x00, 0x1E, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x27, 0x27, 0x27, 0x27 }, new byte[] { 0x00, 0x40, 0x00, 0x40, 0x00, 0x40, 0x00, 0x3C, 0x3B, 0x40, 0x3B, 0x40, 0x3B, 0x40, 0x00, 0x3C, 0x79, 0x40, 0x79, 0x40, 0x79, 0x40, 0x00, 0x3C, 0xB4, 0x40, 0xB4, 0x40, 0xB4, 0x40, 0x00, 0x3C }, TestName = "ASTC HDR mode 3 small-range luminance")]
	[TestCase(new byte[] { 0x42, 0xE0, 0xFE, 0xA1, 0x61, 0x1F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x27, 0x27, 0x27, 0x27 }, new byte[] { 0x00, 0x30, 0xC0, 0x40, 0xC0, 0x20, 0x00, 0x3C, 0xB1, 0x34, 0xAC, 0x45, 0xAC, 0x25, 0x00, 0x3C, 0xD4, 0x39, 0xD9, 0x4A, 0xD9, 0x2A, 0x00, 0x3C, 0xC0, 0x3E, 0x00, 0x50, 0x00, 0x30, 0x00, 0x3C }, TestName = "ASTC HDR mode 7 RGB offset")]
	[TestCase(new byte[] { 0x42, 0x60, 0x01, 0x21, 0x21, 0x41, 0xA1, 0xB1, 0x01, 0x00, 0x00, 0x00, 0x27, 0x27, 0x27, 0x27 }, new byte[] { 0x00, 0x40, 0x00, 0x48, 0x00, 0x50, 0x00, 0x3C, 0x60, 0x42, 0x60, 0x4A, 0x60, 0x52, 0x00, 0x3C, 0x20, 0x45, 0x20, 0x4D, 0x20, 0x55, 0x00, 0x3C, 0x00, 0x48, 0x00, 0x50, 0x00, 0x58, 0x00, 0x3C }, TestName = "ASTC HDR mode 11 RGB")]
	[TestCase(new byte[] { 0x42, 0xC0, 0x01, 0x21, 0x21, 0x41, 0xA1, 0xB1, 0x01, 0xFE, 0x01, 0x00, 0x27, 0x27, 0x27, 0x27 }, new byte[] { 0x00, 0x40, 0x00, 0x48, 0x00, 0x50, 0x00, 0x00, 0x60, 0x42, 0x60, 0x4A, 0x60, 0x52, 0x40, 0x35, 0x20, 0x45, 0x20, 0x4D, 0x20, 0x55, 0x5F, 0x39, 0x00, 0x48, 0x00, 0x50, 0x00, 0x58, 0x00, 0x3C }, TestName = "ASTC HDR mode 14 RGB with LDR alpha")]
	[TestCase(new byte[] { 0x42, 0xE0, 0x01, 0x21, 0x21, 0x41, 0xA1, 0xB1, 0x81, 0x91, 0x01, 0x00, 0x27, 0x27, 0x27, 0x27 }, new byte[] { 0x00, 0x40, 0x00, 0x48, 0x00, 0x50, 0x00, 0x40, 0x60, 0x42, 0x60, 0x4A, 0x60, 0x52, 0x60, 0x42, 0x20, 0x45, 0x20, 0x4D, 0x20, 0x55, 0x20, 0x45, 0x00, 0x48, 0x00, 0x50, 0x00, 0x58, 0x00, 0x48 }, TestName = "ASTC HDR mode 15 RGBA")]
	[TestCase(new byte[] { 0x41, 0xC4, 0x01, 0x21, 0x21, 0x41, 0xA1, 0xB1, 0x01, 0xFE, 0x01, 0xC0, 0x1B, 0x1B, 0x1B, 0x1B }, new byte[] { 0x00, 0x40, 0x00, 0x48, 0x00, 0x50, 0x00, 0x00, 0x00, 0x40, 0x00, 0x48, 0x00, 0x50, 0x00, 0x3C, 0x00, 0x48, 0x00, 0x50, 0x00, 0x58, 0x00, 0x00, 0x00, 0x48, 0x00, 0x50, 0x00, 0x58, 0x00, 0x3C }, TestName = "ASTC HDR dual-plane LDR alpha")]
	[TestCase(new byte[] { 0x42, 0x40, 0xFE, 0xFF, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x27, 0x27, 0x27, 0x27 }, new byte[] { 0xFF, 0x7B, 0xFF, 0x7B, 0xFF, 0x7B, 0x00, 0x3C, 0xFF, 0x7B, 0xFF, 0x7B, 0xFF, 0x7B, 0x00, 0x3C, 0xFF, 0x7B, 0xFF, 0x7B, 0xFF, 0x7B, 0x00, 0x3C, 0xFF, 0x7B, 0xFF, 0x7B, 0xFF, 0x7B, 0x00, 0x3C }, TestName = "ASTC HDR interpolation clamps to finite FP16")]
	[TestCase(new byte[] { 0xFC, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x40, 0x00, 0xC4, 0x00, 0x48, 0x00, 0x3C }, new byte[] { 0x00, 0x40, 0x00, 0xC4, 0x00, 0x48, 0x00, 0x3C, 0x00, 0x40, 0x00, 0xC4, 0x00, 0x48, 0x00, 0x3C, 0x00, 0x40, 0x00, 0xC4, 0x00, 0x48, 0x00, 0x3C, 0x00, 0x40, 0x00, 0xC4, 0x00, 0x48, 0x00, 0x3C }, TestName = "ASTC HDR void extent preserves literal FP16")]
	public void DecodesHdrBlock(byte[] block, byte[] row)
	{
		byte[] expected = new byte[row.Length * 4];
		for (int y = 0; y < 4; y++)
		{
			row.CopyTo(expected, y * row.Length);
		}
		AssertBlock(block, expected);
	}

	[Test]
	public void DecodesMixedLdrAndHdrPartitions()
	{
		byte[] ldrTexel = [0x00, 0x3C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3C];
		byte[] hdrTexel = [0x00, 0x44, 0x60, 0x4C, 0xC0, 0x6D, 0x00, 0x3C];
		AssertBlock([0x42, 0xA8, 0x80, 0xE7, 0x1F, 0x00, 0xE0, 0x1F, 0x31, 0xD3, 0xFD, 0x9F, 0x00, 0x00, 0x00, 0x00],
			[..hdrTexel, ..hdrTexel, ..hdrTexel, ..ldrTexel,
			 ..hdrTexel, ..hdrTexel, ..ldrTexel, ..ldrTexel,
			 ..hdrTexel, ..ldrTexel, ..ldrTexel, ..ldrTexel,
			 ..hdrTexel, ..ldrTexel, ..ldrTexel, ..ldrTexel]);
	}

	private static void AssertBlock(byte[] block, byte[] expectedHalf)
	{
		float[] expectedFloat = MemoryMarshal.Cast<byte, Half>(expectedHalf.AsSpan()).ToArray().Select(value => (float)value).ToArray();
		int bytesRead = AstcDecoder.DecodeASTC<ColorRGBA<Half>, Half>(block, 4, 4, 4, 4, out byte[] actualHalf);
		AstcDecoder.DecodeASTC<ColorRGBA<float>, float>(block, 4, 4, 4, 4, out byte[] actualFloat);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(bytesRead, Is.EqualTo(16));
			Assert.That(actualHalf, Is.EqualTo(expectedHalf));
			Assert.That(MemoryMarshal.Cast<byte, float>(actualFloat.AsSpan()).ToArray(), Is.EqualTo(expectedFloat));
		}
	}
}
