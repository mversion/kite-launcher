using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Kite.Plugins.Builtin.EnteAuth;

/// <summary>
/// Decrypts one libsodium crypto_secretstream_xchacha20poly1305 message, the format Ente Auth uses for
/// each stored code (a 24-byte header plus tag byte, ciphertext and 16-byte MAC). Pure managed code so
/// Kite needs no native libsodium; it follows libsodium's secretstream.c step for step, and the MAC is
/// checked before anything is decrypted.
/// </summary>
public static class SecretStream
{
    public const int HeaderBytes = 24, KeyBytes = 32, ABytes = 17;

    /// <summary>The plaintext, or null when the key is wrong or the data was altered.</summary>
    public static byte[]? Pull(ReadOnlySpan<byte> key, ReadOnlySpan<byte> header, ReadOnlySpan<byte> input)
    {
        if (key.Length != KeyBytes || header.Length != HeaderBytes || input.Length < ABytes) return null;

        // init_pull: k = HChaCha20(key, header[0..16]); nonce = counter(1, LE32) || header[16..24]
        var k = HChaCha20(key, header[..16]);
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(nonce, 1);
        header[16..24].CopyTo(nonce.AsSpan(4));

        var mlen = input.Length - ABytes;
        var c = input.Slice(1, mlen);
        var storedMac = input.Slice(1 + mlen, 16);

        // Poly1305 key from block 0; the tag byte travels in block 1.
        var polyKey = ChaCha20(k, nonce, 0, new byte[64]).AsSpan(0, 32).ToArray();
        var block = new byte[64];
        block[0] = input[0];
        block = ChaCha20(k, nonce, 1, block);
        block[0] = input[0];

        // MAC input exactly as libsodium feeds it (ad is empty here).
        var pad = (int)((0x10 - 64L + mlen) & 0xf);
        var macInput = new byte[64 + mlen + pad + 16];
        block.CopyTo(macInput, 0);
        c.CopyTo(macInput.AsSpan(64));
        BinaryPrimitives.WriteUInt64LittleEndian(macInput.AsSpan(64 + mlen + pad), 0);                  // adlen
        BinaryPrimitives.WriteUInt64LittleEndian(macInput.AsSpan(64 + mlen + pad + 8), (ulong)(64 + mlen)); // 64 + mlen
        var mac = Poly1305(polyKey, macInput);
        if (!CryptographicOperations.FixedTimeEquals(mac, storedMac)) return null;

        return ChaCha20(k, nonce, 2, c.ToArray());
    }

