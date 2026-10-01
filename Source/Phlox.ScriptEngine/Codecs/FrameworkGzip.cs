/*
 * gzip as Halcyon's iwStringCodec "gzip" got it from .NET Framework 4.x System.IO.Compression.GZipStream
 * (Halcyon targets .NET Framework 4.7.1). .NET 10's GZipStream differs in three ways a script can see:
 *  - compression: Framework writes the fixed header 1f 8b 08 00 00000000 04 00 and a stock-zlib level 6 body;
 *    .NET 10 writes 00 0a in the last two header bytes and a zlib-ng body (ZlibDeflate reproduces zlib);
 *    an empty input gives no bytes at all;
 *  - decompression reads the first member only (.NET 10 reads every member);
 *  - Framework decompresses with its own managed inflater, read through an 8192-byte input buffer and
 *    Stream.CopyTo's 81920-byte reads. A stream that ends early gives what that inflater had handed out
 *    (so a short truncated stream gives nothing), a distance before the start of the output reads zeros
 *    instead of failing, and bad data fails with the Framework's own messages.
 * The inflater below follows the Framework's (Inflater, InputBuffer, OutputWindow, HuffmanTree,
 * GZipDecoder) and is checked against Framework 4.8.1 on a 6431-stream fuzz corpus of truncations,
 * bit flips and random blocks (IwStringCodecTests.GzipDecodeMatchesFrameworkOnTheFuzzCorpus).
 */

using System;
using System.IO;

namespace Phlox.ScriptEngine.Codecs
{
    internal static class FrameworkGzip
    {
        private static readonly byte[] Header = { 0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00 };

        public static byte[] Compress(byte[] data)
        {
            if (data.Length == 0) return new byte[0];
            byte[] body = ZlibDeflate.Compress(data);
            byte[] result = new byte[Header.Length + body.Length + 8];
            Buffer.BlockCopy(Header, 0, result, 0, Header.Length);
            Buffer.BlockCopy(body, 0, result, Header.Length, body.Length);
            uint crc = Crc32.Compute(data, 0, data.Length);
            int p = Header.Length + body.Length;
            WriteUInt32(result, p, crc);
            WriteUInt32(result, p + 4, (uint)data.Length);
            return result;
        }

        private static void WriteUInt32(byte[] b, int p, uint v)
        {
            b[p] = (byte)v; b[p + 1] = (byte)(v >> 8); b[p + 2] = (byte)(v >> 16); b[p + 3] = (byte)(v >> 24);
        }

        /// <summary>GZipStream(Decompress).CopyTo(MemoryStream) as .NET Framework 4.x ran it.</summary>
        public static byte[] Decompress(byte[] data)
        {
            Inflater inflater = new Inflater();
            MemoryStream mso = new MemoryStream();
            byte[] copyBuffer = new byte[81920];            // Stream.CopyTo's buffer
            byte[] inputBuffer = new byte[8192];            // DeflateStream.DefaultBufferSize in Framework 4.x
            int inputPos = 0;
            for (;;)
            {
                // DeflateStream.Read(copyBuffer, 0, 81920)
                int offset = 0;
                int remaining = copyBuffer.Length;
                for (;;)
                {
                    int n = inflater.Inflate(copyBuffer, offset, remaining);
                    offset += n;
                    remaining -= n;
                    if (remaining == 0) break;
                    if (inflater.Finished()) break;
                    int bytes = Math.Min(inputBuffer.Length, data.Length - inputPos);
                    if (bytes <= 0) break;
                    Buffer.BlockCopy(data, inputPos, inputBuffer, 0, bytes);
                    inputPos += bytes;
                    inflater.SetInput(inputBuffer, 0, bytes);
                }
                int read = copyBuffer.Length - remaining;
                if (read == 0) break;
                mso.Write(copyBuffer, 0, read);
            }
            return mso.ToArray();
        }

        internal static class Crc32
        {
            private static readonly uint[] Table = MakeTable();

            private static uint[] MakeTable()
            {
                uint[] t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                return t;
            }

            public static uint Update(uint crc, byte[] b, int offset, int count)
            {
                crc = ~crc;
                for (int i = 0; i < count; i++) crc = Table[(crc ^ b[offset + i]) & 0xff] ^ (crc >> 8);
                return ~crc;
            }

            public static uint Compute(byte[] b, int offset, int count) { return Update(0, b, offset, count); }
        }

