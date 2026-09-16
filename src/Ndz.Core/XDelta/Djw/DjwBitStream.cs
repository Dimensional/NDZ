namespace Ndz.Core.XDelta.Djw;

/// <summary>
/// DJW's bit-packing convention, confirmed directly from real xdelta3 decoder source
/// (`djw_decode_symbol`/`xd3_decode_bits` in `xdelta3-djw.h`): within each byte, bits are
/// consumed starting from bit 0 (the byte's LSB) up to bit 7 (MSB); each bit read shifts the
/// accumulating value left and ORs the new bit into the low position - so the FIRST bit
/// pulled from the stream becomes the MOST significant bit of the decoded value. The writer
/// here produces the exact mirror image, one bit at a time, for the same reason this project
/// never guesses a bitstream convention: getting this backwards produces silently-wrong,
/// not-obviously-broken output.
/// </summary>
public sealed class DjwBitWriter
{
    private readonly List<byte> _output;
    private byte _currentByte;
    private int _bitCount; // bits already placed in _currentByte, 0..7

    public DjwBitWriter(List<byte> output) => _output = output;

    /// <summary>Writes the low <paramref name="bitCount"/> bits of <paramref name="value"/>, most-significant-first.</summary>
    public void WriteBits(int value, int bitCount)
    {
        for (int i = bitCount - 1; i >= 0; i--)
        {
            int bit = (value >> i) & 1;
            _currentByte |= (byte)(bit << _bitCount);
            _bitCount++;
            if (_bitCount == 8)
            {
                _output.Add(_currentByte);
                _currentByte = 0;
                _bitCount = 0;
            }
        }
    }

    /// <summary>Pads the final partial byte with zero bits and flushes it, if any bits are pending.</summary>
    public void Flush()
    {
        if (_bitCount > 0)
        {
            _output.Add(_currentByte);
            _currentByte = 0;
            _bitCount = 0;
        }
    }
}

/// <summary>The read-side mirror of <see cref="DjwBitWriter"/>.</summary>
public sealed class DjwBitReader
{
    private readonly byte[] _data;
    private int _bytePos;
    private byte _currentByte;
    private int _bitMask = 0x100; // 0x100 signals "need a new byte"

    public DjwBitReader(byte[] data, int startOffset)
    {
        _data = data;
        _bytePos = startOffset;
    }

    public int ReadBits(int bitCount)
    {
        int value = 0;
        for (int i = 0; i < bitCount; i++)
            value = (value << 1) | ReadBit();
        return value;
    }

    public int ReadBit()
    {
        if (_bitMask == 0x100)
        {
            if (_bytePos >= _data.Length)
                throw new XDeltaException("DJW secondary decoder ran out of input.");
            _currentByte = _data[_bytePos++];
            _bitMask = 1;
        }

        int bit = (_currentByte & _bitMask) != 0 ? 1 : 0;
        _bitMask <<= 1;
        return bit;
    }
}
