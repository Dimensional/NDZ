namespace Ndz.Core.XDelta;

/// <summary>
/// Applies an RFC 3284 (VCDIFF) delta against a source buffer. Supports multi-window
/// deltas (both <c>VCD_SOURCE</c>-against-the-external-source and
/// <c>VCD_TARGET</c>-against-previously-decoded-output windows, plus source-less windows),
/// the full default code table (including real encoders' combined two-instruction opcodes,
/// not just the single-instruction generic opcodes this project's own
/// <see cref="VcdiffEncoder"/> produces), and the near/same address cache - this is the
/// general decoder needed to correctly read real-world patches (e.g. from real
/// <c>xdelta3.exe</c> or ndz-studio), not just the narrower shape this project's own encoder
/// emits. Secondary (DJW) decompression is wired in separately - see
/// <see cref="Djw.DjwCodec"/>.
/// </summary>
public static class VcdiffDecoder
{
    /// <summary>Applies <paramref name="delta"/> against <paramref name="source"/>, returning the reconstructed target bytes.</summary>
    /// <exception cref="XDeltaException">The delta is malformed, uses an unsupported feature (a custom code table, or a secondary compressor other than none/DJW), or a window's checksum doesn't match.</exception>
    public static byte[] Decode(byte[] source, byte[] delta)
    {
        int pos = 0;
        ParseFileHeader(delta, ref pos, out byte secondaryCompressorId);

        var output = new List<byte>(source.Length);
        while (pos < delta.Length)
        {
            DecodeWindow(source, delta, ref pos, secondaryCompressorId, output);
        }

        return output.ToArray();
    }

    private static void ParseFileHeader(byte[] delta, ref int pos, out byte secondaryCompressorId)
    {
        if (delta.Length < 5 ||
            delta[0] != VcdiffFormat.Magic[0] || delta[1] != VcdiffFormat.Magic[1] ||
            delta[2] != VcdiffFormat.Magic[2] || delta[3] != VcdiffFormat.Magic[3])
        {
            throw new XDeltaException("Not a VCDIFF stream (bad magic).");
        }

        byte hdrIndicator = delta[4];
        pos = 5;
        secondaryCompressorId = VcdiffFormat.SecondaryNone;

        if ((hdrIndicator & VcdiffFormat.HdrDecompress) != 0)
            secondaryCompressorId = ReadByte(delta, ref pos);

        if ((hdrIndicator & VcdiffFormat.HdrCodeTable) != 0)
            throw new XDeltaException("This delta uses a custom VCDIFF code table, which isn't supported here.");

        if ((hdrIndicator & VcdiffFormat.HdrAppHeader) != 0)
        {
            long appHeaderLength = VarInt.Read(delta, ref pos);
            pos += checked((int)appHeaderLength);
        }
    }

