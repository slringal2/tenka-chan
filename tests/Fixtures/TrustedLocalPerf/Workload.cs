using System;

public static class Workload
{
    public static int Main(string[] args)
    {
        var checksum = 0;
        for (var i = 0; i < 100_000; i++)
            checksum = unchecked(checksum + ((i * 17) ^ (i >> 3)));
        Console.WriteLine("checksum=" + checksum);
        return 0;
    }
}
