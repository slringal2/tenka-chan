using Tenka.Sample.GameLab;
using Xunit;

public sealed class GameLabTests
{
    [Theory]
    [InlineData(1, 1)][InlineData(2, 1)][InlineData(17, 9)][InlineData(128, 64)][InlineData(600, 300)]
    public void InventoryMatchesKnownFixtureCounts(int size, int expected)
    {
        var (owned, requested) = GameData.Create(size);
        Assert.Equal(expected, InventorySearch.CountOwned(owned, requested));
    }
    [Fact] public void DuplicateRequestsCountSeparatelyAndNegativeItemsAreIgnored() =>
        Assert.Equal(2, InventorySearch.CountOwned([-1, 2, 2], [-1, 2, 2, 9]));
    [Fact] public void EmptyInputsAreValid()
    {
        Assert.Equal(0, InventorySearch.CountOwned([], []));
        Assert.Equal("", LootReport.Build([]));
        Assert.Equal(0L, DamageBuckets.SumBuckets([], 8));
    }
    [Fact] public void ReportMatchesExactContract() => Assert.Equal(
        "-42,0,1,2147483647,-2147483648,", LootReport.Build([-42, 0, 1, int.MaxValue, int.MinValue]));
    [Theory]
    [InlineData(1, 0L)][InlineData(2, 5L)][InlineData(8, 23L)][InlineData(1024, 1079L)][InlineData(1073741824, 1073741879L)]
    public void BucketSumsMatchKnownValues(int buckets, long expected) =>
        Assert.Equal(expected, DamageBuckets.SumBuckets([0, 1, 7, 8, 9, 31, int.MaxValue], buckets));
    [Theory][InlineData(0)][InlineData(-8)][InlineData(3)]
    public void InvalidBucketsAreRejected(int buckets) => Assert.Throws<ArgumentOutOfRangeException>(() => DamageBuckets.SumBuckets([1], buckets));
    [Fact] public void NegativeHitsAreRejected() => Assert.Throws<ArgumentOutOfRangeException>(() => DamageBuckets.SumBuckets([-1], 8));
    [Fact] public void NullInputsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => InventorySearch.CountOwned(null!, []));
        Assert.Throws<ArgumentNullException>(() => InventorySearch.CountOwned([], null!));
        Assert.Throws<ArgumentNullException>(() => LootReport.Build(null!));
        Assert.Throws<ArgumentNullException>(() => DamageBuckets.SumBuckets(null!, 8));
    }
    [Fact] public void CounterHasNoLostUpdates() => Assert.Equal(80, CounterContention.Run(20));
    [Fact] public void InvalidCliIsRejected()
    {
        foreach (var args in new[] { new[] { "--size", "3001" }, new[] { "--iterations", "0" }, new[] { "--seconds", "30" }, new[] { "--mode", "retired" }, new[] { "--size" }, new[] { "--size", "2", "--size", "3" } })
            Assert.Throws<ArgumentException>(() => Options.Parse(args));
    }
    [Fact] public void DurationAndIterationsCannotBeMixed()
    {
        Assert.Throws<ArgumentException>(() => Options.Parse(["--seconds", "4", "--iterations", "3"]));
        Assert.Equal(4, Options.Parse(["--seconds", "4"]).Seconds);
    }
    [Fact] public void SelfVerificationUsesActualImplementation() => Assert.Equal(0, GameLabProgram.Run(["--verify"]));
}
