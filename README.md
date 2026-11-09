# Stress-for-Windows

A CPU / RAM / hash / compression stress and **stability** tester for Windows (also builds on Linux/macOS with .NET).
Rewritten from the original fixed-load tool; the old syntax `WindowsStress.exe 50 all 20` still works.

Every worker verifies its own results (runs twice and compares, known answers, round trips, memory patterns).
Any mismatch is printed as `ERROR` and means the CPU, RAM, PSU or cooling is not stable under that load.

## Build

Requires the .NET 8 SDK.

    dotnet build -c Release WindowsStress
    # single exe for Windows:
    dotnet publish WindowsStress -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

## Modes

| Mode | What it does |
|---|---|
| `cpu` | integer, floating point and SIMD (SSE/AVX) math |
| `hash` | SHA-256/512/1, MD5, SHA3, PBKDF2 |
| `zip` | GZip, Brotli, Deflate, ZLib compress + decompress round trips |
| `scratch` | CryptoNight/RandomX style: 2 MB AES scratchpad per thread (cache/AES-NI stress) |
| `mine` | offline mining-style load: memory-hard hash (scratchpad filled, then random data-dependent reads/writes mixing integer, rotate, multiply, floating-point sqrt and AES-NI, then SHA-256). Reports H/s and "shares". **No network, no pool, no coins.** |
| `ram` | MemTest style: patterns (0xAA, 0x55, address, ~address), random XOR walks, verify |
| `fun` | Mandelbrot + prime sieve (known answer: 148933 primes below 2,000,000) |
| `all` | everything together (including mining), 40% of free RAM |
| `kill` | everything, 2x oversubscribed threads, 90% of free RAM, high priority |

## Options

    --load N       CPU load per thread, 1-100 (default 100)
    --threads N    worker threads or 'all'
    --time S       seconds, 0 = until Ctrl+C (default 60)
    --ram P        percent of FREE RAM, 1-95
    --mine-mem MB  scratchpad per mining thread, 2-1024 (default 32, auto-reduced if RAM is low)
    --priority X   low | normal | high
    --yes          skip the confirmation prompt (ram/all/kill ask you to type YES)

## Examples

    WindowsStress cpu --time 300
    WindowsStress hash --load 50 --time 120
    WindowsStress mine --mine-mem 256 --time 300
    WindowsStress ram --ram 70 --time 600
    WindowsStress kill --time 120
    WindowsStress 50 all 20            # old syntax

Exit code: `0` no errors, `2` errors found.

## Safety

`ram`, `all` and `kill` can make the machine slow, unresponsive or swap heavily. Save your work first.
256 MB of RAM is always left free, RealTime priority is never used, and Ctrl+C stops everything cleanly.
Make sure cooling works before running long tests.
