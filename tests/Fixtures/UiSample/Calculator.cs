using System.Collections.Generic;
using System.Linq;

namespace UiSample;

public static class Calculator
{
    public static int Add(int a, int b) => a + b;

    public static int CountCopies(IEnumerable<int> items)
    {
        var total = 0;
        for (var i = 0; i < 3; i++)
        {
            var snapshot = items.ToList();
            total += snapshot.Count;
        }
        return total;
    }
}