        private static class Messages
        {
            public const string CorruptedGZipHeader = "The magic number in GZip header is not correct. Make sure you are passing in a GZip stream.";
            public const string UnknownCompressionMode = "The compression mode specified in GZip header is unknown.";
            public const string UnknownBlockType = "Unknown block type. Stream might be corrupted.";
            public const string InvalidBlockLength = "Block length does not match with its complement.";
            public const string InvalidHuffmanData = "Failed to construct a huffman tree using the length array. The stream might be corrupted.";
            public const string GenericInvalidData = "Found invalid data while decoding.";
            public const string InvalidCRC = "The CRC in GZip footer does not match the CRC calculated from the decompressed data.";
            public const string InvalidStreamSize = "The stream size in GZip footer does not match the real stream size.";
            public const string UnknownState = "Decoder is in some unknown state. This might be caused by corrupted data.";
        }

        private sealed class InputBuffer
        {
            private byte[] buffer;
            private int start;
            private int end;
            private uint bitBuffer;
            private int bitsInBuffer;

            public int AvailableBits { get { return bitsInBuffer; } }
            public int AvailableBytes { get { return (end - start) + (bitsInBuffer / 8); } }

            public bool EnsureBitsAvailable(int count)
            {
                if (bitsInBuffer < count)
                {
                    if (NeedsInput()) return false;
                    bitBuffer |= (uint)buffer[start++] << bitsInBuffer;
                    bitsInBuffer += 8;
                    if (bitsInBuffer < count)
                    {
                        if (NeedsInput()) return false;
                        bitBuffer |= (uint)buffer[start++] << bitsInBuffer;
                        bitsInBuffer += 8;
                    }
                }
                return true;
            }

            public uint TryLoad16Bits()
            {
                if (bitsInBuffer < 8)
                {
                    if (start < end) { bitBuffer |= (uint)buffer[start++] << bitsInBuffer; bitsInBuffer += 8; }
                    if (start < end) { bitBuffer |= (uint)buffer[start++] << bitsInBuffer; bitsInBuffer += 8; }
                }
                else if (bitsInBuffer < 16)
                {
                    if (start < end) { bitBuffer |= (uint)buffer[start++] << bitsInBuffer; bitsInBuffer += 8; }
                }
                return bitBuffer;
            }

            public int GetBits(int count)
            {
                if (!EnsureBitsAvailable(count)) return -1;
                int result = (int)(bitBuffer & (((uint)1 << count) - 1));
                bitBuffer >>= count;
                bitsInBuffer -= count;
                return result;
            }

            public int CopyTo(byte[] output, int offset, int length)
            {
                int bytesFromBitBuffer = 0;
                while (bitsInBuffer > 0 && length > 0)
                {
                    output[offset++] = (byte)bitBuffer;
                    bitBuffer >>= 8;
                    bitsInBuffer -= 8;
                    length--;
                    bytesFromBitBuffer++;
                }
                if (length == 0) return bytesFromBitBuffer;
                int avail = end - start;
                if (length > avail) length = avail;
                Array.Copy(buffer, start, output, offset, length);
                start += length;
                return bytesFromBitBuffer + length;
            }

            public bool NeedsInput() { return start == end; }

            public void SetInput(byte[] buffer, int offset, int length)
            {
                this.buffer = buffer;
                start = offset;
                end = offset + length;
            }

            public void SkipBits(int n)
            {
                bitBuffer >>= n;
                bitsInBuffer -= n;
            }

            public void SkipToByteBoundary()
            {
                bitBuffer >>= (bitsInBuffer % 8);
                bitsInBuffer = bitsInBuffer - (bitsInBuffer % 8);
            }
        }

        private sealed class OutputWindow
        {
            private const int WindowSize = 32768;
            private const int WindowMask = 32767;
            private readonly byte[] window = new byte[WindowSize];
            private int end;
            private int bytesUsed;

            public void Write(byte b)
            {
                window[end++] = b;
                end &= WindowMask;
                ++bytesUsed;
            }

            public void WriteLengthDistance(int length, int distance)
            {
                bytesUsed += length;
                int copyStart = (end - distance) & WindowMask;
                int border = WindowSize - length;
                if (copyStart <= border && end < border)
                {
                    if (length <= distance)
                    {
                        Array.Copy(window, copyStart, window, end, length);
                        end += length;
                    }
                    else
                    {
                        while (length-- > 0) window[end++] = window[copyStart++];
                    }
                }
                else
                {
                    while (length-- > 0)
                    {
                        window[end++] = window[copyStart++];
                        end &= WindowMask;
                        copyStart &= WindowMask;
                    }
                }
            }

            public int CopyFrom(InputBuffer input, int length)
            {
                length = Math.Min(Math.Min(length, WindowSize - bytesUsed), input.AvailableBytes);
                int copied;
                int tailLen = WindowSize - end;
                if (length > tailLen)
                {
                    copied = input.CopyTo(window, end, tailLen);
                    if (copied == tailLen) copied += input.CopyTo(window, 0, length - tailLen);
                }
                else
                {
                    copied = input.CopyTo(window, end, length);
                }
                end = (end + copied) & WindowMask;
                bytesUsed += copied;
                return copied;
            }

