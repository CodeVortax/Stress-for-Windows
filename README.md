# Stress-for-Windows

A CPU / RAM / hash / compression / mining-style stress and **stability** tester for Windows (also builds on Linux/macOS with .NET).
Rewritten from the original fixed-load tool; the old syntax `WindowsStress.exe 50 all 20` still works.

Every worker verifies its own results — runs twice and compares, checks against a known answer, round-trips data, or
rewrites memory to a known pattern and reads it back. Any mismatch is printed as `ERROR`, which means the CPU, RAM,
power supply or cooling is not stable under that load. That's the point of a stress test: find out what breaks
*before* it breaks during something that matters.

## Download

Grab the latest build straight from the [Releases page](../../releases/tag/latest) — no build tools needed:

**[⬇ WindowsStress-win-x64.exe](../../releases/download/latest/WindowsStress-win-x64.exe)** (~2.7 MB, single file, nothing to install)

Other platforms (win-arm64, linux-x64, osx-arm64) are on the same release page.

Windows SmartScreen will likely warn because the exe isn't code-signed — click **More info → Run anyway**.

One-line PowerShell download + run (quick CPU test):

    iwr -Uri https://github.com/CodeVortax/Stress-for-Windows/releases/download/latest/WindowsStress-win-x64.exe -OutFile WindowsStress.exe; .\WindowsStress.exe cpu --time 20

## Modes

| Mode | Scenario | What a failure tells you |
|---|---|---|
| `cpu` | New build or overclock — check the CPU holds up; quick heat/fan check | Bad overclock, weak cooling, too little voltage |
| `hash` | Steady heavy crypto-style load | Same as `cpu`, on the hashing instructions specifically |
| `zip` | Mixed CPU + memory load, close to real work like compressing backups | CPU or RAM errors that only show up on data-heavy work |
| `scratch` | Hits CPU cache and AES-NI (the CryptoNight mining fingerprint) | Unstable cache or memory controller |
| `mine` | Offline mining-style load: memory-hard hash, random RAM access + math. One of the heaviest sustained loads there is | Unstable RAM timings or CPU. Reports H/s and "shares" — **no network, no pool, no coins, nothing submitted anywhere** |
| `ram` | New RAM, XMP/EXPO enabled, or random crashes | Faulty stick, bad timings, too little voltage |
| `fun` | Light test: Mandelbrot + prime sieve | Rare; usually a CPU fault |
| `all` | Full health check — everything at once, 40% of free RAM | Any component is weak |
| `kill` | "What happens at the maximum" — 2x oversubscribed threads, 90% of free RAM, high priority | Expect lag, swapping, or a crash. A hard reboot here usually means the power supply or cooling is the real limit |

## Options

    --load N       CPU load per thread, 1-100 (default 100)
    --threads N    worker threads, or 'all' (default: all logical cores)
    --time S       seconds, 0 = until Ctrl+C (default 60)
    --ram P        percent of FREE RAM to grab, 1-95 (ram/all/kill modes)
    --mine-mem MB  scratchpad per mining thread, 2-1024 (default 32; auto-reduced if RAM is low)
    --priority X   low | normal | high (default low; kill defaults to high)
    --yes          skip the "type YES" confirmation prompt (ram/all/kill ask by default)

## Examples

    WindowsStress cpu --time 60                     # quick check, all cores, 100% load
    WindowsStress cpu --load 50 --time 120           # half load, for temperature/power testing
    WindowsStress cpu --threads 1 --time 300         # find a single weak core
    WindowsStress hash --time 120
    WindowsStress zip --time 120
    WindowsStress mine --mine-mem 256 --time 300     # 256 MB scratchpad misses cache, hits RAM
    WindowsStress ram --ram 50 --time 300            # RAM check while you keep working
    WindowsStress all --time 600                     # full health check, ~10 min
    WindowsStress mine --mine-mem 256 --time 28800    # overnight stability test (8 hours)
    WindowsStress kill --time 120                    # for fun, to the limit
    WindowsStress 50 all 20                          # old syntax: 50% load, all cores, 20s

Exit code: `0` no errors, `2` errors found.

### Run in the background (PowerShell)

`kill`, `all` and `ram` normally ask you to type `YES` first. To run unattended/hidden, pass `--yes`:

    iwr -Uri https://github.com/CodeVortax/Stress-for-Windows/releases/download/latest/WindowsStress-win-x64.exe -OutFile WindowsStress.exe
    Start-Process -FilePath .\WindowsStress.exe -ArgumentList "kill --time 120 --yes" -WindowStyle Hidden -RedirectStandardOutput NUL -RedirectStandardError NUL

Stop it early:

    Stop-Process -Name WindowsStress

## Safety

`ram`, `all` and `kill` can make the machine slow, unresponsive, or heavily swap to disk — save your work first.
- 256 MB of RAM is always left free.
- RealTime priority is never used.
- Ctrl+C stops everything cleanly (use `Stop-Process -Name WindowsStress` if running hidden/backgrounded).
- Make sure cooling actually works before running long or `kill`-mode tests.

## Build from source

Requires the .NET 8 SDK.

    dotnet build -c Release WindowsStress

    # small single-file exe (NativeAOT, ~2.7 MB, what the release uses):
    dotnet publish WindowsStress -c Release -r win-x64 -p:PublishAot=true -p:IlcOptimizationPreference=Size

    # self-contained single-file (larger, ~65 MB, includes the full runtime):
    dotnet publish WindowsStress -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

CI (`.github/workflows/release.yml`) builds on every push/PR and publishes a `latest` release with every push to
`master`, plus a versioned release whenever a tag like `v2.0.0` is pushed.
