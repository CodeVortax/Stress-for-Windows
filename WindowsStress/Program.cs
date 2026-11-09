using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace WindowsStress
{
    sealed class Options
    {
        public string Mode = "cpu";
        public int Load = 100;
        public int Threads = Environment.ProcessorCount;
        public int Seconds = 60;
        public int RamPercent = -1;      // -1 = mode default
        public string Priority = null;   // null = mode default
        public bool Yes;
    }

    static class Program
    {
        const string Usage = @"
WindowsStress - CPU / RAM / hash / zip stress and stability tester

Usage:
  WindowsStress <mode> [options]
  WindowsStress <cpu%> <all|threads> <seconds>        (old syntax, same as: cpu --load N ...)

Modes:
  cpu      integer + floating point + SIMD math
  hash     SHA-256/512/1, MD5, SHA3, PBKDF2
  zip      GZip / Brotli / Deflate / ZLib compress + decompress round trips
  scratch  CryptoNight/RandomX style: 2 MB AES scratchpad per thread (cache stress)
  ram      MemTest style: fills RAM with patterns, random XOR walks, verifies
  fun      Mandelbrot + prime sieve
  all      everything above together, RAM budget 40% of free memory
  kill     everything, oversubscribed threads, 90% of free RAM, high priority

Options:
  --load N        target CPU load per thread 1-100 (default 100)
  --threads N     number of worker threads, or 'all' (default: all logical cores)
  --time S        duration in seconds, 0 = until Ctrl+C (default 60)
  --ram P         percent of FREE RAM to grab, 1-95 (ram/all/kill modes)
  --priority X    low | normal | high (default low; kill uses high)
  --yes           skip the confirmation prompt
  -h, --help      show this help

Examples:
  WindowsStress cpu --time 300
  WindowsStress hash --load 50 --time 120
  WindowsStress ram --ram 70 --time 600
  WindowsStress kill --time 120

Every worker checks its own results. Any mismatch is reported as an ERROR,
which means the CPU, RAM or power supply is not stable under this load.
Ctrl+C stops the test cleanly. Exit code: 0 = no errors, 2 = errors found.
";

        static readonly string[] Modes = { "cpu", "hash", "zip", "scratch", "ram", "fun", "all", "kill" };

        static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "-h" || args[0] == "--help" || args[0] == "/?"))
            {
                Console.WriteLine(Usage);
                return 0;
            }

            Options o;
            string err = ParseArgs(args, out o);
            if (err != null)
            {
                Console.Error.WriteLine("Error: " + err);
                Console.WriteLine(Usage);
                return 1;
            }

            // ---- header
            GC.Collect();
            long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            long free = FreeBytes();
            Console.WriteLine("WindowsStress  mode={0}  load={1}%  threads={2}  time={3}  priority={4}",
                o.Mode, o.Load, o.Threads, o.Seconds == 0 ? "until Ctrl+C" : o.Seconds + "s", o.Priority);
            Console.WriteLine("Machine: {0} logical cores, RAM {1} total / {2} free, {3}",
                Environment.ProcessorCount, Human(total, true), Human(free, true), RuntimeInformation.OSDescription);

            bool useRam = o.Mode == "ram" || o.Mode == "all" || o.Mode == "kill";
            long budget = 0;
            if (useRam)
            {
                budget = (long)(free * (o.RamPercent / 100.0));
                budget = Math.Min(budget, free - 256L * 1024 * 1024);   // always leave 256 MB
                if (budget < 0) budget = 0;
                Console.WriteLine("RAM to be used: {0} ({1}% of free)", Human(budget, true), o.RamPercent);
            }

            // ---- confirmation for the risky modes
            if (useRam && !o.Yes)
            {
                if (Console.IsInputRedirected)
                {
                    Console.Error.WriteLine("This mode needs confirmation. Run it in a console or add --yes.");
                    return 1;
                }
                Console.Write("This can make the machine slow or unresponsive and unsaved work may be lost.\nType YES to continue: ");
                string answer = Console.ReadLine();
                if (answer == null || answer.Trim() != "YES") { Console.WriteLine("Cancelled."); return 1; }
            }

            SetPriority(o.Priority);

            // ---- build workers
            int computeThreads = o.Mode == "ram" ? 0 : o.Threads;
            int ramThreads = 0;
            if (o.Mode == "ram") ramThreads = o.Threads;
            else if (o.Mode == "kill") ramThreads = o.Threads;
            else if (o.Mode == "all") ramThreads = Math.Max(1, o.Threads / 2);
            if (ramThreads > 0)   // every RAM thread needs at least one 64 MB chunk
                ramThreads = (int)Math.Max(1, Math.Min(ramThreads, budget / (64L * 1024 * 1024)));

            var all = new List<Worker>();
            var jobs = new List<Thread>();
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
            var token = cts.Token;

            for (int t = 0; t < computeThreads; t++)
            {
                List<Worker> mine = MakeWorkers(o.Mode);
                all.AddRange(mine);
                int id = t;
                jobs.Add(new Thread(() => Loop(id, mine, o.Load, token)) { IsBackground = true, Name = "compute-" + t });
            }
            for (int t = 0; t < ramThreads; t++)
            {
                var rw = new RamWorker(budget / ramThreads);
                all.Add(rw);
                var one = new List<Worker> { rw };
                jobs.Add(new Thread(() => Loop(0, one, o.Load, token)) { IsBackground = true, Name = "ram-" + t });
            }

            Console.WriteLine("Starting {0} threads ({1} compute + {2} RAM). Press Ctrl+C to stop.\n",
                jobs.Count, computeThreads, ramThreads);

            var clock = Stopwatch.StartNew();
            foreach (var th in jobs) th.Start();

            // ---- monitor
            while (!token.IsCancellationRequested)
            {
                if (o.Seconds > 0 && clock.Elapsed.TotalSeconds >= o.Seconds) break;
                token.WaitHandle.WaitOne(1000);

                string line = string.Format("[{0}{1}] threads {2} | RAM held {3} | errors {4}{5}",
                    clock.Elapsed.ToString(@"hh\:mm\:ss"),
                    o.Seconds > 0 ? "/" + TimeSpan.FromSeconds(o.Seconds).ToString(@"hh\:mm\:ss") : "",
                    jobs.Count,
                    Human(Interlocked.Read(ref RamWorker.AllocatedBytes), true),
                    Interlocked.Read(ref Reporter.TotalErrors),
                    TempText());
                Console.Write("\r" + line.PadRight(Math.Max(line.Length, 100)));
            }

            // ---- stop and report
            cts.Cancel();
            foreach (var th in jobs) th.Join(10000);
            clock.Stop();
            Console.WriteLine("\n");

            double secs = Math.Max(0.001, clock.Elapsed.TotalSeconds);
            Console.WriteLine("{0,-16} {1,14} {2,16} {3,8}", "Worker", "Work done", "Average rate", "Errors");
            foreach (var g in all.GroupBy(w => w.Name))
            {
                long ops = g.Sum(w => w.Ops);
                long errs = g.Sum(w => w.Errors);
                bool bytes = g.First().Bytes;
                Console.WriteLine("{0,-16} {1,14} {2,16} {3,8}", g.Key,
                    Human(ops, bytes), Human((long)(ops / secs), bytes) + "/s", errs);
            }

            long totalErrors = Interlocked.Read(ref Reporter.TotalErrors);
            Console.WriteLine();
            if (totalErrors == 0)
            {
                Console.WriteLine("PASS: no errors in {0:0.#} s.", clock.Elapsed.TotalSeconds);
                return 0;
            }
            Console.WriteLine("FAIL: {0} errors. Hardware (CPU/RAM/PSU/cooling) or an overclock is not stable under this load.", totalErrors);
            return 2;
        }

        // ------------------------------------------------------------------ worker threads

        static List<Worker> MakeWorkers(string mode)
        {
            var l = new List<Worker>();
            bool everything = mode == "all" || mode == "kill";
            if (mode == "cpu" || everything) { l.Add(new CpuIntWorker()); l.Add(new CpuFloatWorker()); l.Add(new SimdWorker()); }
            if (mode == "hash" || everything) l.Add(new HashWorker());
            if (mode == "zip" || everything) l.Add(new ZipWorker());
            if (mode == "scratch" || everything) l.Add(new ScratchWorker());
            if (mode == "fun" || everything) { l.Add(new MandelWorker()); l.Add(new SieveWorker()); }
            return l;
        }

        static void Loop(int id, List<Worker> workers, int load, CancellationToken token)
        {
            var throttle = new Throttle(load);
            int k = id % workers.Count;   // threads start on different workers
            while (!token.IsCancellationRequested)
            {
                try { workers[k].Step(); }
                catch (OutOfMemoryException) { Thread.Sleep(200); }
                k = (k + 1) % workers.Count;
                throttle.Pace();
            }
        }

        // ------------------------------------------------------------------ helpers

        static string ParseArgs(string[] a, out Options o)
        {
            o = new Options();
            int n;

            // old syntax: WindowsStress.exe 50 all 20
            if (a.Length > 0 && int.TryParse(a[0], out n))
            {
                o.Load = n;
                if (a.Length > 1)
                {
                    int t;
                    if (a[1].Equals("all", StringComparison.OrdinalIgnoreCase)) o.Threads = Environment.ProcessorCount;
                    else if (!int.TryParse(a[1], out t)) return "thread count must be a number or 'all'";
                    else o.Threads = t == 0 ? Environment.ProcessorCount : t;
                }
                if (a.Length > 2 && !int.TryParse(a[2], out o.Seconds)) return "duration must be a number of seconds";
                return Finish(o);
            }

            int i = 0;
            if (a.Length > 0 && !a[0].StartsWith("-")) { o.Mode = a[0].ToLowerInvariant(); i = 1; }

            for (; i < a.Length; i++)
            {
                switch (a[i].ToLowerInvariant())
                {
                    case "--load":
                        if (i + 1 >= a.Length || !int.TryParse(a[++i], out o.Load)) return "--load needs a number";
                        break;
                    case "--time":
                        if (i + 1 >= a.Length || !int.TryParse(a[++i], out o.Seconds)) return "--time needs a number of seconds";
                        break;
                    case "--ram":
                        if (i + 1 >= a.Length || !int.TryParse(a[++i], out o.RamPercent)) return "--ram needs a percentage";
                        break;
                    case "--threads":
                        if (i + 1 >= a.Length) return "--threads needs a number or 'all'";
                        string v = a[++i];
                        if (v.Equals("all", StringComparison.OrdinalIgnoreCase)) o.Threads = Environment.ProcessorCount;
                        else if (!int.TryParse(v, out o.Threads)) return "--threads needs a number or 'all'";
                        break;
                    case "--priority":
                        if (i + 1 >= a.Length) return "--priority needs low, normal or high";
                        o.Priority = a[++i].ToLowerInvariant();
                        break;
                    case "--yes":
                    case "-y":
                        o.Yes = true;
                        break;
                    default:
                        return "unknown option: " + a[i];
                }
            }
            return Finish(o);
        }

        static string Finish(Options o)
        {
            if (Array.IndexOf(Modes, o.Mode) < 0) return "unknown mode: " + o.Mode;
            if (o.Load < 1 || o.Load > 100) return "load must be between 1 and 100";
            if (o.Threads < 1) return "threads must be at least 1";
            if (o.Seconds < 0) return "time cannot be negative";

            if (o.RamPercent < 0)
                o.RamPercent = o.Mode == "kill" ? 90 : o.Mode == "all" ? 40 : o.Mode == "ram" ? 60 : 0;
            if (o.RamPercent > 95) return "--ram cannot be more than 95";

            if (o.Priority == null) o.Priority = o.Mode == "kill" ? "high" : "low";
            if (o.Priority != "low" && o.Priority != "normal" && o.Priority != "high")
                return "priority must be low, normal or high";
            return null;
        }

        static void SetPriority(string p)
        {
            try
            {
                var proc = Process.GetCurrentProcess();
                proc.PriorityClass = p == "high" ? ProcessPriorityClass.High
                                   : p == "normal" ? ProcessPriorityClass.Normal
                                   : ProcessPriorityClass.BelowNormal;
            }
            catch (Exception) { /* not allowed on this system: carry on */ }
        }

        static long FreeBytes()
        {
            var info = GC.GetGCMemoryInfo();
            return Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
        }

        static string TempText()
        {
            try
            {
                const string path = "/sys/class/thermal/thermal_zone0/temp";   // Linux/Android only
                if (File.Exists(path))
                {
                    double c = double.Parse(File.ReadAllText(path).Trim()) / 1000.0;
                    return string.Format(" | {0:0.#} C", c);
                }
            }
            catch (Exception) { }
            return "";
        }

        static string Human(long v, bool bytes)
        {
            double d = v;
            double step = bytes ? 1024.0 : 1000.0;
            string[] units = bytes ? new[] { "B", "KB", "MB", "GB", "TB" } : new[] { "op", "Kop", "Mop", "Gop", "Top" };
            int u = 0;
            while (d >= step && u < units.Length - 1) { d /= step; u++; }
            return string.Format("{0:0.##} {1}", d, units[u]);
        }
    }
}