            public int FreeBytes { get { return WindowSize - bytesUsed; } }
            public int AvailableBytes { get { return bytesUsed; } }

            public int CopyTo(byte[] output, int offset, int length)
            {
                int copy_end;
                if (length > bytesUsed)
                {
                    copy_end = end;
                    length = bytesUsed;
                }
                else
                {
                    copy_end = (end - bytesUsed + length) & WindowMask;
                }
                int copied = length;
                int tailLen = length - copy_end;
                if (tailLen > 0)
                {
                    Array.Copy(window, WindowSize - tailLen, output, offset, tailLen);
                    offset += tailLen;
                    length = copy_end;
                }
                Array.Copy(window, copy_end - length, output, offset, length);
                bytesUsed -= copied;
                return copied;
            }
        }

        private sealed class HuffmanTree
        {
            public const int MaxLiteralTreeElements = 288;
            public const int MaxDistTreeElements = 32;
            public const int EndOfBlockCode = 256;
            public const int NumberOfCodeLengthTreeElements = 19;

            private readonly int tableBits;
            private short[] table;
            private short[] left;
            private short[] right;
            private readonly byte[] codeLengthArray;
            private readonly int tableMask;

            public static readonly HuffmanTree StaticLiteralLengthTree = new HuffmanTree(GetStaticLiteralTreeLength());
            public static readonly HuffmanTree StaticDistanceTree = new HuffmanTree(GetStaticDistanceTreeLength());

            public HuffmanTree(byte[] codeLengths)
            {
                codeLengthArray = codeLengths;
                tableBits = codeLengthArray.Length == MaxLiteralTreeElements ? 9 : 7;
                tableMask = (1 << tableBits) - 1;
                CreateTable();
            }

            private static byte[] GetStaticLiteralTreeLength()
            {
                byte[] literalTreeLength = new byte[MaxLiteralTreeElements];
                for (int i = 0; i <= 143; i++) literalTreeLength[i] = 8;
                for (int i = 144; i <= 255; i++) literalTreeLength[i] = 9;
                for (int i = 256; i <= 279; i++) literalTreeLength[i] = 7;
                for (int i = 280; i <= 287; i++) literalTreeLength[i] = 8;
                return literalTreeLength;
            }

            private static byte[] GetStaticDistanceTreeLength()
            {
                byte[] staticDistanceTreeLength = new byte[MaxDistTreeElements];
                for (int i = 0; i < MaxDistTreeElements; i++) staticDistanceTreeLength[i] = 5;
                return staticDistanceTreeLength;
            }

            private static uint BitReverse(uint code, int length)
            {
                uint new_code = 0;
                do
                {
                    new_code |= (code & 1);
                    new_code <<= 1;
                    code >>= 1;
                } while (--length > 0);
                return new_code >> 1;
            }

            private uint[] CalculateHuffmanCode()
            {
                uint[] bitLengthCount = new uint[17];
                foreach (int codeLength in codeLengthArray) bitLengthCount[codeLength]++;
                bitLengthCount[0] = 0;
                uint[] nextCode = new uint[17];
                uint tempCode = 0;
                for (int bits = 1; bits <= 16; bits++)
                {
                    tempCode = (tempCode + bitLengthCount[bits - 1]) << 1;
                    nextCode[bits] = tempCode;
                }
                uint[] code = new uint[MaxLiteralTreeElements];
                for (int i = 0; i < codeLengthArray.Length; i++)
                {
                    int len = codeLengthArray[i];
                    if (len > 0)
                    {
                        code[i] = BitReverse(nextCode[len], len);
                        nextCode[len]++;
                    }
                }
                return code;
            }

            private void CreateTable()
            {
                uint[] codeArray = CalculateHuffmanCode();
                table = new short[1 << tableBits];
                left = new short[2 * codeLengthArray.Length];
                right = new short[2 * codeLengthArray.Length];
                short avail = (short)codeLengthArray.Length;

                for (int ch = 0; ch < codeLengthArray.Length; ch++)
                {
                    int len = codeLengthArray[ch];
                    if (len > 0)
                    {
                        int start = (int)codeArray[ch];
                        if (len <= tableBits)
                        {
                            int increment = 1 << len;
                            if (start >= increment) throw new InvalidDataException(Messages.InvalidHuffmanData);
                            int locs = 1 << (tableBits - len);
                            for (int j = 0; j < locs; j++)
                            {
                                table[start] = (short)ch;
                                start += increment;
                            }
                        }
                        else
                        {
                            int overflowBits = len - tableBits;
                            int codeBitMask = 1 << tableBits;
                            int index = start & ((1 << tableBits) - 1);
                            short[] array = table;
                            do
                            {
                                short value = array[index];
                                if (value == 0)
                                {
                                    array[index] = (short)-avail;
                                    value = (short)-avail;
                                    avail++;
                                }
                                if (value > 0) throw new InvalidDataException(Messages.InvalidHuffmanData);
                                array = (start & codeBitMask) == 0 ? left : right;
                                index = -value;
                                codeBitMask <<= 1;
                                overflowBits--;
                            } while (overflowBits != 0);
                            array[index] = (short)ch;
                        }
                    }
                }
            }

