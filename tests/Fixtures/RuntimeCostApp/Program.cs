using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace RuntimeCostFixture;
public static class Workload
{
    private static readonly object Gate = new();
    private static long _sink;
    public static void Main(string[] args)
    {
        Busy();
        if (args.Contains("--idle")) { Thread.Sleep(300); Console.WriteLine("idle"); return; }
        Allocate();
        ObserveExceptions();
        for (var i = 0; i < 4; i++)
        {
            using var ready = new ManualResetEventSlim();
            var worker = new Thread(() => { ready.Set(); lock (Gate) { Interlocked.Increment(ref _sink); } });
            lock (Gate)
            {
                worker.Start();
                if (!ready.Wait(3000)) throw new TimeoutException("Fixture worker failed to start.");
                Thread.Sleep(50);
            }
            if (!worker.Join(3000)) throw new TimeoutException("Fixture worker failed to complete.");
        }
        Busy();
        Console.WriteLine("runtime-cost-fixture=" + _sink);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ObserveExceptions()
    {
        // First-chance throws are expected and caught; the reader MUST NOT
        // classify them as application failures or leak the message text.
        for (var i = 0; i < 6; i++)
        {
            try { throw new InvalidOperationException("PRIVATE-FIXTURE-MESSAGE-DO-NOT-EXPOSE"); }
            catch (InvalidOperationException) { Interlocked.Increment(ref _sink); }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Allocate()
    {
        var retained = new byte[64][];
        for (var pass = 0; pass < 4; pass++)
        {
            for (var i = 0; i < 512; i++) retained[i % retained.Length] = new byte[32768];
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        GC.KeepAlive(retained);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Busy()
    {
        var timer = Stopwatch.StartNew(); long n = 1;
        while (timer.ElapsedMilliseconds < 100)
            for (var i = 0; i < 10000; i++) n = unchecked(n * 31 + i);
        Volatile.Write(ref _sink, n);
    }
}