    /// <summary>Encrypts one final message (used by tests and for symmetry with <see cref="Pull"/>).</summary>
    public static (byte[] Header, byte[] Cipher) Push(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message, byte[]? header = null)
    {
        header ??= RandomNumberGenerator.GetBytes(HeaderBytes);
        var k = HChaCha20(key, header.AsSpan(0, 16));
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(nonce, 1);
        header.AsSpan(16, 8).CopyTo(nonce.AsSpan(4));

        var polyKey = ChaCha20(k, nonce, 0, new byte[64]).AsSpan(0, 32).ToArray();
        var block = new byte[64];
        block[0] = 3; // TAG_FINAL
        block = ChaCha20(k, nonce, 1, block);
        var c = ChaCha20(k, nonce, 2, message.ToArray());

        var mlen = message.Length;
        var pad = (int)((0x10 - 64L + mlen) & 0xf);
        var macInput = new byte[64 + mlen + pad + 16];
        block.CopyTo(macInput, 0);
        c.CopyTo(macInput, 64);
        BinaryPrimitives.WriteUInt64LittleEndian(macInput.AsSpan(64 + mlen + pad), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(macInput.AsSpan(64 + mlen + pad + 8), (ulong)(64 + mlen));
        var mac = Poly1305(polyKey, macInput);

        var output = new byte[1 + mlen + 16];
        output[0] = block[0];
        c.CopyTo(output, 1);
        mac.CopyTo(output, 1 + mlen);
        return (header, output);
    }

    // ------------------------------------------------------------------ ChaCha20 (RFC 8439) and HChaCha20

    private static uint Rotl(uint v, int c) => (v << c) | (v >> (32 - c));

    private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
    {
        x[a] += x[b]; x[d] = Rotl(x[d] ^ x[a], 16);
        x[c] += x[d]; x[b] = Rotl(x[b] ^ x[c], 12);
        x[a] += x[b]; x[d] = Rotl(x[d] ^ x[a], 8);
        x[c] += x[d]; x[b] = Rotl(x[b] ^ x[c], 7);
    }

    private static void Rounds(Span<uint> x)
    {
        for (var i = 0; i < 10; i++)
        {
            QuarterRound(x, 0, 4, 8, 12); QuarterRound(x, 1, 5, 9, 13); QuarterRound(x, 2, 6, 10, 14); QuarterRound(x, 3, 7, 11, 15);
            QuarterRound(x, 0, 5, 10, 15); QuarterRound(x, 1, 6, 11, 12); QuarterRound(x, 2, 7, 8, 13); QuarterRound(x, 3, 4, 9, 14);
        }
    }

    private static void InitState(Span<uint> s, ReadOnlySpan<byte> key)
    {
        s[0] = 0x61707865; s[1] = 0x3320646e; s[2] = 0x79622d32; s[3] = 0x6b206574;
        for (var i = 0; i < 8; i++) s[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
    }

    public static byte[] HChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce16)
    {
        Span<uint> x = stackalloc uint[16];
        InitState(x, key);
        for (var i = 0; i < 4; i++) x[12 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce16[(i * 4)..]);
        Rounds(x);
        var output = new byte[32];
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i * 4), x[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16 + i * 4), x[12 + i]);
        }
        return output;
    }

    /// <summary>XORs <paramref name="data"/> with the ChaCha20 keystream starting at block <paramref name="counter"/>.</summary>
    public static byte[] ChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce12, uint counter, byte[] data)
    {
        Span<uint> state = stackalloc uint[16];
        Span<uint> x = stackalloc uint[16];
        Span<byte> stream = stackalloc byte[64];
        InitState(state, key);
        for (var i = 0; i < 3; i++) state[13 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce12[(i * 4)..]);

        var output = new byte[data.Length];
        for (var offset = 0; offset < data.Length; offset += 64, counter++)
        {
            state[12] = counter;
            state.CopyTo(x);
            Rounds(x);
            for (var i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32LittleEndian(stream[(i * 4)..], x[i] + state[i]);
            var n = Math.Min(64, data.Length - offset);
            for (var i = 0; i < n; i++) output[offset + i] = (byte)(data[offset + i] ^ stream[i]);
        }
        return output;
    }

    // ------------------------------------------------------------------ Poly1305 (RFC 8439)

    private static readonly BigInteger P1305 = (BigInteger.One << 130) - 5;
    private static readonly BigInteger Clamp = BigInteger.Parse("0ffffffc0ffffffc0ffffffc0fffffff", System.Globalization.NumberStyles.HexNumber);

    public static byte[] Poly1305(ReadOnlySpan<byte> key32, ReadOnlySpan<byte> message)
    {
        var r = new BigInteger(key32[..16], isUnsigned: true) & Clamp;
        var s = new BigInteger(key32[16..32], isUnsigned: true);
        var acc = BigInteger.Zero;
        Span<byte> block = stackalloc byte[17];
        for (var offset = 0; offset < message.Length; offset += 16)
        {
            var n = Math.Min(16, message.Length - offset);
            block.Clear();
            message.Slice(offset, n).CopyTo(block);
            block[n] = 1;
            acc = (acc + new BigInteger(block[..(n + 1)], isUnsigned: true)) * r % P1305;
        }
        acc = (acc + s) & ((BigInteger.One << 128) - 1);
        var tag = new byte[16];
        acc.TryWriteBytes(tag, out _, isUnsigned: true);
        return tag;
    }
}