            public int GetNextSymbol(InputBuffer input)
            {
                uint bitBuffer = input.TryLoad16Bits();
                if (input.AvailableBits == 0) return -1;
                int symbol = table[bitBuffer & tableMask];
                if (symbol < 0)
                {
                    uint mask = (uint)1 << tableBits;
                    do
                    {
                        symbol = -symbol;
                        symbol = (bitBuffer & mask) == 0 ? left[symbol] : right[symbol];
                        mask <<= 1;
                    } while (symbol < 0);
                }
                int codeLength = codeLengthArray[symbol];
                if (codeLength <= 0) throw new InvalidDataException(Messages.InvalidHuffmanData);
                if (codeLength > input.AvailableBits) return -1;
                input.SkipBits(codeLength);
                return symbol;
            }
        }

        private enum State
        {
            ReadingHeader = 0,
            ReadingBFinal = 2,
            ReadingBType = 3,
            ReadingNumLitCodes = 4,
            ReadingNumDistCodes = 5,
            ReadingNumCodeLengthCodes = 6,
            ReadingCodeLengthCodes = 7,
            ReadingTreeCodesBefore = 8,
            ReadingTreeCodesAfter = 9,
            DecodeTop = 10,
            HaveInitialLength = 11,
            HaveFullLength = 12,
            HaveDistCode = 13,
            UncompressedAligning = 15,
            UncompressedByte1 = 16,
            UncompressedByte2 = 17,
            UncompressedByte3 = 18,
            UncompressedByte4 = 19,
            DecodingUncompressed = 20,
            StartReadingFooter = 21,
            ReadingFooter = 22,
            VerifyingFooter = 23,
            Done = 24,
        }

        private enum HeaderState
        {
            ReadingID1, ReadingID2, ReadingCM, ReadingFLG, ReadingMMTime, ReadingXFL, ReadingOS, ReadingXLen1,
            ReadingXLen2, ReadingXLenData, ReadingFileName, ReadingComment, ReadingCRC16Part1, ReadingCRC16Part2,
            Done, ReadingCRC, ReadingFileSize,
        }

        private sealed class Inflater
        {
            private static readonly byte[] extraLengthBits = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
            private static readonly int[] lengthBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
            private static readonly int[] distanceBasePosition = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577, 0, 0 };
            private static readonly byte[] codeOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };
            private static readonly byte[] staticDistanceTreeTable = {
                0x00, 0x10, 0x08, 0x18, 0x04, 0x14, 0x0c, 0x1c, 0x02, 0x12, 0x0a, 0x1a, 0x06, 0x16, 0x0e, 0x1e,
                0x01, 0x11, 0x09, 0x19, 0x05, 0x15, 0x0d, 0x1d, 0x03, 0x13, 0x0b, 0x1b, 0x07, 0x17, 0x0f, 0x1f,
            };

            private const int BlockUncompressed = 0, BlockStatic = 1, BlockDynamic = 2;

            private readonly OutputWindow output = new OutputWindow();
            private readonly InputBuffer input = new InputBuffer();
            private HuffmanTree literalLengthTree;
            private HuffmanTree distanceTree;
            private State state = State.ReadingHeader;
            private int bfinal;
            private int blockType;
            private readonly byte[] blockLengthBuffer = new byte[4];
            private int blockLength;
            private int length;
            private int distanceCode;
            private int extraBits;
            private int loopCounter;
            private int literalLengthCodeCount;
            private int distanceCodeCount;
            private int codeLengthCodeCount;
            private int codeArraySize;
            private int lengthCode;
            private readonly byte[] codeList = new byte[HuffmanTree.MaxLiteralTreeElements + HuffmanTree.MaxDistTreeElements];
            private readonly byte[] codeLengthTreeCodeLength = new byte[HuffmanTree.NumberOfCodeLengthTreeElements];
            private HuffmanTree codeLengthTree;