    private static void DecodeWindow(byte[] source, byte[] delta, ref int pos, byte secondaryCompressorId, List<byte> output)
    {
        byte winIndicator = ReadByte(delta, ref pos);
        bool usesSource = (winIndicator & VcdiffFormat.WinSource) != 0;
        bool usesTarget = (winIndicator & VcdiffFormat.WinTarget) != 0;
        if (usesSource && usesTarget)
            throw new XDeltaException("A VCDIFF window can't set both VCD_SOURCE and VCD_TARGET.");

        long sourceSegmentLength = 0, sourceSegmentPosition = 0;
        if (usesSource || usesTarget)
        {
            sourceSegmentLength = VarInt.Read(delta, ref pos);
            sourceSegmentPosition = VarInt.Read(delta, ref pos);

            long available = usesSource ? source.Length : output.Count;
            if (sourceSegmentPosition < 0 || sourceSegmentLength < 0 || sourceSegmentPosition + sourceSegmentLength > available)
                throw new XDeltaException("A VCDIFF window's source segment runs outside the available source/target data.");
        }

        long deltaEncodingLength = VarInt.Read(delta, ref pos);
        int deltaEncodingStart = pos;

        long targetWindowLength = VarInt.Read(delta, ref pos);
        byte deltaIndicator = ReadByte(delta, ref pos);
        bool dataCompressed = (deltaIndicator & VcdiffFormat.DeltaDataComp) != 0;
        bool instCompressed = (deltaIndicator & VcdiffFormat.DeltaInstComp) != 0;
        bool addrCompressed = (deltaIndicator & VcdiffFormat.DeltaAddrComp) != 0;

        long addRunLength = VarInt.Read(delta, ref pos);
        long instructionsLength = VarInt.Read(delta, ref pos);
        long addressesLength = VarInt.Read(delta, ref pos);

        bool hasChecksum = (winIndicator & VcdiffFormat.WinChecksum) != 0;
        uint expectedChecksum = 0;
        if (hasChecksum)
        {
            byte c0 = ReadByte(delta, ref pos), c1 = ReadByte(delta, ref pos), c2 = ReadByte(delta, ref pos), c3 = ReadByte(delta, ref pos);
            expectedChecksum = (uint)((c0 << 24) | (c1 << 16) | (c2 << 8) | c3);
        }

        long headerLength = pos - deltaEncodingStart;
        if (headerLength + addRunLength + instructionsLength + addressesLength != deltaEncodingLength)
            throw new XDeltaException("A VCDIFF window's recorded length doesn't match its section lengths - corrupt or malformed delta.");

        byte[] addRunData = ReadSection(delta, ref pos, checked((int)addRunLength), dataCompressed, secondaryCompressorId, VcdiffSection.AddRunData);
        byte[] instructionsData = ReadSection(delta, ref pos, checked((int)instructionsLength), instCompressed, secondaryCompressorId, VcdiffSection.InstructionsAndSizes);
        byte[] addressesData = ReadSection(delta, ref pos, checked((int)addressesLength), addrCompressed, secondaryCompressorId, VcdiffSection.AddressesForCopy);

        int windowStart = output.Count;
        var cache = new AddressCache();
        int addRunPos = 0, instrPos = 0, addrPos = 0;
        int? pendingSecondOpcode = null;

        while (output.Count - windowStart < targetWindowLength)
        {
            byte instType, mode;
            int size;
            if (pendingSecondOpcode is int pending)
            {
                byte op = (byte)pending;
                pendingSecondOpcode = null;
                instType = VcdiffFormat.DefaultInst2[op];
                size = VcdiffFormat.DefaultSize2[op];
                mode = VcdiffFormat.DefaultMode2[op];
            }
            else
            {
                byte op = instructionsData[instrPos++];
                if (VcdiffFormat.DefaultInst2[op] != (byte)VcdiffInstructionType.Noop)
                    pendingSecondOpcode = op;
                instType = VcdiffFormat.DefaultInst1[op];
                size = VcdiffFormat.DefaultSize1[op];
                mode = VcdiffFormat.DefaultMode1[op];
            }

            if (size == 0)
                size = checked((int)VarInt.Read(instructionsData, ref instrPos));

            switch ((VcdiffInstructionType)instType)
            {
                case VcdiffInstructionType.Run:
                {
                    byte value = addRunData[addRunPos++];
                    for (int i = 0; i < size; i++)
                        output.Add(value);
                    break;
                }
                case VcdiffInstructionType.Add:
                    for (int i = 0; i < size; i++)
                        output.Add(addRunData[addRunPos++]);
                    break;
                case VcdiffInstructionType.Copy:
                {
                    long encodedValue = cache.IsSameMode(mode)
                        ? addressesData[addrPos++]
                        : VarInt.Read(addressesData, ref addrPos);
                    long here = sourceSegmentLength + (output.Count - windowStart);
                    long address = cache.DecodeAddress(here, mode, encodedValue);

                    for (int i = 0; i < size; i++)
                    {
                        long a = address + i;
                        byte value = a < sourceSegmentLength
                            ? (usesSource ? source[sourceSegmentPosition + a] : output[checked((int)(sourceSegmentPosition + a))])
                            : output[checked((int)(windowStart + (a - sourceSegmentLength)))];
                        output.Add(value);
                    }
                    break;
                }
                default:
                    throw new XDeltaException($"Unsupported VCDIFF instruction type {instType} - only ADD/RUN/COPY (the default code table) are supported.");
            }
        }

        if (output.Count - windowStart != targetWindowLength)
            throw new XDeltaException("A VCDIFF window decoded to a different length than its header declared.");

        if (hasChecksum)
        {
            Span<byte> windowBytes = output.Count - windowStart <= 4096
                ? stackalloc byte[output.Count - windowStart]
                : new byte[output.Count - windowStart];
            for (int i = 0; i < windowBytes.Length; i++)
                windowBytes[i] = output[windowStart + i];

            uint actual = Adler32.Compute(windowBytes);
            if (actual != expectedChecksum)
                throw new XDeltaException($"VCDIFF window checksum mismatch (expected 0x{expectedChecksum:X8}, got 0x{actual:X8}) - the source doesn't match what this delta was generated against.");
        }
    }

    private static byte[] ReadSection(byte[] delta, ref int pos, int length, bool compressed, byte secondaryCompressorId, VcdiffSection section)
    {
        if (length < 0 || pos + length > delta.Length)
            throw new XDeltaException("VCDIFF stream ends unexpectedly (truncated section data).");

        byte[] raw = new byte[length];
        Array.Copy(delta, pos, raw, 0, length);
        pos += length;

        if (!compressed)
            return raw;

        return secondaryCompressorId switch
        {
            VcdiffFormat.SecondaryDjw => Djw.DjwCodec.Decompress(raw),
            _ => throw new XDeltaException($"Section {section} is secondary-compressed with an unsupported compressor (id {secondaryCompressorId}) - only DJW (id 1) is supported."),
        };
    }

    private static byte ReadByte(byte[] data, ref int pos)
    {
        if (pos >= data.Length)
            throw new XDeltaException("VCDIFF stream ends unexpectedly.");
        return data[pos++];
    }
}
