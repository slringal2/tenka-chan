using System.Runtime.CompilerServices;

// Small, deterministic trusted example for build-output discovery and JIT capture.
Console.WriteLine(SampleWork.SumSquares(new[] { 1, 2, 3, 4 }));

public static class SampleWork
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long SumSquares(int[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        long result = 0;
        foreach (var value in values) result += (long)value * value;
        return result;
    }
}
