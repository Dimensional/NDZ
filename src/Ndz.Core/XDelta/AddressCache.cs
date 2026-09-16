namespace Ndz.Core.XDelta;

/// <summary>
/// RFC 3284 §5.3's near/same address cache for COPY instruction addressing - the mechanism
/// that makes nearby, recently-used copy addresses cheap to encode (a near-cache hit costs a
/// small varint; a same-cache hit costs exactly one byte, no varint at all) instead of always
/// spelling out a full absolute address. Shared, identical logic for encode and decode - both
/// sides must derive the same cache state from the same address history for this to work at
/// all. Algorithm confirmed directly against the real, working `SnowflakePowered/vcdiff`
/// package's own `Shared/AddressCache.cs` (already exercised on every successful real-
/// `xdelta3.exe`-interop decode during this project's prior investigation - see
/// `docs/xdelta-vcdiff-notes.md`), not reconstructed from the RFC text alone.
///
/// <para>
/// Address modes, in order: 0 = SELF (an absolute offset into the source segment), 1 = HERE
/// (a backward offset from the current decode position), 2..(2+nearSize-1) = NEAR (the given
/// near-cache slot's last address plus a small positive offset), then
/// (2+nearSize)..(2+nearSize+sameSize-1) = SAME (a direct lookup into a same-cache slot
/// indexed by <c>address % (sameSize * 256)</c>, keyed with one trailing byte instead of a
/// varint).
/// </para>
/// </summary>
public sealed class AddressCache
{
    private const int FirstNear = 2; // RFC 3284's VCDiffModes.FIRST

    private readonly int _nearSize;
    private readonly int _sameSize;
    private readonly long[] _nearCache;
    private readonly long[] _sameCache;
    private int _nextSlot;

    public AddressCache(int nearSize = VcdiffFormat.DefaultNearCacheSize, int sameSize = VcdiffFormat.DefaultSameCacheSize)
    {
        _nearSize = nearSize;
        _sameSize = sameSize;
        _nearCache = new long[Math.Max(nearSize, 1)];
        _sameCache = new long[Math.Max(sameSize, 1) * 256];
    }

    private int FirstSame => FirstNear + _nearSize;
    private int LastMode => FirstSame + _sameSize - 1;

    /// <summary>True if <paramref name="mode"/> is a SAME-cache mode (encoded as one raw byte, no varint).</summary>
    public bool IsSameMode(int mode) => mode >= FirstSame && mode <= LastMode;

    private bool IsNearMode(int mode) => mode >= FirstNear && mode < FirstSame;

    /// <summary>
    /// Picks the cheapest way to address <paramref name="address"/> from the current decode
    /// position <paramref name="here"/>, updating the cache exactly as the decoder will when
    /// it re-derives the same address - returns the mode byte and the value to encode (a
    /// same-cache single byte if <paramref name="mode"/> comes back a SAME mode, otherwise a
    /// varint via <see cref="VarInt"/>).
    /// </summary>
    public int EncodeAddress(long address, long here, out long encodedValue)
    {
        if (_sameSize > 0)
        {
            int pos = (int)(address % (_sameSize * 256));
            if (_sameCache[pos] == address)
            {
                UpdateCache(address);
                encodedValue = pos % 256;
                return FirstSame + (pos / 256);
            }
        }

        int bestMode = 0; // SELF
        long bestEncoded = address;

        long hereEncoded = here - address;
        if (hereEncoded < bestEncoded)
        {
            bestMode = 1; // HERE
            bestEncoded = hereEncoded;
        }

        for (int i = 0; i < _nearSize; i++)
        {
            long nearEncoded = address - _nearCache[i];
            if (nearEncoded >= 0 && nearEncoded < bestEncoded)
            {
                bestMode = FirstNear + i;
                bestEncoded = nearEncoded;
            }
        }

        UpdateCache(address);
        encodedValue = bestEncoded;
        return bestMode;
    }

    /// <summary>Resolves a decoded (mode, encoded value) pair back to an absolute address, updating the cache the same way the encoder did when it chose this mode.</summary>
    public long DecodeAddress(long here, int mode, long encodedValue)
    {
        long decoded;
        if (IsSameMode(mode))
        {
            decoded = _sameCache[((mode - FirstSame) * 256) + (int)encodedValue];
        }
        else if (mode == 0) // SELF
        {
            decoded = encodedValue;
        }
        else if (mode == 1) // HERE
        {
            decoded = here - encodedValue;
        }
        else if (IsNearMode(mode))
        {
            decoded = _nearCache[mode - FirstNear] + encodedValue;
        }
        else
        {
            throw new XDeltaException($"Invalid VCDIFF address cache mode {mode}.");
        }

        UpdateCache(decoded);
        return decoded;
    }

    private void UpdateCache(long address)
    {
        if (_nearSize > 0)
        {
            _nearCache[_nextSlot] = address;
            _nextSlot = (_nextSlot + 1) % _nearSize;
        }
        if (_sameSize > 0)
        {
            _sameCache[(int)(address % (_sameSize * 256))] = address;
        }
    }
}
