using AssetRipper.TextureDecoder.Rgb;
using System.Buffers.Binary;
using System.Diagnostics;

namespace AssetRipper.TextureDecoder.Astc
{
	public static partial class AstcDecoder
	{
		// ASTC HDR endpoints use 12-bit pseudo-logarithmic values (Khronos ASTC spec C.2.15).
		private const int HdrOpaque = 0x780;
		private const int HdrEndpointMax = 0xFFF;

		private static void ValidateBlockDimension(int value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
		{
			if (value < 4 || value > 12)
			{
				throw new ArgumentException($"Block dimension must be between 4 and 12, inclusive. Value: {value}", paramName);
			}
		}

		public static int DecodeASTC<TOutputColor, TOutputChannelValue>(ReadOnlySpan<byte> input, int width, int height, int blockWidth, int blockHeight, out byte[] output)
			where TOutputChannelValue : unmanaged
			where TOutputColor : unmanaged, IColor<TOutputChannelValue>
		{
			output = new byte[width * height * Unsafe.SizeOf<TOutputColor>()];
			return DecodeASTC<TOutputColor, TOutputChannelValue>(input, width, height, blockWidth, blockHeight, output);
		}

		public static int DecodeASTC<TOutputColor, TOutputChannelValue>(ReadOnlySpan<byte> input, int width, int height, int blockWidth, int blockHeight, Span<byte> output)
			where TOutputChannelValue : unmanaged
			where TOutputColor : unmanaged, IColor<TOutputChannelValue>
		{
			ThrowHelper.ThrowIfNotLittleEndian();
			return DecodeASTC<TOutputColor, TOutputChannelValue>(input, width, height, blockWidth, blockHeight, MemoryMarshal.Cast<byte, TOutputColor>(output));
		}

		public static int DecodeASTC<TOutputColor, TOutputChannelValue>(ReadOnlySpan<byte> input, int width, int height, int blockWidth, int blockHeight, Span<TOutputColor> output)
			where TOutputChannelValue : unmanaged
			where TOutputColor : unmanaged, IColor<TOutputChannelValue>
		{
			ThrowHelper.ThrowIfNotEnoughSpace(output.Length, width * height);
			ValidateBlockDimension(blockWidth);
			ValidateBlockDimension(blockHeight);
			int bcw = (width + blockWidth - 1) / blockWidth;
			int bch = (height + blockHeight - 1) / blockHeight;
			int clen_last = (width + blockWidth - 1) % blockWidth + 1;
			Span<TOutputColor> buffer = stackalloc TOutputColor[blockWidth * blockHeight];
			int inputOffset = 0;
			for (int t = 0; t < bch; t++)
			{
				for (int s = 0; s < bcw; s++, inputOffset += 16)
				{
					DecodeBlock<TOutputColor, TOutputChannelValue>(input.Slice(inputOffset, 16), blockWidth, blockHeight, buffer);
					int clen = s < bcw - 1 ? blockWidth : clen_last;
					for (int i = 0, y = t * blockHeight; i < blockHeight && y < height; i++, y++)
					{
						int outputOffsetUInt32 = t * blockHeight * width + s * blockWidth + i * width;
						for (int j = 0; j < clen; j++)
						{
							output[outputOffsetUInt32 + j] = buffer[j + i * blockWidth];
						}
					}
				}
			}
			return inputOffset;
		}

		private static void DecodeBlock<TOutputColor, TOutputChannelValue>(ReadOnlySpan<byte> input, int blockWidth, int blockHeight, Span<TOutputColor> output)
			where TOutputChannelValue : unmanaged
			where TOutputColor : unmanaged, IColor<TOutputChannelValue>
		{
			if (input[0] == 0xfc && (input[1] & 1) == 1)
			{
				int lowS = GetBits(input, 12, 13);
				int highS = GetBits(input, 25, 13);
				int lowT = GetBits(input, 38, 13);
				int highT = GetBits(input, 51, 13);
				bool allOnes = lowS == 0x1fff && highS == 0x1fff && lowT == 0x1fff && highT == 0x1fff;
				if ((input[1] & 0x0c) != 0x0c || (!allOnes && (lowS >= highS || lowT >= highT)))
				{
					FillErrorColor<TOutputColor, TOutputChannelValue>(output);
					return;
				}

				// Constant color block, officially called a "void extent" block
				TOutputColor c = default;

				// Check what format the data has
				if ((input[1] & 2) == 0)
				{
					// U16
					c.SetChannels(
						ConvertUnorm16<TOutputChannelValue>(BinaryPrimitives.ReadUInt16LittleEndian(input[8..])),
						ConvertUnorm16<TOutputChannelValue>(BinaryPrimitives.ReadUInt16LittleEndian(input[10..])),
						ConvertUnorm16<TOutputChannelValue>(BinaryPrimitives.ReadUInt16LittleEndian(input[12..])),
						ConvertUnorm16<TOutputChannelValue>(BinaryPrimitives.ReadUInt16LittleEndian(input[14..])));
				}
				else
				{
					// F16
					c.SetConvertedChannels<TOutputColor, TOutputChannelValue, Half>(
						BinaryPrimitives.ReadHalfLittleEndian(input[8..]),
						BinaryPrimitives.ReadHalfLittleEndian(input[10..]),
						BinaryPrimitives.ReadHalfLittleEndian(input[12..]),
						BinaryPrimitives.ReadHalfLittleEndian(input[14..]));
				}
				output[..(blockWidth * blockHeight)].Fill(c);
			}
			else
			{
				BlockData blockData = new();
				blockData.bw = blockWidth;
				blockData.bh = blockHeight;
				if (!DecodeBlockParameters(input, ref blockData))
				{
					FillErrorColor<TOutputColor, TOutputChannelValue>(output);
					return;
				}
				DecodeEndpoints(input, ref blockData);
				DecodeWeights(input, ref blockData);
				if (blockData.partition_count > 1)
				{
					SelectPartition(input, ref blockData);
				}
				ApplyColor<TOutputColor, TOutputChannelValue>(in blockData, output);
			}
		}

		private static void FillErrorColor<TOutputColor, TOutputChannelValue>(Span<TOutputColor> output)
			where TOutputChannelValue : unmanaged
			where TOutputColor : unmanaged, IColor<TOutputChannelValue>
		{
			// Opaque magenta is the ASTC LDR error color (Khronos ASTC spec C.2.5).
			TOutputColor error = default;
			error.SetConvertedChannels<TOutputColor, TOutputChannelValue, byte>(255, 0, 255, 255);
			output.Fill(error);
		}

		private static bool DecodeBlockParameters(ReadOnlySpan<byte> input, ref BlockData pBlock)
		{
			if ((input[0] & 0x0f) == 0 || ((input[0] & 0xc3) == 0xc0 && (input[1] & 1) != 0))
			{
				return false;
			}
			pBlock.dual_plane = (input[1] & 4) >> 2;
			pBlock.weight_range = (input[0] >> 4 & 1) | (input[1] << 2 & 8);

			if ((input[0] & 3) != 0)
			{
				pBlock.weight_range |= input[0] << 1 & 6;
				switch (input[0] & 0xc)
				{
					case 0x0:
						pBlock.width = (BinaryPrimitives.ReadInt32LittleEndian(input) >> 7 & 3) + 4;
						pBlock.height = (input[0] >> 5 & 3) + 2;
						break;
					case 0x4:
						pBlock.width = (BinaryPrimitives.ReadInt32LittleEndian(input) >> 7 & 3) + 8;
						pBlock.height = (input[0] >> 5 & 3) + 2;
						break;
					case 0x8:
						pBlock.width = (input[0] >> 5 & 3) + 2;
						pBlock.height = (BinaryPrimitives.ReadInt32LittleEndian(input) >> 7 & 3) + 8;
						break;
					case 0xc:
						if ((input[1] & 1) != 0)
						{
							pBlock.width = (input[0] >> 7 & 1) + 2;
							pBlock.height = (input[0] >> 5 & 3) + 2;
						}
						else
						{
							pBlock.width = (input[0] >> 5 & 3) + 2;
							pBlock.height = (input[0] >> 7 & 1) + 6;
						}
						break;
				}
			}
			else
			{
				pBlock.weight_range |= input[0] >> 1 & 6;
				switch (BinaryPrimitives.ReadInt32LittleEndian(input) & 0x180)
				{
					case 0x000:
						pBlock.width = 12;
						pBlock.height = (input[0] >> 5 & 3) + 2;
						break;
					case 0x080:
						pBlock.width = (input[0] >> 5 & 3) + 2;
						pBlock.height = 12;
						break;
					case 0x100:
						pBlock.width = (input[0] >> 5 & 3) + 6;
						pBlock.height = (input[1] >> 1 & 3) + 6;
						pBlock.dual_plane = 0;
						pBlock.weight_range &= 7;
						break;
					case 0x180:
						pBlock.width = (input[0] & 0x20) != 0 ? 10 : 6;
						pBlock.height = (input[0] & 0x20) != 0 ? 6 : 10;
						break;
				}
			}

			pBlock.partition_count = (input[1] >> 3 & 3) + 1;

			pBlock.weight_count = pBlock.width * pBlock.height;
			if (pBlock.dual_plane != 0)
			{
				pBlock.weight_count *= 2;
			}

			int config_bits, cem_base = 0;
			int weight_bits = WeightPrecTableA[pBlock.weight_range] switch
			{
				3 => pBlock.weight_count * WeightPrecTableB[pBlock.weight_range] + (pBlock.weight_count * 8 + 4) / 5,
				5 => pBlock.weight_count * WeightPrecTableB[pBlock.weight_range] + (pBlock.weight_count * 7 + 2) / 3,
				_ => pBlock.weight_count * WeightPrecTableB[pBlock.weight_range],
			};
			if ((pBlock.weight_range & 7) < 2 || pBlock.weight_count > 64
				|| pBlock.width > pBlock.bw || pBlock.height > pBlock.bh
				|| weight_bits is < 24 or > 96
				|| (pBlock.dual_plane != 0 && pBlock.partition_count == 4))
			{
				return false;
			}
			if (pBlock.partition_count == 1)
			{
				pBlock.cem[0] = BinaryPrimitives.ReadInt32LittleEndian(input[1..]) >> 5 & 0xf;
				config_bits = 17;
			}
			else
			{
				cem_base = BinaryPrimitives.ReadInt32LittleEndian(input[2..]) >> 7 & 3;
				if (cem_base == 0)
				{
					int cem = input[3] >> 1 & 0xf;
					for (int i = 0; i < pBlock.partition_count; i++)
					{
						pBlock.cem[i] = cem;
					}
					config_bits = 29;
				}
				else
				{
					for (int i = 0; i < pBlock.partition_count; i++)
					{
						pBlock.cem[i] = ((input[3] >> (i + 1) & 1) + cem_base - 1) << 2;
					}
					switch (pBlock.partition_count)
					{
						case 2:
							pBlock.cem[0] |= input[3] >> 3 & 3;
							pBlock.cem[1] |= GetBits(input, 126 - weight_bits, 2);
							break;
						case 3:
							pBlock.cem[0] |= input[3] >> 4 & 1;
							pBlock.cem[0] |= GetBits(input, 122 - weight_bits, 2) & 2;
							pBlock.cem[1] |= GetBits(input, 124 - weight_bits, 2);
							pBlock.cem[2] |= GetBits(input, 126 - weight_bits, 2);
							break;
						case 4:
							for (int i = 0; i < 4; i++)
							{
								pBlock.cem[i] |= GetBits(input, 120 + i * 2 - weight_bits, 2);
							}
							break;
					}
					config_bits = 25 + pBlock.partition_count * 3;
				}
			}

			if (pBlock.dual_plane != 0)
			{
				config_bits += 2;
				pBlock.plane_selector = GetBits(input, cem_base != 0 ? 130 - weight_bits - pBlock.partition_count * 3 : 126 - weight_bits, 2);
			}

			int remain_bits = 128 - config_bits - weight_bits;

			pBlock.endpoint_value_num = 0;
			for (int i = 0; i < pBlock.partition_count; i++)
			{
				pBlock.endpoint_value_num += (pBlock.cem[i] >> 1 & 6) + 2;
			}

			if (pBlock.endpoint_value_num > 18 || remain_bits < (13 * pBlock.endpoint_value_num + 4) / 5)
			{
				return false;
			}

			for (int i = 0, endpoint_bits; i < CemTableA.Length; i++)
			{
				endpoint_bits = CemTableA[i] switch
				{
					3 => pBlock.endpoint_value_num * CemTableB[i] + (pBlock.endpoint_value_num * 8 + 4) / 5,
					5 => pBlock.endpoint_value_num * CemTableB[i] + (pBlock.endpoint_value_num * 7 + 2) / 3,
					_ => pBlock.endpoint_value_num * CemTableB[i],
				};
				if (endpoint_bits <= remain_bits)
				{
					pBlock.cem_range = i;
					return true;
				}
			}
			return false;
		}

		private static void DecodeEndpoints(ReadOnlySpan<byte> input, ref BlockData pBlock)
		{
			Span<IntSeqData> epSeq = stackalloc IntSeqData[32];
			DecodeIntseq(input, pBlock.partition_count == 1 ? 17 : 29, CemTableA[pBlock.cem_range], CemTableB[pBlock.cem_range], pBlock.endpoint_value_num, false, epSeq);

			Span<int> ev = stackalloc int[32];
			switch (CemTableA[pBlock.cem_range])
			{
				case 3:
					for (int i = 0, b = 0, c = DETritsTable[CemTableB[pBlock.cem_range]]; i < pBlock.endpoint_value_num; i++)
					{
						int a = (epSeq[i].bits & 1) * 0x1ff;
						int x = epSeq[i].bits >> 1;
						switch (CemTableB[pBlock.cem_range])
						{
							case 1:
								b = 0;
								break;
							case 2:
								b = 0b100010110 * x;
								break;
							case 3:
								b = x << 7 | x << 2 | x;
								break;
							case 4:
								b = x << 6 | x;
								break;
							case 5:
								b = x << 5 | x >> 2;
								break;
							case 6:
								b = x << 4 | x >> 4;
								break;
						}
						ev[i] = (a & 0x80) | ((epSeq[i].nonbits * c + b) ^ a) >> 2;
					}
					break;

				case 5:
					for (int i = 0, b = 0, c = DEQuintsTable[CemTableB[pBlock.cem_range]]; i < pBlock.endpoint_value_num; i++)
					{
						int a = (epSeq[i].bits & 1) * 0x1ff;
						int x = epSeq[i].bits >> 1;
						switch (CemTableB[pBlock.cem_range])
						{
							case 1:
								b = 0;
								break;
							case 2:
								b = 0b100001100 * x;
								break;
							case 3:
								b = x << 7 | x << 1 | x >> 1;
								break;
							case 4:
								b = x << 6 | x >> 1;
								break;
							case 5:
								b = x << 5 | x >> 3;
								break;
						}
						ev[i] = (a & 0x80) | ((epSeq[i].nonbits * c + b) ^ a) >> 2;
					}
					break;

				default:
					switch (CemTableB[pBlock.cem_range])
					{
						case 1:
							for (int i = 0; i < pBlock.endpoint_value_num; i++)
							{
								ev[i] = epSeq[i].bits * 0xff;
							}
							break;
						case 2:
							for (int i = 0; i < pBlock.endpoint_value_num; i++)
							{
								ev[i] = epSeq[i].bits * 0x55;
							}
							break;
						case 3:
							for (int i = 0; i < pBlock.endpoint_value_num; i++)
							{
								ev[i] = epSeq[i].bits << 5 | epSeq[i].bits << 2 | epSeq[i].bits >> 1;
							}
							break;
						case 4:
							for (int i = 0; i < pBlock.endpoint_value_num; i++)
							{
								ev[i] = epSeq[i].bits << 4 | epSeq[i].bits;
							}
							break;
						case 5:
							for (int i = 0; i < pBlock.endpoint_value_num; i++)
							{
								ev[i] = epSeq[i].bits << 3 | epSeq[i].bits >> 2;
							}
							break;
						case 6:
							for (int i = 0; i < pBlock.endpoint_value_num; i++)
							{
								ev[i] = epSeq[i].bits << 2 | epSeq[i].bits >> 4;
							}
							break;
						case 7:
							for (int i = 0; i < pBlock.endpoint_value_num; i++)
							{
								ev[i] = epSeq[i].bits << 1 | epSeq[i].bits >> 6;
							}
							break;
						case 8:
							for (int i = 0; i < pBlock.endpoint_value_num; i++)
							{
								ev[i] = epSeq[i].bits;
							}
							break;
					}
					break;
			}

			Span<int> v = ev;
			for (int cem = 0; cem < pBlock.partition_count; v = v.Slice((pBlock.cem[cem] / 4 + 1) * 2), cem++)
			{
				switch (pBlock.cem[cem])
				{
					case 0:
						SetEndpoint(pBlock.endpoints[cem], v[0], v[0], v[0], 255, v[1], v[1], v[1], 255);
						break;
					case 1:
						{
							int l0 = (v[0] >> 2) | (v[1] & 0xc0);
							int l1 = Clamp(l0 + (v[1] & 0x3f));
							SetEndpoint(pBlock.endpoints[cem], l0, l0, l0, 255, l1, l1, l1, 255);
						}
						break;
					case 2:
						{
							int v0 = v[0];
							int v1 = v[1];
							int l0 = v0 <= v1 ? v0 << 4 : (v1 << 4) + 8;
							int l1 = v0 <= v1 ? v1 << 4 : (v0 << 4) - 8;
							SetEndpoint(pBlock.endpoints[cem], l0, l0, l0, HdrOpaque, l1, l1, l1, HdrOpaque);
						}
						break;
					case 3:
						{
							int v0 = v[0];
							int v1 = v[1];
							bool wideDelta = (v0 & 0x80) != 0;
							int deltaMask = wideDelta ? 0x1F : 0x0F;
							int shift = wideDelta ? 2 : 1;
							int l0 = ((v1 & ~deltaMask) << 4) | ((v0 & 0x7F) << shift);
							int l1 = int.Min(l0 + ((v1 & deltaMask) << shift), HdrEndpointMax);
							SetEndpoint(pBlock.endpoints[cem], l0, l0, l0, HdrOpaque, l1, l1, l1, HdrOpaque);
						}
						break;
					case 4:
						SetEndpoint(pBlock.endpoints[cem], v[0], v[0], v[0], v[2], v[1], v[1], v[1], v[3]);
						break;
					case 5:
						BitTransferSigned(ref v[1], ref v[0]);
						BitTransferSigned(ref v[3], ref v[2]);
						v[1] += v[0];
						SetEndpointClamp(pBlock.endpoints[cem], v[0], v[0], v[0], v[2], v[1], v[1], v[1], v[2] + v[3]);
						break;
					case 6:
						SetEndpoint(pBlock.endpoints[cem], v[0] * v[3] >> 8, v[1] * v[3] >> 8, v[2] * v[3] >> 8, 255, v[0], v[1], v[2], 255);
						break;
					case 7:
						DecodeHdrRgbOffset(v, pBlock.endpoints[cem]);
						break;
					case 8:
						if (v[0] + v[2] + v[4] <= v[1] + v[3] + v[5])
						{
							SetEndpoint(pBlock.endpoints[cem], v[0], v[2], v[4], 255, v[1], v[3], v[5], 255);
						}
						else
						{
							SetEndpointBlue(pBlock.endpoints[cem], v[1], v[3], v[5], 255, v[0], v[2], v[4], 255);
						}

						break;
					case 9:
						BitTransferSigned(ref v[1], ref v[0]);
						BitTransferSigned(ref v[3], ref v[2]);
						BitTransferSigned(ref v[5], ref v[4]);
						if (v[1] + v[3] + v[5] >= 0)
						{
							SetEndpointClamp(pBlock.endpoints[cem], v[0], v[2], v[4], 255, v[0] + v[1], v[2] + v[3], v[4] + v[5], 255);
						}
						else
						{
							SetEndpointBlueClamp(pBlock.endpoints[cem], v[0] + v[1], v[2] + v[3], v[4] + v[5], 255, v[0], v[2], v[4], 255);
						}

						break;
					case 10:
						SetEndpoint(pBlock.endpoints[cem], v[0] * v[3] >> 8, v[1] * v[3] >> 8, v[2] * v[3] >> 8, v[4], v[0], v[1], v[2], v[5]);
						break;
					case 11:
						DecodeHdrRgb(v, pBlock.endpoints[cem]);
						break;
					case 12:
						if (v[0] + v[2] + v[4] <= v[1] + v[3] + v[5])
						{
							SetEndpoint(pBlock.endpoints[cem], v[0], v[2], v[4], v[6], v[1], v[3], v[5], v[7]);
						}
						else
						{
							SetEndpointBlue(pBlock.endpoints[cem], v[1], v[3], v[5], v[7], v[0], v[2], v[4], v[6]);
						}

						break;
					case 13:
						BitTransferSigned(ref v[1], ref v[0]);
						BitTransferSigned(ref v[3], ref v[2]);
						BitTransferSigned(ref v[5], ref v[4]);
						BitTransferSigned(ref v[7], ref v[6]);
						if (v[1] + v[3] + v[5] >= 0)
						{
							SetEndpointClamp(pBlock.endpoints[cem], v[0], v[2], v[4], v[6], v[0] + v[1], v[2] + v[3], v[4] + v[5], v[6] + v[7]);
						}
						else
						{
							SetEndpointBlueClamp(pBlock.endpoints[cem], v[0] + v[1], v[2] + v[3], v[4] + v[5], v[6] + v[7], v[0], v[2], v[4], v[6]);
						}

						break;
					case 14:
						DecodeHdrRgb(v, pBlock.endpoints[cem]);
						pBlock.endpoints[cem][3] = v[6];
						pBlock.endpoints[cem][7] = v[7];
						break;
					case 15:
						DecodeHdrRgb(v, pBlock.endpoints[cem]);
						DecodeHdrAlpha(v[6], v[7], out pBlock.endpoints[cem][3], out pBlock.endpoints[cem][7]);
						break;
				}
			}
		}

		private static void DecodeHdrRgbOffset(ReadOnlySpan<int> v, Span<int> endpoint)
		{
			int packedMode = (v[0] >> 6) | ((v[1] & 0x80) >> 5) | ((v[2] & 0x80) >> 4);
			int mode = packedMode < 12 ? packedMode & 3 : packedMode == 15 ? 5 : 4;
			int major = packedMode < 12 ? packedMode >> 2 : packedMode == 15 ? 0 : packedMode & 3;
			Span<int> fields = [v[0] & 0x3F, v[1] & 0x1F, v[2] & 0x1F, v[3] & 0x1F];
			int extra = ((v[1] & 0x40) >> 6)
				| ((v[1] & 0x20) >> 4)
				| ((v[2] & 0x40) >> 4)
				| ((v[2] & 0x20) >> 2)
				| ((v[3] & 0x80) >> 3)
				| ((v[3] & 0x40) >> 1)
				| ((v[3] & 0x20) << 1);

			// Table C.2.20: destinations for X0 through X6, by endpoint bit mode.
			const byte R = 0, G = 16, B = 32, S = 48;
			ReadOnlySpan<byte> destinations =
			[
				R + 9, R + 8, R + 7, R + 10, R + 6, S + 6, S + 5,
				R + 8, G + 5, R + 7, B + 5, R + 6, R + 10, R + 9,
				R + 9, R + 8, R + 7, R + 6, S + 7, S + 6, S + 5,
				R + 8, G + 5, R + 7, B + 5, R + 6, S + 6, S + 5,
				G + 6, G + 5, B + 6, B + 5, R + 6, R + 7, S + 5,
				G + 6, G + 5, B + 6, B + 5, R + 6, S + 6, S + 5,
			];
			UnpackHdrExtraBits(fields, extra, destinations.Slice(mode * 7, 7));
			ReadOnlySpan<byte> shifts = [1, 1, 2, 3, 4, 5];
			int shift = shifts[mode];
			int r = fields[0] << shift;
			int g = fields[1] << shift;
			int b = fields[2] << shift;
			int offset = fields[3] << shift;
			if (mode != 5)
			{
				g = r - g;
				b = r - b;
			}
			SetHdrRgbEndpoints(endpoint, major, r - offset, g - offset, b - offset, r, g, b);
		}

		private static void DecodeHdrRgb(ReadOnlySpan<int> v, Span<int> endpoint)
		{
			int major = ((v[4] & 0x80) >> 7) | ((v[5] & 0x80) >> 6);
			if (major == 3)
			{
				SetEndpoint(endpoint, v[0] << 4, v[2] << 4, (v[4] & 0x7F) << 5, HdrOpaque, v[1] << 4, v[3] << 4, (v[5] & 0x7F) << 5, HdrOpaque);
				return;
			}

			int mode = ((v[1] & 0x80) >> 7) | ((v[2] & 0x80) >> 6) | ((v[3] & 0x80) >> 5);
			Span<int> fields =
			[
				v[0] | ((v[1] & 0x40) << 2),
				v[2] & 0x3F,
				v[3] & 0x3F,
				v[1] & 0x3F,
				v[4] & 0x1F,
				v[5] & 0x1F
			];
			int extra = ((v[2] & 0x40) >> 6) | ((v[3] & 0x40) >> 5)
				| ((v[4] & 0x40) >> 4) | ((v[5] & 0x40) >> 3)
				| ((v[4] & 0x20) >> 1) | (v[5] & 0x20);

			// Table C.2.23: destinations for X0 through X5, by endpoint bit mode.
			const byte A = 0, B0 = 16, B1 = 32, C = 48, D0 = 64, D1 = 80;
			ReadOnlySpan<byte> destinations =
			[
				B0 + 6, B1 + 6, D0 + 6, D1 + 6, D0 + 5, D1 + 5,
				B0 + 6, B1 + 6, B0 + 7, B1 + 7, D0 + 5, D1 + 5,
				A + 9, C + 6, D0 + 6, D1 + 6, D0 + 5, D1 + 5,
				B0 + 6, B1 + 6, A + 9, C + 6, D0 + 5, D1 + 5,
				B0 + 6, B1 + 6, B0 + 7, B1 + 7, A + 9, A + 10,
				A + 9, A + 10, C + 7, C + 6, D0 + 5, D1 + 5,
				B0 + 6, B1 + 6, A + 11, C + 6, A + 9, A + 10,
				A + 9, A + 10, A + 11, C + 6, D0 + 5, D1 + 5,
			];
			UnpackHdrExtraBits(fields, extra, destinations.Slice(mode * 6, 6));
			ReadOnlySpan<byte> deltaWidths = [7, 6, 7, 6, 5, 6, 5, 6];
			int d0 = SignExtendHdrDelta(fields[4], deltaWidths[mode]);
			int d1 = SignExtendHdrDelta(fields[5], deltaWidths[mode]);
			int shift = 3 - (mode >> 1);
			int r1 = fields[0];
			int g1 = r1 - fields[1];
			int b1 = r1 - fields[2];
			int c = fields[3];
			SetHdrRgbEndpoints(endpoint, major, (r1 - c) << shift, (g1 - c - d0) << shift, (b1 - c - d1) << shift, r1 << shift, g1 << shift, b1 << shift);
		}

		private static void DecodeHdrAlpha(int v0, int v1, out int a0, out int a1)
		{
			int mode = ((v0 & 0x80) >> 7) | ((v1 & 0x80) >> 6);
			v0 &= 0x7F;
			v1 &= 0x7F;
			if (mode == 3)
			{
				a0 = v0 << 5;
				a1 = v1 << 5;
				return;
			}

			int shift = 4 - mode;
			int deltaWidth = 6 - mode;
			a0 = (v0 | ((v1 << (mode + 1)) & 0x780)) << shift;
			int delta = SignExtendHdrDelta(v1 & ((1 << deltaWidth) - 1), deltaWidth);
			a1 = int.Clamp(a0 + (delta << shift), 0, HdrEndpointMax);
		}

		private static int SignExtendHdrDelta(int value, int width)
		{
			int sign = 1 << (width - 1);
			return (value ^ sign) - sign;
		}

		private static void UnpackHdrExtraBits(Span<int> fields, int extra, ReadOnlySpan<byte> destinations)
		{
			for (int i = 0; i < destinations.Length; i++)
			{
				int destination = destinations[i];
				fields[destination >> 4] |= ((extra >> i) & 1) << (destination & 15);
			}
		}

		private static void SetHdrRgbEndpoints(Span<int> endpoint, int major, int r0, int g0, int b0, int r1, int g1, int b1)
		{
			SetEndpoint(
				endpoint,
				int.Clamp(r0, 0, HdrEndpointMax),
				int.Clamp(g0, 0, HdrEndpointMax),
				int.Clamp(b0, 0, HdrEndpointMax),
				HdrOpaque,
				int.Clamp(r1, 0, HdrEndpointMax),
				int.Clamp(g1, 0, HdrEndpointMax),
				int.Clamp(b1, 0, HdrEndpointMax),
				HdrOpaque);
			if (major != 0)
			{
				(endpoint[0], endpoint[major]) = (endpoint[major], endpoint[0]);
				(endpoint[4], endpoint[4 + major]) = (endpoint[4 + major], endpoint[4]);
			}
		}

		private static void DecodeWeights(ReadOnlySpan<byte> input, ref BlockData block)
		{
			Span<IntSeqData> wSeq = stackalloc IntSeqData[128];
			DecodeIntseq(input, 128, WeightPrecTableA[block.weight_range], WeightPrecTableB[block.weight_range], block.weight_count, true, wSeq);

			Span<int> wv = stackalloc int[128];
			if (WeightPrecTableA[block.weight_range] == 0)
			{
				switch (WeightPrecTableB[block.weight_range])
				{
					case 1:
						for (int i = 0; i < block.weight_count; i++)
						{
							wv[i] = wSeq[i].bits != 0 ? 63 : 0;
						}

						break;
					case 2:
						for (int i = 0; i < block.weight_count; i++)
						{
							wv[i] = wSeq[i].bits << 4 | wSeq[i].bits << 2 | wSeq[i].bits;
						}

						break;
					case 3:
						for (int i = 0; i < block.weight_count; i++)
						{
							wv[i] = wSeq[i].bits << 3 | wSeq[i].bits;
						}

						break;
					case 4:
						for (int i = 0; i < block.weight_count; i++)
						{
							wv[i] = wSeq[i].bits << 2 | wSeq[i].bits >> 2;
						}

						break;
					case 5:
						for (int i = 0; i < block.weight_count; i++)
						{
							wv[i] = wSeq[i].bits << 1 | wSeq[i].bits >> 4;
						}

						break;
				}
				for (int i = 0; i < block.weight_count; i++)
				{
					if (wv[i] > 32)
					{
						++wv[i];
					}
				}
			}
			else if (WeightPrecTableB[block.weight_range] == 0)
			{
				int s = WeightPrecTableA[block.weight_range] == 3 ? 32 : 16;
				for (int i = 0; i < block.weight_count; i++)
				{
					wv[i] = wSeq[i].nonbits * s;
				}
			}
			else
			{
				if (WeightPrecTableA[block.weight_range] == 3)
				{
					switch (WeightPrecTableB[block.weight_range])
					{
						case 1:
							for (int i = 0; i < block.weight_count; i++)
							{
								wv[i] = wSeq[i].nonbits * 50;
							}
							break;
						case 2:
							for (int i = 0; i < block.weight_count; i++)
							{
								wv[i] = wSeq[i].nonbits * 23;
								if ((wSeq[i].bits & 2) != 0)
								{
									wv[i] += 0b1000101;
								}
							}
							break;
						case 3:
							for (int i = 0; i < block.weight_count; i++)
							{
								wv[i] = wSeq[i].nonbits * 11 + ((wSeq[i].bits << 4 | wSeq[i].bits >> 1) & 0b1100011);
							}
							break;
					}
				}
				else if (WeightPrecTableA[block.weight_range] == 5)
				{
					switch (WeightPrecTableB[block.weight_range])
					{
						case 1:
							for (int i = 0; i < block.weight_count; i++)
							{
								wv[i] = wSeq[i].nonbits * 28;
							}

							break;
						case 2:
							for (int i = 0; i < block.weight_count; i++)
							{
								wv[i] = wSeq[i].nonbits * 13;
								if ((wSeq[i].bits & 2) != 0)
								{
									wv[i] += 0b1000010;
								}
							}
							break;
					}
				}
				for (int i = 0; i < block.weight_count; i++)
				{
					int a = (wSeq[i].bits & 1) * 0x7f;
					wv[i] = (a & 0x20) | ((wv[i] ^ a) >> 2);
					if (wv[i] > 32)
					{
						++wv[i];
					}
				}
			}

			int ds = (1024 + block.bw / 2) / (block.bw - 1);
			int dt = (1024 + block.bh / 2) / (block.bh - 1);
			int pn = block.dual_plane != 0 ? 2 : 1;

			for (int t = 0, i = 0; t < block.bh; t++)
			{
				for (int s = 0; s < block.bw; s++, i++)
				{
					int gs = (ds * s * (block.width - 1) + 32) >> 6;
					int gt = (dt * t * (block.height - 1) + 32) >> 6;
					int fs = gs & 0xf;
					int ft = gt & 0xf;
					int v = (gs >> 4) + (gt >> 4) * block.width;
					int w11 = (fs * ft + 8) >> 4;
					int w10 = ft - w11;
					int w01 = fs - w11;
					int w00 = 16 - fs - ft + w11;

					for (int p = 0; p < pn; p++)
					{
						int p00 = wv[v * pn + p];
						int p01 = wv[(v + 1) * pn + p];
						int p10 = wv[(v + block.width) * pn + p];
						int p11 = wv[(v + block.width + 1) * pn + p];
						block.weights[i * 2 + p] = (p00 * w00 + p01 * w01 + p10 * w10 + p11 * w11 + 8) >> 4;
					}
				}
			}
		}

		private static void SelectPartition(ReadOnlySpan<byte> input, ref BlockData block)
		{
			bool small_block = block.bw * block.bh < 31;
			int seed = (BinaryPrimitives.ReadInt32LittleEndian(input) >> 13 & 0x3ff) | (block.partition_count - 1) << 10;

			uint rnum;
			unchecked
			{
				rnum = (uint)seed;
				rnum ^= rnum >> 15;
				rnum -= rnum << 17;
				rnum += rnum << 7;
				rnum += rnum << 4;
				rnum ^= rnum >> 5;
				rnum += rnum << 16;
				rnum ^= rnum >> 7;
				rnum ^= rnum >> 3;
				rnum ^= rnum << 6;
				rnum ^= rnum >> 17;
			}

			Span<int> seeds = stackalloc int[8];
			for (int i = 0; i < 8; i++)
			{
				seeds[i] = (int)((rnum >> (i * 4)) & 0xF);
				seeds[i] *= seeds[i];
			}


			ReadOnlySpan<int> sh =
			[
				(seed & 2) != 0 ? 4 : 5,
				block.partition_count == 3 ? 6 : 5
			];

			if ((seed & 1) != 0)
			{
				for (int i = 0; i < 8; i++)
				{
					seeds[i] >>= sh[i % 2];
				}
			}
			else
			{
				for (int i = 0; i < 8; i++)
				{
					seeds[i] >>= sh[1 - i % 2];
				}
			}

			if (small_block)
			{
				for (int t = 0, i = 0; t < block.bh; t++)
				{
					for (int s = 0; s < block.bw; s++, i++)
					{
						int x = s << 1;
						int y = t << 1;
						int a = (int)((seeds[0] * x + seeds[1] * y + (rnum >> 14)) & 0x3f);
						int b = (int)((seeds[2] * x + seeds[3] * y + (rnum >> 10)) & 0x3f);
						int c = (int)(block.partition_count < 3 ? 0 : (seeds[4] * x + seeds[5] * y + (rnum >> 6)) & 0x3f);
						int d = (int)(block.partition_count < 4 ? 0 : (seeds[6] * x + seeds[7] * y + (rnum >> 2)) & 0x3f);
						block.partition[i] = (a >= b && a >= c && a >= d) ? 0 : (b >= c && b >= d) ? 1 : (c >= d) ? 2 : 3;
					}
				}
			}
			else
			{
				for (int y = 0, i = 0; y < block.bh; y++)
				{
					for (int x = 0; x < block.bw; x++, i++)
					{
						int a = (int)((seeds[0] * x + seeds[1] * y + (rnum >> 14)) & 0x3f);
						int b = (int)((seeds[2] * x + seeds[3] * y + (rnum >> 10)) & 0x3f);
						int c = (int)(block.partition_count < 3 ? 0 : (seeds[4] * x + seeds[5] * y + (rnum >> 6)) & 0x3f);
						int d = (int)(block.partition_count < 4 ? 0 : (seeds[6] * x + seeds[7] * y + (rnum >> 2)) & 0x3f);
						block.partition[i] = (a >= b && a >= c && a >= d) ? 0 : (b >= c && b >= d) ? 1 : (c >= d) ? 2 : 3;
					}
				}
			}
		}

		private static void ApplyColor<TOutputColor, TOutputChannelValue>(in BlockData block, Span<TOutputColor> output)
			where TOutputChannelValue : unmanaged
			where TOutputColor : unmanaged, IColor<TOutputChannelValue>
		{
			Span<int> planeOffsets = [0, 0, 0, 0];
			if (block.dual_plane != 0)
			{
				planeOffsets[block.plane_selector] = 1;
			}
			for (int i = 0; i < block.bw * block.bh; i++)
			{
				int partition = block.partition_count > 1 ? block.partition[i] : 0;
				int mode = block.cem[partition];
				ReadOnlySpan<int> endpoint = block.endpoints[partition];
				int weightIndex = i * 2;
				TOutputChannelValue r = SelectColor(endpoint[0], endpoint[4], block.weights[weightIndex + planeOffsets[0]], mode, false);
				TOutputChannelValue g = SelectColor(endpoint[1], endpoint[5], block.weights[weightIndex + planeOffsets[1]], mode, false);
				TOutputChannelValue b = SelectColor(endpoint[2], endpoint[6], block.weights[weightIndex + planeOffsets[2]], mode, false);
				TOutputChannelValue a = SelectColor(endpoint[3], endpoint[7], block.weights[weightIndex + planeOffsets[3]], mode, true);
				output[i].SetChannels(r, g, b, a);
			}

			static TOutputChannelValue SelectColor(int v0, int v1, int weight, int mode, bool alpha)
			{
				Debug.Assert(weight is >= 0 and <= 64);
				if (mode is 2 or 3 or 7 or 11 or 15 || (mode == 14 && !alpha))
				{
					Debug.Assert(v0 is >= 0 and <= HdrEndpointMax);
					Debug.Assert(v1 is >= 0 and <= HdrEndpointMax);
					int interpolated = ((v0 << 4) * (64 - weight) + (v1 << 4) * weight + 32) >> 6;

					// Convert the interpolated logarithmic integer to IEEE binary16 (C.2.19).
					int mantissa = interpolated & 0x7FF;
					int adjusted = mantissa < 512 ? 3 * mantissa : mantissa < 1536 ? 4 * mantissa - 512 : 5 * mantissa - 2048;
					int halfBits = ((interpolated & 0xF800) >> 1) | (adjusted >> 3);
					Half value = BitConverter.UInt16BitsToHalf((ushort)int.Min(halfBits, 0x7BFF));

					return NumericConversion.Convert<Half, TOutputChannelValue>(value);
				}
				else
				{
					Debug.Assert(v0 is >= byte.MinValue and <= byte.MaxValue);
					Debug.Assert(v1 is >= byte.MinValue and <= byte.MaxValue);
					int v0Weighted = (v0 << 8 | v0) * (64 - weight);
					int v1Weighted = (v1 << 8 | v1) * weight;
					ushort interpolated = (ushort)((v0Weighted + v1Weighted + 32) >> 6);
					return ConvertUnorm16<TOutputChannelValue>(interpolated);
				}
			}
		}

		private static T ConvertUnorm16<T>(ushort value) where T : unmanaged
		{
			return NumericConversion.IsFloatingPoint<T>()
				? NumericConversion.Convert<Half, T>(Unorm16ToHalf(value))
				: NumericConversion.Convert<ushort, T>(value);
		}

		private static Half Unorm16ToHalf(ushort value)
		{
			// UNORM 16 to floating-point conversion is calculated exactly like this
			// because any deviation would violate the official ASTC specification.
			float normalized = value == ushort.MaxValue ? 1f : value / 65536f;
			Half rounded = (Half)normalized;
			return (float)rounded > normalized ? Half.BitDecrement(rounded) : rounded;
		}

		private static void DecodeIntseq(ReadOnlySpan<byte> input, int offset, int a, int b, int count, bool reverse, Span<IntSeqData> _out)
		{
			if (count <= 0)
			{
				return;
			}

			int n = 0;

			if (a == 3)
			{
				int mask = (1 << b) - 1;
				int block_count = (count + 4) / 5;
				int last_block_count = (count + 4) % 5 + 1;
				int block_size = 8 + 5 * b;
				int last_block_size = (block_size * last_block_count + 4) / 5;

				if (reverse)
				{
					for (int i = 0, p = offset; i < block_count; i++, p -= block_size)
					{
						int now_size = (i < block_count - 1) ? block_size : last_block_size;
						ulong d = BitReverseU64(GetBits64(input, p - now_size, now_size), now_size);
						int x = (int)((d >> b & 3) | (d >> b * 2 & 0xc) | (d >> b * 3 & 0x10) | (d >> b * 4 & 0x60) | (d >> b * 5 & 0x80));
						for (int j = 0; j < 5 && n < count; j++, n++)
						{
							_out[n] = new IntSeqData()
							{
								bits = (int)(d >> (DImt[j] + b * j)) & mask,
								nonbits = DITritsTable[j * 256 + x],
							};
						}
					}
				}
				else
				{
					for (int i = 0, p = offset; i < block_count; i++, p += block_size)
					{
						ulong d = GetBits64(input, p, (i < block_count - 1) ? block_size : last_block_size);
						int x = (int)((d >> b & 3) | (d >> b * 2 & 0xc) | (d >> b * 3 & 0x10) | (d >> b * 4 & 0x60) | (d >> b * 5 & 0x80));
						for (int j = 0; j < 5 && n < count; j++, n++)
						{
							_out[n] = new IntSeqData()
							{
								bits = unchecked((int)(d >> (DImt[j] + b * j))) & mask,
								nonbits = DITritsTable[j * 256 + x],
							};
						}
					}
				}
			}
			else if (a == 5)
			{
				int mask = (1 << b) - 1;
				int block_count = (count + 2) / 3;
				int last_block_count = (count + 2) % 3 + 1;
				int block_size = 7 + 3 * b;
				int last_block_size = (block_size * last_block_count + 2) / 3;

				if (reverse)
				{
					for (int i = 0, p = offset; i < block_count; i++, p -= block_size)
					{
						int now_size = (i < block_count - 1) ? block_size : last_block_size;
						ulong d = BitReverseU64(GetBits64(input, p - now_size, now_size), now_size);
						int x = (int)((d >> b & 7) | (d >> b * 2 & 0x18) | (d >> b * 3 & 0x60));
						for (int j = 0; j < 3 && n < count; j++, n++)
						{
							_out[n] = new IntSeqData()
							{
								bits = (int)d >> (DImq[j] + b * j) & mask,
								nonbits = DIQuintsTable[j * 128 + x],
							};
						}
					}
				}
				else
				{
					for (int i = 0, p = offset; i < block_count; i++, p += block_size)
					{
						ulong d = GetBits64(input, p, (i < block_count - 1) ? block_size : last_block_size);
						int x = (int)((d >> b & 7) | (d >> b * 2 & 0x18) | (d >> b * 3 & 0x60));
						for (int j = 0; j < 3 && n < count; j++, n++)
						{
							_out[n] = new IntSeqData()
							{
								bits = (int)d >> (DImq[j] + b * j) & mask,
								nonbits = DIQuintsTable[j * 128 + x],
							};
						}
					}
				}
			}
			else
			{
				if (reverse)
				{
					for (int p = offset - b; n < count; n++, p -= b)
					{
						_out[n] = new IntSeqData()
						{
							bits = BitReverseU8((byte)GetBits(input, p, b), b),
							nonbits = 0,
						};
					}
				}
				else
				{
					for (int p = offset; n < count; n++, p += b)
					{
						_out[n] = new IntSeqData()
						{
							bits = GetBits(input, p, b),
							nonbits = 0,
						};
					}
				}
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
		private static byte BitReverseU8(byte c, int bits)
		{
			return (byte)(BitReverseTable[c] >> (8 - bits));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
		private static ulong BitReverseU64(ulong d, int bits)
		{
			ulong ret;
			unchecked
			{
				ret =
					(ulong)BitReverseTable[(int)(d >> 0 & 0xff)] << 56 |
					(ulong)BitReverseTable[(int)(d >> 8 & 0xff)] << 48 |
					(ulong)BitReverseTable[(int)(d >> 16 & 0xff)] << 40 |
					(ulong)BitReverseTable[(int)(d >> 24 & 0xff)] << 32 |
					(ulong)BitReverseTable[(int)(d >> 32 & 0xff)] << 24 |
					(ulong)BitReverseTable[(int)(d >> 40 & 0xff)] << 16 |
					(ulong)BitReverseTable[(int)(d >> 48 & 0xff)] << 8 |
					(ulong)BitReverseTable[(int)(d >> 56 & 0xff)];
			}
			return ret >> (64 - bits);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
		private static int GetBits(ReadOnlySpan<byte> input, int bit, int len)
		{
			Debug.Assert(len <= 24);
			if (!HasEnoughBits(input, bit, len))
			{
				// Invalid input, but we should return something instead of throwing an exception
				return default;
			}
			ReadOnlySpan<byte> slice = input[(bit / 8)..];
			uint bitBuffer;
			if (slice.Length >= sizeof(uint))
			{
				bitBuffer = BinaryPrimitives.ReadUInt32LittleEndian(slice);
			}
			else
			{
				Span<byte> temp = [0, 0, 0, 0];
				slice.CopyTo(temp);
				bitBuffer = BinaryPrimitives.ReadUInt32LittleEndian(temp);
			}
			uint mask = uint.MaxValue >> (32 - len);
			uint result = (bitBuffer >> (bit % 8)) & mask;
			return unchecked((int)result);

			static bool HasEnoughBits(ReadOnlySpan<byte> input, int bit, int len)
			{
				if (bit < 0 || len <= 0)
				{
					return false;
				}
				int bytesNeeded = (bit / 8) + ((bit % 8 + len + 7) / 8);
				return input.Length >= bytesNeeded;
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
		private static ulong GetBits64(ReadOnlySpan<byte> input, int bit, int len)
		{
			ulong mask = len == 64 ? 0xffffffffffffffff : (1UL << len) - 1;
			if (len < 1)
			{
				return 0;
			}
			else if (bit >= 64)
			{
				return BinaryPrimitives.ReadUInt64LittleEndian(input[sizeof(ulong)..]) >> (bit - 64) & mask;
			}
			else if (bit <= 0)
			{
				return BinaryPrimitives.ReadUInt64LittleEndian(input) << -bit & mask;
			}
			else if (bit + len <= 64)
			{
				return BinaryPrimitives.ReadUInt64LittleEndian(input) >> bit & mask;
			}
			else
			{
				return (BinaryPrimitives.ReadUInt64LittleEndian(input) >> bit | BinaryPrimitives.ReadUInt64LittleEndian(input[sizeof(ulong)..]) << (64 - bit)) & mask;
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
		private static byte Clamp(int n)
		{
			return n < 0 ? byte.MinValue : n > 255 ? byte.MaxValue : (byte)n;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
		private static void BitTransferSigned(ref int a, ref int b)
		{
			b = (b >> 1) | (a & 0x80);
			a = (a >> 1) & 0x3f;
			if ((a & 0x20) != 0)
			{
				a -= 0x40;
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
		private static void SetEndpoint(Span<int> endpoint, int r1, int g1, int b1, int a1, int r2, int g2, int b2, int a2)
		{
			endpoint[0] = r1;
			endpoint[1] = g1;
			endpoint[2] = b1;
			endpoint[3] = a1;
			endpoint[4] = r2;
			endpoint[5] = g2;
			endpoint[6] = b2;
			endpoint[7] = a2;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
		private static void SetEndpointClamp(Span<int> endpoint, int r1, int g1, int b1, int a1, int r2, int g2, int b2, int a2)
		{
			endpoint[0] = Clamp(r1);
			endpoint[1] = Clamp(g1);
			endpoint[2] = Clamp(b1);
			endpoint[3] = Clamp(a1);
			endpoint[4] = Clamp(r2);
			endpoint[5] = Clamp(g2);
			endpoint[6] = Clamp(b2);
			endpoint[7] = Clamp(a2);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
		private static void SetEndpointBlue(Span<int> endpoint, int r1, int g1, int b1, int a1, int r2, int g2, int b2, int a2)
		{
			endpoint[0] = (r1 + b1) >> 1;
			endpoint[1] = (g1 + b1) >> 1;
			endpoint[2] = b1;
			endpoint[3] = a1;
			endpoint[4] = (r2 + b2) >> 1;
			endpoint[5] = (g2 + b2) >> 1;
			endpoint[6] = b2;
			endpoint[7] = a2;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
		private static void SetEndpointBlueClamp(Span<int> endpoint, int r1, int g1, int b1, int a1, int r2, int g2, int b2, int a2)
		{
			endpoint[0] = Clamp((r1 + b1) >> 1);
			endpoint[1] = Clamp((g1 + b1) >> 1);
			endpoint[2] = Clamp(b1);
			endpoint[3] = Clamp(a1);
			endpoint[4] = Clamp((r2 + b2) >> 1);
			endpoint[5] = Clamp((g2 + b2) >> 1);
			endpoint[6] = Clamp(b2);
			endpoint[7] = Clamp(a2);
		}

		private static ReadOnlySpan<byte> BitReverseTable =>
		[
			0x00, 0x80, 0x40, 0xC0, 0x20, 0xA0, 0x60, 0xE0, 0x10, 0x90, 0x50, 0xD0, 0x30, 0xB0, 0x70, 0xF0,
			0x08, 0x88, 0x48, 0xC8, 0x28, 0xA8, 0x68, 0xE8, 0x18, 0x98, 0x58, 0xD8, 0x38, 0xB8, 0x78, 0xF8,
			0x04, 0x84, 0x44, 0xC4, 0x24, 0xA4, 0x64, 0xE4, 0x14, 0x94, 0x54, 0xD4, 0x34, 0xB4, 0x74, 0xF4,
			0x0C, 0x8C, 0x4C, 0xCC, 0x2C, 0xAC, 0x6C, 0xEC, 0x1C, 0x9C, 0x5C, 0xDC, 0x3C, 0xBC, 0x7C, 0xFC,
			0x02, 0x82, 0x42, 0xC2, 0x22, 0xA2, 0x62, 0xE2, 0x12, 0x92, 0x52, 0xD2, 0x32, 0xB2, 0x72, 0xF2,
			0x0A, 0x8A, 0x4A, 0xCA, 0x2A, 0xAA, 0x6A, 0xEA, 0x1A, 0x9A, 0x5A, 0xDA, 0x3A, 0xBA, 0x7A, 0xFA,
			0x06, 0x86, 0x46, 0xC6, 0x26, 0xA6, 0x66, 0xE6, 0x16, 0x96, 0x56, 0xD6, 0x36, 0xB6, 0x76, 0xF6,
			0x0E, 0x8E, 0x4E, 0xCE, 0x2E, 0xAE, 0x6E, 0xEE, 0x1E, 0x9E, 0x5E, 0xDE, 0x3E, 0xBE, 0x7E, 0xFE,
			0x01, 0x81, 0x41, 0xC1, 0x21, 0xA1, 0x61, 0xE1, 0x11, 0x91, 0x51, 0xD1, 0x31, 0xB1, 0x71, 0xF1,
			0x09, 0x89, 0x49, 0xC9, 0x29, 0xA9, 0x69, 0xE9, 0x19, 0x99, 0x59, 0xD9, 0x39, 0xB9, 0x79, 0xF9,
			0x05, 0x85, 0x45, 0xC5, 0x25, 0xA5, 0x65, 0xE5, 0x15, 0x95, 0x55, 0xD5, 0x35, 0xB5, 0x75, 0xF5,
			0x0D, 0x8D, 0x4D, 0xCD, 0x2D, 0xAD, 0x6D, 0xED, 0x1D, 0x9D, 0x5D, 0xDD, 0x3D, 0xBD, 0x7D, 0xFD,
			0x03, 0x83, 0x43, 0xC3, 0x23, 0xA3, 0x63, 0xE3, 0x13, 0x93, 0x53, 0xD3, 0x33, 0xB3, 0x73, 0xF3,
			0x0B, 0x8B, 0x4B, 0xCB, 0x2B, 0xAB, 0x6B, 0xEB, 0x1B, 0x9B, 0x5B, 0xDB, 0x3B, 0xBB, 0x7B, 0xFB,
			0x07, 0x87, 0x47, 0xC7, 0x27, 0xA7, 0x67, 0xE7, 0x17, 0x97, 0x57, 0xD7, 0x37, 0xB7, 0x77, 0xF7,
			0x0F, 0x8F, 0x4F, 0xCF, 0x2F, 0xAF, 0x6F, 0xEF, 0x1F, 0x9F, 0x5F, 0xDF, 0x3F, 0xBF, 0x7F, 0xFF
		];

		private static ReadOnlySpan<int> WeightPrecTableA => [0, 0, 0, 3, 0, 5, 3, 0, 0, 0, 5, 3, 0, 5, 3, 0];
		private static ReadOnlySpan<int> WeightPrecTableB => [0, 0, 1, 0, 2, 0, 1, 3, 0, 0, 1, 2, 4, 2, 3, 5];

		private static ReadOnlySpan<int> CemTableA => [0, 3, 5, 0, 3, 5, 0, 3, 5, 0, 3, 5, 0, 3, 5, 0, 3, 0, 0];
		private static ReadOnlySpan<int> CemTableB => [8, 6, 5, 7, 5, 4, 6, 4, 3, 5, 3, 2, 4, 2, 1, 3, 1, 2, 1];

		private static ReadOnlySpan<int> DImt => [0, 2, 4, 5, 7];
		private static ReadOnlySpan<int> DImq => [0, 3, 5];
		private static ReadOnlySpan<int> DITritsTable =>
		[
			0, 1, 2, 0, 0, 1, 2, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 0, 0, 1, 2, 1, 0, 1, 2, 2, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 0, 0, 1, 2, 1, 0, 1, 2, 2, 0, 1, 2, 1, 0, 1, 2, 0, 0, 1, 2, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 0, 0, 1, 2, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 0, 0, 1, 2, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 0, 0, 1, 2, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 0, 0, 1, 2, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 0, 0, 1, 2, 1, 0, 1, 2, 2, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 0, 0, 1, 2, 1, 0, 1, 2, 2, 0, 1, 2, 1, 0, 1, 2, 0, 0, 1, 2, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 0, 0, 1, 2, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 0, 0, 1, 2, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 0, 0, 1, 2, 1, 0, 1, 2, 2, 0, 1, 2, 2,
			0, 0, 0, 0, 1, 1, 1, 0, 2, 2, 2, 0, 2, 2, 2, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 0, 2, 2, 2, 0, 2, 2, 2, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 0, 0, 0, 0, 0, 1, 1, 1, 0, 2, 2, 2, 0, 2, 2, 2, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 1, 2, 2, 2, 0, 0, 0, 0, 0, 1, 1, 1, 0, 2, 2, 2, 0, 2, 2, 2, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 1, 2, 2, 2, 0, 0, 0, 0, 0, 1, 1, 1, 0, 2, 2, 2, 0, 2, 2, 2, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 1, 0, 0, 0, 1, 0, 0, 0, 0, 1, 1, 1, 0, 2, 2, 2, 0, 2, 2, 2, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 0, 2, 2, 2, 0, 2, 2, 2, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 1, 2, 2, 2, 1, 0, 0, 0, 0, 1, 1, 1, 0, 2, 2, 2, 0, 2, 2, 2, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 1, 2, 2, 2, 1,
			0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 2, 2, 2, 2, 2, 1, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1, 2, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 2, 2, 2, 2, 2, 1, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1, 2, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 2, 2, 2, 2, 2, 1, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1, 2, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 2, 2, 2, 2, 2, 1, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1, 2, 2, 2, 2, 2, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 2, 2, 2, 2, 2, 1, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1, 2, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 2, 2, 2, 2, 2, 1, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1, 2, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 2, 2, 2, 2, 2, 1, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1, 2, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 2, 2, 2, 2, 2, 1, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1, 2, 2, 2, 2, 2,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2
		];
		private static ReadOnlySpan<int> DIQuintsTable =>
		[
			0, 1, 2, 3, 4, 0, 4, 4, 0, 1, 2, 3, 4, 1, 4, 4, 0, 1, 2, 3, 4, 2, 4, 4, 0, 1, 2, 3, 4, 3, 4, 4, 0, 1, 2, 3, 4, 0, 4, 0, 0, 1, 2, 3, 4, 1, 4, 1, 0, 1, 2, 3, 4, 2, 4, 2, 0, 1, 2, 3, 4, 3, 4, 3, 0, 1, 2, 3, 4, 0, 2, 3, 0, 1, 2, 3, 4, 1, 2, 3, 0, 1, 2, 3, 4, 2, 2, 3, 0, 1, 2, 3, 4, 3, 2, 3, 0, 1, 2, 3, 4, 0, 0, 1, 0, 1, 2, 3, 4, 1, 0, 1, 0, 1, 2, 3, 4, 2, 0, 1, 0, 1, 2, 3, 4, 3, 0, 1,
			0, 0, 0, 0, 0, 4, 4, 4, 1, 1, 1, 1, 1, 4, 4, 4, 2, 2, 2, 2, 2, 4, 4, 4, 3, 3, 3, 3, 3, 4, 4, 4, 0, 0, 0, 0, 0, 4, 0, 4, 1, 1, 1, 1, 1, 4, 1, 4, 2, 2, 2, 2, 2, 4, 2, 4, 3, 3, 3, 3, 3, 4, 3, 4, 0, 0, 0, 0, 0, 4, 0, 0, 1, 1, 1, 1, 1, 4, 1, 1, 2, 2, 2, 2, 2, 4, 2, 2, 3, 3, 3, 3, 3, 4, 3, 3, 0, 0, 0, 0, 0, 4, 0, 0, 1, 1, 1, 1, 1, 4, 1, 1, 2, 2, 2, 2, 2, 4, 2, 2, 3, 3, 3, 3, 3, 4, 3, 3,
			0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 1, 4, 0, 0, 0, 0, 0, 0, 2, 4, 0, 0, 0, 0, 0, 0, 3, 4, 1, 1, 1, 1, 1, 1, 4, 4, 1, 1, 1, 1, 1, 1, 4, 4, 1, 1, 1, 1, 1, 1, 4, 4, 1, 1, 1, 1, 1, 1, 4, 4, 2, 2, 2, 2, 2, 2, 4, 4, 2, 2, 2, 2, 2, 2, 4, 4, 2, 2, 2, 2, 2, 2, 4, 4, 2, 2, 2, 2, 2, 2, 4, 4, 3, 3, 3, 3, 3, 3, 4, 4, 3, 3, 3, 3, 3, 3, 4, 4, 3, 3, 3, 3, 3, 3, 4, 4, 3, 3, 3, 3, 3, 3, 4, 4,
		];

		private static ReadOnlySpan<int> DETritsTable => [0, 204, 93, 44, 22, 11, 5];
		private static ReadOnlySpan<int> DEQuintsTable => [0, 113, 54, 26, 13, 6];
	}
}