            // GZipDecoder
            private HeaderState gzipHeaderSubstate = HeaderState.ReadingID1;
            private HeaderState gzipFooterSubstate = HeaderState.ReadingCRC;
            private int gzip_header_flag;
            private int gzip_header_xlen;
            private uint expectedCrc32;
            private uint expectedOutputStreamSizeModulo;
            private int gzipLoopCounter;
            private uint actualCrc32;
            private long actualStreamSizeModulo;

            public void SetInput(byte[] inputBytes, int offset, int count) { input.SetInput(inputBytes, offset, count); }

            public bool Finished() { return state == State.Done || state == State.VerifyingFooter; }

            public int Inflate(byte[] bytes, int offset, int length)
            {
                int count = 0;
                do
                {
                    int copied = output.CopyTo(bytes, offset, length);
                    if (copied > 0)
                    {
                        actualCrc32 = Crc32.Update(actualCrc32, bytes, offset, copied);
                        actualStreamSizeModulo = (actualStreamSizeModulo + (uint)copied) & 0xffffffffL;
                        offset += copied;
                        count += copied;
                        length -= copied;
                    }
                    if (length == 0) break;
                } while (!Finished() && Decode());

                if (state == State.VerifyingFooter)
                {
                    if (output.AvailableBytes == 0) Validate();
                }
                return count;
            }

            private void Validate()
            {
                if (expectedCrc32 != actualCrc32) throw new InvalidDataException(Messages.InvalidCRC);
                if (actualStreamSizeModulo != expectedOutputStreamSizeModulo) throw new InvalidDataException(Messages.InvalidStreamSize);
            }

            private bool Decode()
            {
                bool eob = false;
                bool result;

                if (Finished()) return true;

                if (state == State.ReadingHeader)
                {
                    if (!ReadHeader()) return false;
                    state = State.ReadingBFinal;
                }
                else if (state == State.StartReadingFooter || state == State.ReadingFooter)
                {
                    if (!ReadFooter()) return false;
                    state = State.VerifyingFooter;
                    return true;
                }

                if (state == State.ReadingBFinal)
                {
                    if (!input.EnsureBitsAvailable(1)) return false;
                    bfinal = input.GetBits(1);
                    state = State.ReadingBType;
                }

                if (state == State.ReadingBType)
                {
                    if (!input.EnsureBitsAvailable(2))
                    {
                        state = State.ReadingBType;
                        return false;
                    }
                    blockType = input.GetBits(2);
                    if (blockType == BlockDynamic)
                    {
                        state = State.ReadingNumLitCodes;
                    }
                    else if (blockType == BlockStatic)
                    {
                        literalLengthTree = HuffmanTree.StaticLiteralLengthTree;
                        distanceTree = HuffmanTree.StaticDistanceTree;
                        state = State.DecodeTop;
                    }
                    else if (blockType == BlockUncompressed)
                    {
                        state = State.UncompressedAligning;
                    }
                    else
                    {
                        throw new InvalidDataException(Messages.UnknownBlockType);
                    }
                }

                if (blockType == BlockDynamic)
                {
                    if (state < State.DecodeTop) result = DecodeDynamicBlockHeader();
                    else result = DecodeBlock(out eob);
                }
                else if (blockType == BlockStatic)
                {
                    result = DecodeBlock(out eob);
                }
                else if (blockType == BlockUncompressed)
                {
                    result = DecodeUncompressedBlock(out eob);
                }
                else
                {
                    throw new InvalidDataException(Messages.UnknownBlockType);
                }

                if (eob && bfinal != 0) state = State.StartReadingFooter;
                return result;
            }

            private bool DecodeUncompressedBlock(out bool end_of_block)
            {
                end_of_block = false;
                while (true)
                {
                    switch (state)
                    {
                        case State.UncompressedAligning:
                            input.SkipToByteBoundary();
                            state = State.UncompressedByte1;
                            goto case State.UncompressedByte1;
                        case State.UncompressedByte1:
                        case State.UncompressedByte2:
                        case State.UncompressedByte3:
                        case State.UncompressedByte4:
                            int bits = input.GetBits(8);
                            if (bits < 0) return false;
                            blockLengthBuffer[state - State.UncompressedByte1] = (byte)bits;
                            if (state == State.UncompressedByte4)
                            {
                                blockLength = blockLengthBuffer[0] + blockLengthBuffer[1] * 256;
                                int blockLengthComplement = blockLengthBuffer[2] + blockLengthBuffer[3] * 256;
                                if ((ushort)blockLength != (ushort)(~blockLengthComplement))
                                    throw new InvalidDataException(Messages.InvalidBlockLength);
                            }
                            state += 1;
                            break;
                        case State.DecodingUncompressed:
                            int bytesCopied = output.CopyFrom(input, blockLength);
                            blockLength -= bytesCopied;
                            if (blockLength == 0)
                            {
                                state = State.ReadingBFinal;
                                end_of_block = true;
                                return true;
                            }
                            if (output.FreeBytes == 0) return true;
                            return false;
                        default:
                            throw new InvalidDataException(Messages.UnknownState);
                    }
                }
            }

