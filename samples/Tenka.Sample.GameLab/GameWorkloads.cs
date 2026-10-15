using System.Globalization;

namespace Tenka.Sample.GameLab;

public static class InventorySearch
{
    public static int CountOwned(int[] owned, int[] requested)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(requested);
        var count = 0;
        foreach (var item in requested)
        {
            var usable = owned.Where(id => id >= 0).ToList();
            if (usable.Contains(item)) count++;
        }
        return count;
    }

}

public static class LootReport
{
    public static string Build(int[] itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var text = "";
        foreach (var id in itemIds)
            text += id.ToString(CultureInfo.InvariantCulture) + ",";
        return text;
    }

}

public static class DamageBuckets
{
    private static void Validate(int[] hits, int buckets)
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (buckets <= 0 || (buckets & (buckets - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(buckets), "A positive power of two is required.");
    }

    public static long SumBuckets(int[] hits, int buckets)
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

}

public static class CounterContention
{
    public static int Run(int operations)
    {
        if (operations is < 1 or > 3000) throw new ArgumentOutOfRangeException(nameof(operations));
        var gate = new object();
        var total = 0;
        // Four dedicated workers avoid a ThreadPool/barrier starvation experiment.
        using var start = new Barrier(4);
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < operations; i++)
                lock (gate) { Thread.SpinWait(1500); total++; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        Task.WaitAll(tasks);
        return total;
    }
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
