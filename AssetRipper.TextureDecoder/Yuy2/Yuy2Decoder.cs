using AssetRipper.TextureDecoder.Rgb;

namespace AssetRipper.TextureDecoder.Yuy2
{
	/// <summary>
	/// Provides methods for decompressing YUY2 images into various color formats.<br/>
	/// <see href="https://en.wikipedia.org/wiki/YUV"/><br/>
	/// <see href="https://learn.microsoft.com/en-us/windows/win32/medfound/about-yuv-video"/>
	/// </summary>
	public static class Yuy2Decoder
	{
		/// <summary>
		/// Decompress a YUY2 image
		/// </summary>
		/// <remarks>Input is interpreted as limited-range BT.601 Y'CbCr; output is nonlinear RGB.</remarks>
		/// <typeparam name="TOutputColor">The <see cref="IColor{T}"/> type used for each pixel.</typeparam>
		/// <typeparam name="TOutputChannelValue">The channel type used in <typeparamref name="TOutputColor"/>.</typeparam>
		/// <param name="input">Input buffer containing the compressed image.</param>
		/// <param name="width">Pixel width of the image.</param>
		/// <param name="height">Pixel height of the image.</param>
		/// <param name="output">An output buffer. Must be at least width * height * pixelSize.</param>
		/// <returns>Number of bytes read from <paramref name="input"/></returns>
		public static int DecompressYUY2<TOutputColor, TOutputChannelValue>(ReadOnlySpan<byte> input, int width, int height, out byte[] output)
			where TOutputChannelValue : unmanaged
			where TOutputColor : unmanaged, IColor<TOutputChannelValue>
		{
			output = new byte[width * height * Unsafe.SizeOf<TOutputColor>()];
			return DecompressYUY2<TOutputColor, TOutputChannelValue>(input, width, height, output);
		}

		/// <summary>
		/// Decompress a YUY2 image
		/// </summary>
		/// <remarks>Input is interpreted as limited-range BT.601 Y'CbCr; output is nonlinear RGB.</remarks>
		/// <typeparam name="TOutputColor">The <see cref="IColor{T}"/> type used for each pixel.</typeparam>
		/// <typeparam name="TOutputChannelValue">The channel type used in <typeparamref name="TOutputColor"/>.</typeparam>
		/// <param name="input">Input buffer containing the compressed image.</param>
		/// <param name="width">Pixel width of the image.</param>
		/// <param name="height">Pixel height of the image.</param>
		/// <param name="output">An output buffer. Must be at least width * height * pixelSize.</param>
		/// <returns>Number of bytes read from <paramref name="input"/></returns>
		public static int DecompressYUY2<TOutputColor, TOutputChannelValue>(ReadOnlySpan<byte> input, int width, int height, Span<byte> output)
			where TOutputChannelValue : unmanaged
			where TOutputColor : unmanaged, IColor<TOutputChannelValue>
		{
			ThrowHelper.ThrowIfNotLittleEndian();
			return DecompressYUY2<TOutputColor, TOutputChannelValue>(input, width, height, MemoryMarshal.Cast<byte, TOutputColor>(output));
		}

		/// <summary>
		/// Decompress a YUY2 image
		/// </summary>
		/// <remarks>Input is interpreted as limited-range BT.601 Y'CbCr; output is nonlinear RGB.</remarks>
		/// <typeparam name="TOutputColor">The <see cref="IColor{T}"/> type used for each pixel.</typeparam>
		/// <typeparam name="TOutputChannelValue">The channel type used in <typeparamref name="TOutputColor"/>.</typeparam>
		/// <param name="input">Input buffer containing the compressed image.</param>
		/// <param name="width">Pixel width of the image.</param>
		/// <param name="height">Pixel height of the image.</param>
		/// <param name="output">An output buffer. Must be at least width * height.</param>
		/// <returns>Number of bytes read from <paramref name="input"/></returns>
		public static int DecompressYUY2<TOutputColor, TOutputChannelValue>(ReadOnlySpan<byte> input, int width, int height, Span<TOutputColor> output)
			where TOutputChannelValue : unmanaged
			where TOutputColor : unmanaged, IColor<TOutputChannelValue>
		{
			ThrowHelper.ThrowIfNotEnoughSpace(output.Length, width * height);

			int p = 0;
			int o = 0;
			int halfWidth = width / 2;
			for (int j = 0; j < height; j++)
			{
				for (int i = 0; i < halfWidth; ++i)
				{
					int y0 = input[p++];
					int u0 = input[p++];
					int y1 = input[p++];
					int v0 = input[p++];
					int c = y0 - 16;
					int d = u0 - 128;
					int e = v0 - 128;
					if (typeof(TOutputChannelValue) == typeof(byte))
					{
						SetByteColor(ref output[o++], c, d, e);
						SetByteColor(ref output[o++], y1 - 16, d, e);
					}
					else
					{
						SetPreciseColor(ref output[o++], c, d, e);
						SetPreciseColor(ref output[o++], y1 - 16, d, e);
					}
				}
			}

			return p;

			static void SetByteColor(ref TOutputColor color, int c, int d, int e)
			{
				byte b = ClampByte((298 * c + 516 * d + 128) >> 8);
				byte g = ClampByte((298 * c - 100 * d - 208 * e + 128) >> 8);
				byte r = ClampByte((298 * c + 409 * e + 128) >> 8);
				color.SetConvertedChannels<TOutputColor, TOutputChannelValue, byte>(r, g, b);
			}

			static void SetPreciseColor(ref TOutputColor color, int c, int d, int e)
			{
				double y = c / 219.0;
				double cb = d / 224.0;
				double cr = e / 224.0;
				double r = double.Clamp(y + 1.402 * cr, 0, 1);
				double g = double.Clamp(y - (0.114 * 1.772 / 0.587) * cb - (0.299 * 1.402 / 0.587) * cr, 0, 1);
				double b = double.Clamp(y + 1.772 * cb, 0, 1);
				color.SetConvertedChannels<TOutputColor, TOutputChannelValue, double>(r, g, b);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
		private static byte ClampByte(int x)
		{
			return (byte)(byte.MaxValue < x ? byte.MaxValue : (x > byte.MinValue ? x : byte.MinValue));
		}
	}
}