            private bool DecodeBlock(out bool end_of_block_code_seen)
            {
                end_of_block_code_seen = false;
                int freeBytes = output.FreeBytes;
                while (freeBytes > 258)
                {
                    int symbol;
                    switch (state)
                    {
                        case State.DecodeTop:
                            symbol = literalLengthTree.GetNextSymbol(input);
                            if (symbol < 0) return false;
                            if (symbol < 256)
                            {
                                output.Write((byte)symbol);
                                --freeBytes;
                            }
                            else if (symbol == 256)
                            {
                                end_of_block_code_seen = true;
                                state = State.ReadingBFinal;
                                return true;
                            }
                            else
                            {
                                symbol -= 257;
                                if (symbol < 8)
                                {
                                    symbol += 3;
                                    extraBits = 0;
                                }
                                else if (symbol == 28)
                                {
                                    symbol = 258;
                                    extraBits = 0;
                                }
                                else
                                {
                                    if (symbol < 0 || symbol >= extraLengthBits.Length)
                                        throw new InvalidDataException(Messages.GenericInvalidData);
                                    extraBits = extraLengthBits[symbol];
                                }
                                length = symbol;
                                goto case State.HaveInitialLength;
                            }
                            break;
                        case State.HaveInitialLength:
                            if (extraBits > 0)
                            {
                                state = State.HaveInitialLength;
                                int bits = input.GetBits(extraBits);
                                if (bits < 0) return false;
                                if (length < 0 || length >= lengthBase.Length)
                                    throw new InvalidDataException(Messages.GenericInvalidData);
                                length = lengthBase[length] + bits;
                            }
                            state = State.HaveFullLength;
                            goto case State.HaveFullLength;
                        case State.HaveFullLength:
                            if (blockType == BlockDynamic)
                            {
                                distanceCode = distanceTree.GetNextSymbol(input);
                            }
                            else
                            {
                                distanceCode = input.GetBits(5);
                                if (distanceCode >= 0) distanceCode = staticDistanceTreeTable[distanceCode];
                            }
                            if (distanceCode < 0) return false;
                            state = State.HaveDistCode;
                            goto case State.HaveDistCode;
                        case State.HaveDistCode:
                            int offset;
                            if (distanceCode > 3)
                            {
                                extraBits = (distanceCode - 2) >> 1;
                                int bits = input.GetBits(extraBits);
                                if (bits < 0) return false;
                                offset = distanceBasePosition[distanceCode] + bits;
                            }
                            else
                            {
                                offset = distanceCode + 1;
                            }
                            output.WriteLengthDistance(length, offset);
                            freeBytes -= length;
                            state = State.DecodeTop;
                            break;
                        default:
                            throw new InvalidDataException(Messages.UnknownState);
                    }
                }
                return true;
            }

