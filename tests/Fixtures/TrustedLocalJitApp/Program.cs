using System;
using System.Runtime.CompilerServices;

public static class LocalJitFixture
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Compute(int[] input)
    {
        var result = 0;
        foreach (var item in input)
            result += (item * 17) ^ (item >> 3);
        return result;
    }

    public static int Main(string[] args)
    {
        var values = new int[512];
        for (var i = 0; i < values.Length; i++)
            values[i] = i;
        Console.WriteLine("Fixture=" + Compute(values));
        return 0;
    }
}
