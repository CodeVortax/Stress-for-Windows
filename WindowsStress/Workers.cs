using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace WindowsStress
{
    /// <summary>Prints worker errors (rate limited) and keeps a global error count.</summary>
    static class Reporter
    {
        static readonly object L = new object();
        static int printed;
        public static long TotalErrors;

        public static void Error(string who, string msg)
        {
            Interlocked.Increment(ref TotalErrors);
            lock (L)
            {
                if (printed++ < 20)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("!! ERROR in " + who + ": " + msg);
                }
            }
        }
    }

    static class Mixer
    {
        /// <summary>xorshift64 step. State must never be zero.</summary>
        public static ulong Next(ref ulong s)
        {
            s ^= s << 13;
            s ^= s >> 7;
            s ^= s << 17;
            return s;
        }

        public static ulong Mix(ulong x, int n)
        {
            for (int i = 0; i < n; i++)
            {
                x ^= x << 13;
                x ^= x >> 7;
                x ^= x << 17;
                x += 0x9E3779B97F4A7C15UL;
                x = (x << 5) | (x >> 59);
                x *= 0xBF58476D1CE4E5B9UL;
            }
            return x;
        }
    }

    /// <summary>
    /// Keeps a thread busy for roughly Load% of every 100 ms window.
    /// Work steps are short, so we check between steps.
    /// </summary>
    sealed class Throttle
    {
        readonly int load;
        readonly Stopwatch sw = Stopwatch.StartNew();
        long windowStart;

        public Throttle(int loadPercent) { load = loadPercent; }

        public void Pace()
        {
            if (load >= 100) return;
            long el = sw.ElapsedMilliseconds - windowStart;
            if (el >= load)
            {
                long rest = 100 - el;
                if (rest > 0) Thread.Sleep((int)rest);
                windowStart = sw.ElapsedMilliseconds;
            }
        }
    }

    /// <summary>
    /// A unit of stress work. Every worker double-checks its own results so that
    /// a flaky CPU/RAM shows up as an error (this is what makes it a *stability* test).
    /// Each instance is used by exactly one thread.
    /// </summary>
    abstract class Worker
    {
        public readonly string Name;
        public readonly bool Bytes;   // true: Ops counts bytes, false: plain operations
        public long Ops;
        public long Errors;
        public long Shares;   // only used by the mining worker

        protected Worker(string name, bool bytes) { Name = name; Bytes = bytes; }

        public abstract void Step();

        protected void Fail(string msg)
        {
            Errors++;
            Reporter.Error(Name, msg);
        }
    }

    // ---------------------------------------------------------------- CPU

    /// <summary>Integer ALU loop, run twice and compared.</summary>
    sealed class CpuIntWorker : Worker
    {
        ulong seed = ((ulong)Stopwatch.GetTimestamp()) | 1UL;
        public CpuIntWorker() : base("cpu-int", false) { }

        public override void Step()
        {
            seed = Mixer.Mix(seed, 1);
            ulong a = Mixer.Mix(seed, 200000);
            ulong b = Mixer.Mix(seed, 200000);
            if (a != b) Fail("integer result differs between two identical runs");
            Ops += 400000;
        }
    }

    /// <summary>Matrix multiply + sqrt/sin (floating point unit), run twice and compared.</summary>
    sealed class CpuFloatWorker : Worker
    {
        const int N = 48;
        readonly double[] a = new double[N * N];
        readonly double[] b = new double[N * N];
        readonly double[] c = new double[N * N];

        public CpuFloatWorker() : base("cpu-float", false)
        {
            var r = new Random();
            for (int i = 0; i < a.Length; i++) { a[i] = r.NextDouble() + 0.5; b[i] = r.NextDouble() + 0.5; }
        }

        double Run()
        {
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++)
                {
                    double s = 0;
                    for (int k = 0; k < N; k++) s += a[i * N + k] * b[k * N + j];
                    c[i * N + j] = Math.Sqrt(s) + Math.Sin(s);
                }
            double t = 0;
            for (int i = 0; i < c.Length; i++) t += c[i];
            return t;
        }

        public override void Step()
        {
            double x = Run(), y = Run();
            if (BitConverter.DoubleToInt64Bits(x) != BitConverter.DoubleToInt64Bits(y))
                Fail("floating-point result differs between two identical runs");
            Ops += 2L * N * N * N;
        }
    }

    /// <summary>Vector (SSE/AVX) multiply-add loop. Usually the hottest CPU load here.</summary>
    sealed class SimdWorker : Worker
    {
        readonly float[] x = new float[4096];
        readonly float[] y = new float[4096];

        public SimdWorker() : base("cpu-simd", false)
        {
            var r = new Random();
            for (int i = 0; i < x.Length; i++)
            {
                x[i] = 0.9f + (float)r.NextDouble() * 0.099f;
                y[i] = (float)r.NextDouble() * 0.001f;
            }
        }

        float Run()
        {
            int w = Vector<float>.Count;
            var acc = new Vector<float>(1.0f);
            for (int rep = 0; rep < 4000; rep++)
                for (int i = 0; i <= x.Length - w; i += w)
                    acc = acc * new Vector<float>(x, i) + new Vector<float>(y, i);
            return Vector.Sum(acc);
        }

        public override void Step()
        {
            float p = Run(), q = Run();
            if (BitConverter.SingleToInt32Bits(p) != BitConverter.SingleToInt32Bits(q))
                Fail("SIMD result differs between two identical runs");
            Ops += 2L * 4000 * 4096 * 2;
        }
    }

    // ---------------------------------------------------------------- Hashing

    /// <summary>SHA-256/512/1, MD5, SHA3 (when the OS supports it) and PBKDF2, each computed twice and compared.</summary>
    sealed class HashWorker : Worker
    {
        readonly byte[] buf = new byte[1 << 20];
        int n;

        public HashWorker() : base("hash", true) { new Random().NextBytes(buf); }

        public override void Step()
        {
            byte[] h1, h2;
            long work = 2L * buf.Length;
            switch (n++ % 6)
            {
                case 0: h1 = SHA256.HashData(buf); h2 = SHA256.HashData(buf); break;
                case 1: h1 = SHA512.HashData(buf); h2 = SHA512.HashData(buf); break;
                case 2: h1 = SHA1.HashData(buf); h2 = SHA1.HashData(buf); break;
                case 3: h1 = MD5.HashData(buf); h2 = MD5.HashData(buf); break;
                case 4:
                    if (SHA3_256.IsSupported) { h1 = SHA3_256.HashData(buf); h2 = SHA3_256.HashData(buf); }
                    else { h1 = SHA384.HashData(buf); h2 = SHA384.HashData(buf); }
                    break;
                default:
                    var pw = new byte[32];
                    var salt = new byte[16];
                    Array.Copy(buf, 0, pw, 0, 32);
                    Array.Copy(buf, 64, salt, 0, 16);
                    h1 = Rfc2898DeriveBytes.Pbkdf2(pw, salt, 20000, HashAlgorithmName.SHA256, 32);
                    h2 = Rfc2898DeriveBytes.Pbkdf2(pw, salt, 20000, HashAlgorithmName.SHA256, 32);
                    work = 2L * 20000 * 64;
                    break;
            }
            if (!h1.AsSpan().SequenceEqual(h2)) Fail("hash differs between two identical runs");
            Ops += work;
        }
    }

    /// <summary>
    /// CryptoNight/RandomX flavoured: a 2 MB scratchpad (L2/L3 cache) that is randomly read,
    /// AES-encrypted (AES-NI) and XORed back at other random spots.
    /// </summary>
    sealed class ScratchWorker : Worker
    {
        const int PadSize = 2 * 1024 * 1024;
        const int Win = 4096;
        readonly byte[] pad = new byte[PadSize];
        readonly byte[] tmp = new byte[Win];
        readonly Aes aes;
        ulong state;

        public ScratchWorker() : base("scratchpad-aes", true)
        {
            var r = new Random();
            r.NextBytes(pad);
            var key = new byte[32];
            r.NextBytes(key);
            aes = Aes.Create();
            aes.Key = key;
            state = (ulong)r.NextInt64() | 1UL;
        }

        public override void Step()
        {
            for (int it = 0; it < 256; it++)
            {
                Mixer.Next(ref state);
                int src = (int)(state % (ulong)((PadSize - Win) / 16)) * 16;
                aes.EncryptEcb(new ReadOnlySpan<byte>(pad, src, Win), tmp, PaddingMode.None);

                Mixer.Next(ref state);
                int dst = (int)(state % (ulong)((PadSize - Win) / 8)) * 8;
                Span<ulong> d = MemoryMarshal.Cast<byte, ulong>(pad.AsSpan(dst, Win));
                Span<ulong> t = MemoryMarshal.Cast<byte, ulong>(tmp.AsSpan());
                for (int i = 0; i < d.Length; i++) d[i] ^= t[i];
                Ops += 2L * Win;
            }
        }
    }

    // ---------------------------------------------------------------- Compression

    /// <summary>Compress then decompress with GZip/Brotli/Deflate/ZLib and verify the round trip.</summary>
    sealed class ZipWorker : Worker
    {
        readonly byte[] data = new byte[1 << 20];
        int n;

        public ZipWorker() : base("zip", true)
        {
            var r = new Random();
            byte[] word = System.Text.Encoding.ASCII.GetBytes("stress-test-stress-test-hello-world-0123456789 ");
            for (int off = 0; off < data.Length; off += 4096)
            {
                int len = Math.Min(4096, data.Length - off);
                if (((off / 4096) & 1) == 0)
                    r.NextBytes(data.AsSpan(off, len));          // incompressible
                else
                    for (int i = 0; i < len; i++) data[off + i] = word[i % word.Length];   // compressible
            }
        }

        static Stream Comp(int k, Stream dst)
        {
            switch (k)
            {
                case 0: return new GZipStream(dst, CompressionLevel.Optimal, true);
                case 1: return new BrotliStream(dst, CompressionLevel.Optimal, true);
                case 2: return new DeflateStream(dst, CompressionLevel.Optimal, true);
                default: return new ZLibStream(dst, CompressionLevel.Optimal, true);
            }
        }

        static Stream Decomp(int k, Stream src)
        {
            switch (k)
            {
                case 0: return new GZipStream(src, CompressionMode.Decompress, true);
                case 1: return new BrotliStream(src, CompressionMode.Decompress, true);
                case 2: return new DeflateStream(src, CompressionMode.Decompress, true);
                default: return new ZLibStream(src, CompressionMode.Decompress, true);
            }
        }

        public override void Step()
        {
            int k = n++ % 4;
            var packed = new MemoryStream();
            using (var z = Comp(k, packed)) z.Write(data, 0, data.Length);
            packed.Position = 0;
            var unpacked = new MemoryStream(data.Length);
            using (var z = Decomp(k, packed)) z.CopyTo(unpacked);

            if (unpacked.Length != data.Length ||
                !new ReadOnlySpan<byte>(unpacked.GetBuffer(), 0, (int)unpacked.Length).SequenceEqual(data))
                Fail("compress/decompress round trip returned different data");
            Ops += 2L * data.Length;
        }
    }

    // ---------------------------------------------------------------- Fun

    /// <summary>Mandelbrot set (floating point + branches). Iteration count is the checksum.</summary>
    sealed class MandelWorker : Worker
    {
        public MandelWorker() : base("mandelbrot", false) { }

        static long Run()
        {
            const int S = 192;
            long total = 0;
            for (int py = 0; py < S; py++)
                for (int px = 0; px < S; px++)
                {
                    double cr = -2.0 + px * 3.0 / S, ci = -1.5 + py * 3.0 / S;
                    double zr = 0, zi = 0;
                    int it = 0;
                    while (it < 300 && zr * zr + zi * zi < 4.0)
                    {
                        double t = zr * zr - zi * zi + cr;
                        zi = 2 * zr * zi + ci;
                        zr = t;
                        it++;
                    }
                    total += it;
                }
            return total;
        }

        public override void Step()
        {
            long a = Run(), b = Run();
            if (a != b) Fail("Mandelbrot checksum differs between two identical runs");
            Ops += a + b;
        }
    }

    /// <summary>Sieve of Eratosthenes. There are exactly 148933 primes below 2,000,000, so the answer is known.</summary>
    sealed class SieveWorker : Worker
    {
        const int Limit = 2000000;
        readonly bool[] comp = new bool[Limit + 1];

        public SieveWorker() : base("prime-sieve", false) { }

        public override void Step()
        {
            Array.Clear(comp, 0, comp.Length);
            comp[0] = comp[1] = true;
            for (long i = 2; i * i <= Limit; i++)
                if (!comp[i])
                    for (long j = i * i; j <= Limit; j += i) comp[j] = true;
            int count = 0;
            for (int i = 2; i <= Limit; i++) if (!comp[i]) count++;
            if (count != 148933) Fail("prime count was " + count + " instead of 148933");
            Ops += Limit;
        }
    }

    // ---------------------------------------------------------------- Mining

    /// <summary>
    /// Offline proof-of-work style load (no network, no pool, no coins): a RandomX/scrypt flavoured,
    /// memory-hard hash. Per nonce: seed with SHA-256, fill a big scratchpad, then run a long chain of
    /// data-dependent random reads/writes where each step picks a different "instruction"
    /// (integer, rotate, multiply, floating point sqrt, AES-NI), and finish with SHA-256.
    /// A hash with enough leading zero bits counts as a "share". Every 4th nonce is hashed twice
    /// and compared so unstable hardware shows up as an error.
    /// </summary>
    sealed class MineWorker : Worker
    {
        public static int PadMB = 32;       // scratchpad per thread
        const int Difficulty = 12;          // leading zero bits for a share (about 1 in 4096 hashes)
        const int Rounds = 200000;          // data-dependent steps per hash

        readonly ulong[] pad;
        readonly Aes aes;
        readonly byte[] header = new byte[80];
        readonly byte[] blk = new byte[16];
        readonly byte[] enc = new byte[16];
        readonly byte[] fin = new byte[24 + 8 * 64];
        ulong nonce;

        public MineWorker() : base("mine-memhard", false)
        {
            pad = new ulong[(int)((long)PadMB * 1024 * 1024 / 8)];
            var r = new Random();
            r.NextBytes(header);
            var key = new byte[32];
            r.NextBytes(key);
            aes = Aes.Create();
            aes.Key = key;
            nonce = (ulong)r.NextInt64();
        }

        byte[] Hash(ulong n)
        {
            BitConverter.TryWriteBytes(header.AsSpan(72, 8), n);
            byte[] seed = SHA256.HashData(header);
            ulong a = BitConverter.ToUInt64(seed, 0) | 1UL;
            ulong b = BitConverter.ToUInt64(seed, 8);

            ulong f = BitConverter.ToUInt64(seed, 16) | 1UL;
            for (int i = 0; i < pad.Length; i++) { Mixer.Next(ref f); pad[i] = f; }

            double fp = 1.0 + (a & 0xFFFF) / 65536.0;
            for (int r = 0; r < Rounds; r++)
            {
                int j = (int)(a % (ulong)pad.Length);
                ulong v = pad[j];
                switch ((int)(v & 7))
                {
                    case 0: a += v * 0x9E3779B97F4A7C15UL; break;
                    case 1: a ^= (v << 17) | (v >> 47); break;
                    case 2: a *= (v | 1UL); break;
                    case 3:
                        fp = Math.Sqrt(fp + (double)(v & 0xFFFFF));
                        a ^= (ulong)BitConverter.DoubleToInt64Bits(fp);
                        break;
                    case 4: a = ((a << 13) | (a >> 51)) + v; break;
                    case 5:
                        BitConverter.TryWriteBytes(blk.AsSpan(0, 8), a);
                        BitConverter.TryWriteBytes(blk.AsSpan(8, 8), v);
                        aes.EncryptEcb(blk, enc, PaddingMode.None);
                        a ^= BitConverter.ToUInt64(enc, 0);
                        b ^= BitConverter.ToUInt64(enc, 8);
                        break;
                    case 6: b += a ^ v; a = b ^ (a >> 3); break;
                    default: a -= v; a ^= b; break;
                }
                pad[j] = a ^ b ^ v;   // write back: the next read depends on this
                b = ((b << 7) | (b >> 57)) ^ a;
            }

            BitConverter.TryWriteBytes(fin.AsSpan(0, 8), a);
            BitConverter.TryWriteBytes(fin.AsSpan(8, 8), b);
            BitConverter.TryWriteBytes(fin.AsSpan(16, 8), BitConverter.DoubleToInt64Bits(fp));
            for (int i = 0; i < 64; i++)
                BitConverter.TryWriteBytes(fin.AsSpan(24 + 8 * i, 8), pad[(int)((long)i * (pad.Length - 1) / 63)]);
            return SHA256.HashData(fin);
        }

        static int LeadingZeroBits(byte[] h)
        {
            int z = 0;
            foreach (byte x in h)
            {
                if (x == 0) { z += 8; continue; }
                z += BitOperations.LeadingZeroCount((uint)x) - 24;
                break;
            }
            return z;
        }

        public override void Step()
        {
            ulong n = nonce++;
            byte[] h = Hash(n);
            Ops++;
            if ((n & 3) == 0)
            {
                byte[] h2 = Hash(n);
                Ops++;
                if (!h.AsSpan().SequenceEqual(h2)) Fail("same nonce gave two different hashes");
            }
            if (LeadingZeroBits(h) >= Difficulty) Shares++;
        }
    }

    // ---------------------------------------------------------------- RAM

    /// <summary>
    /// MemTest-style RAM worker. Grabs up to its budget in 64 MB chunks, then for every chunk:
    /// fill with a pattern (0xAA.., 0x55.., address, ~address) -> random XOR walk applied twice
    /// (which must cancel out exactly) -> verify the whole chunk.
    /// </summary>
    sealed class RamWorker : Worker
    {
        const int ChunkLongs = 8 * 1024 * 1024;   // 64 MB
        public static long AllocatedBytes;

        readonly long budget;
        readonly List<ulong[]> chunks = new List<ulong[]>();
        long mine;
        bool doneAllocating;
        int cursor, pass;
        ulong seed = ((ulong)Stopwatch.GetTimestamp()) | 1UL;

        public RamWorker(long budgetBytes) : base("ram", true) { budget = budgetBytes; }

        static ulong Pat(int kind, int i)
        {
            ulong a = (ulong)i * 0x9E3779B97F4A7C15UL;
            switch (kind)
            {
                case 0: return 0xAAAAAAAAAAAAAAAAUL;
                case 1: return 0x5555555555555555UL;
                case 2: return a;
                default: return ~a;
            }
        }

        static void Walk(ulong[] c, ulong s0)
        {
            ulong s = s0;
            int n = c.Length / 4;
            for (int k = 0; k < n; k++)
            {
                Mixer.Next(ref s);
                c[(int)(s % (ulong)c.Length)] ^= s;
            }
        }

        public override void Step()
        {
            if (!doneAllocating)
            {
                if (mine + ChunkLongs * 8L > budget) { doneAllocating = true; }
                else
                {
                    try
                    {
                        var c = GC.AllocateUninitializedArray<ulong>(ChunkLongs);
                        for (int i = 0; i < c.Length; i++) c[i] = Pat(2, i);   // touch every page
                        chunks.Add(c);
                        mine += ChunkLongs * 8L;
                        Interlocked.Add(ref AllocatedBytes, ChunkLongs * 8L);
                        Ops += ChunkLongs * 8L;
                    }
                    catch (OutOfMemoryException) { doneAllocating = true; }
                    return;
                }
            }

            if (chunks.Count == 0) { Thread.Sleep(100); return; }

            var chunk = chunks[cursor++ % chunks.Count];
            int kind = pass++ % 4;
            for (int i = 0; i < chunk.Length; i++) chunk[i] = Pat(kind, i);

            seed = Mixer.Mix(seed, 1) | 1UL;
            Walk(chunk, seed);
            Walk(chunk, seed);   // same sequence again: must restore the pattern exactly

            for (int i = 0; i < chunk.Length; i++)
            {
                if (chunk[i] != Pat(kind, i))
                {
                    Fail("memory mismatch at word " + i + " (pattern " + kind + ")");
                    break;
                }
            }
            Ops += chunk.Length * 8L * 2 + (chunk.Length / 4) * 8L * 2;
        }
    }
}