            private bool DecodeDynamicBlockHeader()
            {
                switch (state)
                {
                    case State.ReadingNumLitCodes:
                        literalLengthCodeCount = input.GetBits(5);
                        if (literalLengthCodeCount < 0) return false;
                        literalLengthCodeCount += 257;
                        state = State.ReadingNumDistCodes;
                        goto case State.ReadingNumDistCodes;
                    case State.ReadingNumDistCodes:
                        distanceCodeCount = input.GetBits(5);
                        if (distanceCodeCount < 0) return false;
                        distanceCodeCount += 1;
                        state = State.ReadingNumCodeLengthCodes;
                        goto case State.ReadingNumCodeLengthCodes;
                    case State.ReadingNumCodeLengthCodes:
                        codeLengthCodeCount = input.GetBits(4);
                        if (codeLengthCodeCount < 0) return false;
                        codeLengthCodeCount += 4;
                        loopCounter = 0;
                        state = State.ReadingCodeLengthCodes;
                        goto case State.ReadingCodeLengthCodes;
                    case State.ReadingCodeLengthCodes:
                        while (loopCounter < codeLengthCodeCount)
                        {
                            int bits = input.GetBits(3);
                            if (bits < 0) return false;
                            codeLengthTreeCodeLength[codeOrder[loopCounter]] = (byte)bits;
                            ++loopCounter;
                        }
                        for (int i = codeLengthCodeCount; i < codeOrder.Length; i++) codeLengthTreeCodeLength[codeOrder[i]] = 0;
                        codeLengthTree = new HuffmanTree(codeLengthTreeCodeLength);
                        codeArraySize = literalLengthCodeCount + distanceCodeCount;
                        loopCounter = 0;
                        state = State.ReadingTreeCodesBefore;
                        goto case State.ReadingTreeCodesBefore;
                    case State.ReadingTreeCodesBefore:
                    case State.ReadingTreeCodesAfter:
                        while (loopCounter < codeArraySize)
                        {
                            if (state == State.ReadingTreeCodesBefore)
                            {
                                if ((lengthCode = codeLengthTree.GetNextSymbol(input)) < 0) return false;
                            }
                            if (lengthCode <= 15)
                            {
                                codeList[loopCounter++] = (byte)lengthCode;
                            }
                            else
                            {
                                if (!input.EnsureBitsAvailable(7))
                                {
                                    state = State.ReadingTreeCodesAfter;
                                    return false;
                                }
                                int repeatCount;
                                if (lengthCode == 16)
                                {
                                    if (loopCounter == 0) throw new InvalidDataException();
                                    byte previousCode = codeList[loopCounter - 1];
                                    repeatCount = input.GetBits(2) + 3;
                                    if (loopCounter + repeatCount > codeArraySize) throw new InvalidDataException();
                                    for (int j = 0; j < repeatCount; j++) codeList[loopCounter++] = previousCode;
                                }
                                else if (lengthCode == 17)
                                {
                                    repeatCount = input.GetBits(3) + 3;
                                    if (loopCounter + repeatCount > codeArraySize) throw new InvalidDataException();
                                    for (int j = 0; j < repeatCount; j++) codeList[loopCounter++] = 0;
                                }
                                else
                                {
                                    repeatCount = input.GetBits(7) + 11;
                                    if (loopCounter + repeatCount > codeArraySize) throw new InvalidDataException();
                                    for (int j = 0; j < repeatCount; j++) codeList[loopCounter++] = 0;
                                }
                            }
                            state = State.ReadingTreeCodesBefore;
                        }
                        break;
                    default:
                        throw new InvalidDataException(Messages.UnknownState);
                }

                byte[] literalTreeCodeLength = new byte[HuffmanTree.MaxLiteralTreeElements];
                byte[] distanceTreeCodeLength = new byte[HuffmanTree.MaxDistTreeElements];
                Array.Copy(codeList, literalTreeCodeLength, literalLengthCodeCount);
                Array.Copy(codeList, literalLengthCodeCount, distanceTreeCodeLength, 0, distanceCodeCount);
                // No code for end-of-block: the block could never end.
                if (literalTreeCodeLength[HuffmanTree.EndOfBlockCode] == 0) throw new InvalidDataException();
                literalLengthTree = new HuffmanTree(literalTreeCodeLength);
                distanceTree = new HuffmanTree(distanceTreeCodeLength);
                state = State.DecodeTop;
                return true;
            }

