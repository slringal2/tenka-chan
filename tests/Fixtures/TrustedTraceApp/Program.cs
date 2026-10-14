using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace TraceFixture;

public static class Workload
{
    public static void Main(string[] args)
    {
        if (args.Contains("--sleep")) { Thread.Sleep(20000); return; }
        var waiting = new Thread(() => Thread.Sleep(3000)) { IsBackground = true, Name = "Tenka-waiting" };
        waiting.Start();
        Console.WriteLine(Busy());
        waiting.Join();
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static long Busy()
    {
        var clock = Stopwatch.StartNew();
        long value = 7;
        while (clock.ElapsedMilliseconds < 2500)
            for (var i = 0; i < 100000; i++) value = unchecked(value * 31 + i);
        return value;
    }
}
