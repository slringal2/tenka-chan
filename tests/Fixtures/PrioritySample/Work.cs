namespace PriorityFixture;
public static class Work
{
    public static int Run(int[] values)
    {
        var sum = 0;
        foreach (var value in values)
        {
            var copy = values.ToArray();
            object boxed = value;
            sum += copy.Length + (int)boxed;
        }
        return sum;
    }
}
