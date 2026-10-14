using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Tenka.Sample.GameLab;

// These intentionally inefficient examples are teaching material, not production guidance.
// Keep the entry methods visible in a focused JIT dump. Do NOT disable optimization.
public static class InventorySearch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int CountOwnedSlow(int[] owned, int[] requested)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(requested);
        var count = 0;
        foreach (var item in requested)
        {
            // Rebuild the same list per lookup, then search it linearly.
            var usable = owned.Where(id => id >= 0).ToList();
            if (usable.Contains(item)) count++;
        }
        return count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int CountOwnedFast(int[] owned, int[] requested)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(requested);
        // The lookup construction IS included in each operation/benchmark.
        var usable = new HashSet<int>(owned.Where(id => id >= 0));
        var count = 0;
        foreach (var item in requested)
            if (usable.Contains(item)) count++;
        return count;
    }
}

public static class LootReport
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string BuildSlow(int[] itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var text = "";
        foreach (var id in itemIds)
            text += id.ToString(CultureInfo.InvariantCulture) + ",";
        return text;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string BuildFast(int[] itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var text = new StringBuilder();
        foreach (var id in itemIds)
            text.Append(id.ToString(CultureInfo.InvariantCulture)).Append(',');
        return text.ToString();
    }
}

public static class DamageBuckets
{
    // BOTH versions require nonnegative hits and a power-of-two bucket count.
    // Bit masking is NOT a semantics-preserving replacement for arbitrary signed %.
    private static void Validate(int[] hits, int buckets)
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (buckets <= 0 || (buckets & (buckets - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(buckets), "A positive power of two is required.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long SumModulo(int[] hits, int buckets)
    {
        Validate(hits, buckets);
        long sum = 0;
        foreach (var hit in hits)
        {
            if (hit < 0) throw new ArgumentOutOfRangeException(nameof(hits));
            sum += hit % buckets;
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long SumMask(int[] hits, int buckets)
    {
        Validate(hits, buckets);
        var mask = buckets - 1;
        long sum = 0;
        foreach (var hit in hits)
        {
            if (hit < 0) throw new ArgumentOutOfRangeException(nameof(hits));
            sum += hit & mask;
        }
        return sum;
    }
}

public static class CounterContention
{
    // Optional trace lesson. SpinWait is deliberately independent work; moving it
    // outside the lock does not change the counter contract in this example.
    public static int Run(bool batched, int operations)
    {
        if (operations is < 1 or > 3000) throw new ArgumentOutOfRangeException(nameof(operations));
        var gate = new object();
        var total = 0;
        // Four dedicated workers avoid a ThreadPool/barrier starvation experiment.
        using var start = new Barrier(4);
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            if (batched)
            {
                var local = 0;
                for (var i = 0; i < operations; i++) { Thread.SpinWait(1500); local++; }
                lock (gate) total += local;
            }
            else
            {
                for (var i = 0; i < operations; i++)
                    lock (gate) { Thread.SpinWait(1500); total++; }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        Task.WaitAll(tasks);
        return total;
    }
}

public static class AsyncLoadingExamples
{
    // Static-analysis exercise ONLY: not run by the sample entry point or benchmarks.
    public static async Task<int> BlockingAsync()
    {
        var result = ReadLocalCountAsync().Result;
        Thread.Sleep(2);
        await Task.Yield();
        return result;
    }
    public static async Task<int> AwaitedAsync() => await ReadLocalCountAsync();
    private static async Task<int> ReadLocalCountAsync() { await Task.Yield(); return 42; }
}

public static class GameData
{
    public static (int[] Owned, int[] Requested) Create(int size)
    {
        if (size is < 1 or > 3000) throw new ArgumentOutOfRangeException(nameof(size));
        // Stable data across processes/runtimes: no wall clock, random seed or file I/O.
        var owned = Enumerable.Range(0, size).Select(i => i / 2).ToArray();
        var requested = Enumerable.Range(0, size).Select(i => (i * 37) % size).ToArray();
        return (owned, requested);
    }
}
