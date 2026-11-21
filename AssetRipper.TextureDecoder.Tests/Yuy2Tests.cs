using AssetRipper.TextureDecoder.Rgb.Formats;
using AssetRipper.TextureDecoder.Yuy2;
using System.Runtime.CompilerServices;

namespace AssetRipper.TextureDecoder.Tests
{
	public sealed class Yuy2Tests
	{
		private const int TestImageWidth = 256;
		private const int TestImageHeight = 256;

		[Test]
		public void PreservesByteOutputAndHighPrecisionGrayscale()
		{
			byte[] input = [17, 128, 235, 128];
			Yuy2Decoder.DecompressYUY2<ColorRGBA<byte>, byte>(input, 2, 1, out byte[] bytes);
			Yuy2Decoder.DecompressYUY2<ColorRGBA<ushort>, ushort>(input, 2, 1, out byte[] shorts);
			Yuy2Decoder.DecompressYUY2<ColorRGBA<double>, double>(input, 2, 1, out byte[] doubles);

			Assert.That(bytes, Is.EqualTo(new byte[] { 1, 1, 1, 255, 255, 255, 255, 255 }));
			Assert.That(MemoryMarshal.Cast<byte, ushort>(shorts).ToArray(), Is.EqualTo(new ushort[] { 299, 299, 299, 65535, 65535, 65535, 65535, 65535 }));
			double[] values = MemoryMarshal.Cast<byte, double>(doubles).ToArray();
			Assert.That(values[0], Is.EqualTo(1.0 / 219).Within(1e-12));
			Assert.That(values[1], Is.EqualTo(values[0]));
			Assert.That(values[2], Is.EqualTo(values[0]));
			Assert.That(values[3..], Is.EqualTo(new double[] { 1, 1, 1, 1, 1 }));
		}

		[Test]
		public void ConvertsBt601ChromaAtHighPrecision()
		{
			byte[] input = [100, 140, 100, 110];
			Yuy2Decoder.DecompressYUY2<ColorRGBA<double>, double>(input, 2, 1, out byte[] output);
			double[] values = MemoryMarshal.Cast<byte, double>(output).ToArray();
			double[] expected = [0.270900929549902, 0.422511722930287, 0.478490215264188, 1];
			Assert.That(values[..4], Is.EqualTo(expected).Within(1e-12));
			Assert.That(values[4..], Is.EqualTo(expected).Within(1e-12));
		}

		[Test]
		public void ClampsOutOfRangeLuma()
		{
			byte[] input = [0, 128, 255, 128];
			Yuy2Decoder.DecompressYUY2<ColorRGBA<double>, double>(input, 2, 1, out byte[] output);
			Assert.That(MemoryMarshal.Cast<byte, double>(output).ToArray(), Is.EqualTo(new double[] { 0, 0, 0, 1, 1, 1, 1, 1 }));
		}

		[Test]
		public void DecompressYUY2Test()
		{
			byte[] input = File.ReadAllBytes(TestFileFolders.Yuy2TestFiles + "test.yuy2");
			int bytesRead = Yuy2Decoder.DecompressYUY2<ColorBGRA<byte>, byte>(input, TestImageWidth, TestImageHeight, out byte[] decodedData);
			using (Assert.EnterMultipleScope())
			{
				Assert.That(bytesRead, Is.EqualTo(input.Length));
				Assert.That(decodedData, Has.Length.EqualTo(TestImageWidth * TestImageHeight * Unsafe.SizeOf<ColorBGRA<byte>>()));
			}
		}
	}
}