            private bool ReadHeader()
            {
                while (true)
                {
                    int bits;
                    switch (gzipHeaderSubstate)
                    {
                        case HeaderState.ReadingID1:
                            bits = input.GetBits(8);
                            if (bits < 0) return false;
                            if (bits != 0x1F) throw new InvalidDataException(Messages.CorruptedGZipHeader);
                            gzipHeaderSubstate = HeaderState.ReadingID2;
                            goto case HeaderState.ReadingID2;
                        case HeaderState.ReadingID2:
                            bits = input.GetBits(8);
                            if (bits < 0) return false;
                            if (bits != 0x8b) throw new InvalidDataException(Messages.CorruptedGZipHeader);
                            gzipHeaderSubstate = HeaderState.ReadingCM;
                            goto case HeaderState.ReadingCM;
                        case HeaderState.ReadingCM:
                            bits = input.GetBits(8);
                            if (bits < 0) return false;
                            if (bits != 0x8) throw new InvalidDataException(Messages.UnknownCompressionMode);
                            gzipHeaderSubstate = HeaderState.ReadingFLG;
                            goto case HeaderState.ReadingFLG;
                        case HeaderState.ReadingFLG:
                            bits = input.GetBits(8);
                            if (bits < 0) return false;
                            gzip_header_flag = bits;
                            gzipHeaderSubstate = HeaderState.ReadingMMTime;
                            gzipLoopCounter = 0;
                            goto case HeaderState.ReadingMMTime;
                        case HeaderState.ReadingMMTime:
                            bits = 0;
                            while (gzipLoopCounter < 4)
                            {
                                bits = input.GetBits(8);
                                if (bits < 0) return false;
                                gzipLoopCounter++;
                            }
                            gzipHeaderSubstate = HeaderState.ReadingXFL;
                            gzipLoopCounter = 0;
                            goto case HeaderState.ReadingXFL;
                        case HeaderState.ReadingXFL:
                            bits = input.GetBits(8);
                            if (bits < 0) return false;
                            gzipHeaderSubstate = HeaderState.ReadingOS;
                            goto case HeaderState.ReadingOS;
                        case HeaderState.ReadingOS:
                            bits = input.GetBits(8);
                            if (bits < 0) return false;
                            gzipHeaderSubstate = HeaderState.ReadingXLen1;
                            goto case HeaderState.ReadingXLen1;
                        case HeaderState.ReadingXLen1:
                            if ((gzip_header_flag & 0x04) == 0) goto case HeaderState.ReadingFileName;
                            bits = input.GetBits(8);
                            if (bits < 0) return false;
                            gzip_header_xlen = bits;
                            gzipHeaderSubstate = HeaderState.ReadingXLen2;
                            goto case HeaderState.ReadingXLen2;
                        case HeaderState.ReadingXLen2:
                            bits = input.GetBits(8);
                            if (bits < 0) return false;
                            gzip_header_xlen |= bits << 8;
                            gzipHeaderSubstate = HeaderState.ReadingXLenData;
                            gzipLoopCounter = 0;
                            goto case HeaderState.ReadingXLenData;
                        case HeaderState.ReadingXLenData:
                            bits = 0;
                            while (gzipLoopCounter < gzip_header_xlen)
                            {
                                bits = input.GetBits(8);
                                if (bits < 0) return false;
                                gzipLoopCounter++;
                            }
                            gzipHeaderSubstate = HeaderState.ReadingFileName;
                            gzipLoopCounter = 0;
                            goto case HeaderState.ReadingFileName;
                        case HeaderState.ReadingFileName:
                            if ((gzip_header_flag & 0x08) == 0)
                            {
                                gzipHeaderSubstate = HeaderState.ReadingComment;
                                goto case HeaderState.ReadingComment;
                            }
                            do
                            {
                                bits = input.GetBits(8);
                                if (bits < 0) return false;
                            } while (bits != 0);
                            gzipHeaderSubstate = HeaderState.ReadingComment;
                            goto case HeaderState.ReadingComment;
                        case HeaderState.ReadingComment:
                            if ((gzip_header_flag & 0x10) == 0)
                            {
                                gzipHeaderSubstate = HeaderState.ReadingCRC16Part1;
                                goto case HeaderState.ReadingCRC16Part1;
                            }
                            do
                            {
                                bits = input.GetBits(8);
                                if (bits < 0) return false;
                            } while (bits != 0);
                            gzipHeaderSubstate = HeaderState.ReadingCRC16Part1;
                            goto case HeaderState.ReadingCRC16Part1;
                        case HeaderState.ReadingCRC16Part1:
                            if ((gzip_header_flag & 0x02) == 0)
                            {
                                gzipHeaderSubstate = HeaderState.Done;
                                goto case HeaderState.Done;
                            }
                            bits = input.GetBits(8);
                            if (bits < 0) return false;
                            gzipHeaderSubstate = HeaderState.ReadingCRC16Part2;
                            goto case HeaderState.ReadingCRC16Part2;
                        case HeaderState.ReadingCRC16Part2:
                            bits = input.GetBits(8);
                            if (bits < 0) return false;
                            gzipHeaderSubstate = HeaderState.Done;
                            goto case HeaderState.Done;
                        case HeaderState.Done:
                            return true;
                        default:
                            throw new InvalidDataException(Messages.UnknownState);
                    }
                }
            }

            private bool ReadFooter()
            {
                input.SkipToByteBoundary();
                if (gzipFooterSubstate == HeaderState.ReadingCRC)
                {
                    while (gzipLoopCounter < 4)
                    {
                        int bits = input.GetBits(8);
                        if (bits < 0) return false;
                        expectedCrc32 |= (uint)bits << (8 * gzipLoopCounter);
                        gzipLoopCounter++;
                    }
                    gzipFooterSubstate = HeaderState.ReadingFileSize;
                    gzipLoopCounter = 0;
                }
                if (gzipFooterSubstate == HeaderState.ReadingFileSize)
                {
                    if (gzipLoopCounter == 0) expectedOutputStreamSizeModulo = 0;
                    while (gzipLoopCounter < 4)
                    {
                        int bits = input.GetBits(8);
                        if (bits < 0) return false;
                        expectedOutputStreamSizeModulo |= (uint)bits << (8 * gzipLoopCounter);
                        gzipLoopCounter++;
                    }
                }
                return true;
            }
        }
    }
}
