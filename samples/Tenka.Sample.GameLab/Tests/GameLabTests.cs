using Tenka.Sample.GameLab;
using Xunit;

public sealed class GameLabTests
{
    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(17)][InlineData(128)][InlineData(600)]
    public void InventoryVersionsAgree(int size)
    {
        var (owned, requested) = GameData.Create(size);
        Assert.Equal(InventorySearch.CountOwnedSlow(owned, requested), InventorySearch.CountOwnedFast(owned, requested));
    }
    [Fact] public void DuplicateRequestsCountSeparatelyAndNegativeItemsAreIgnored()
    {
        Assert.Equal(2, InventorySearch.CountOwnedSlow([-1, 2, 2], [-1, 2, 2, 9]));
        Assert.Equal(2, InventorySearch.CountOwnedFast([-1, 2, 2], [-1, 2, 2, 9]));
    }
    [Fact] public void EmptyInventoryAndReportAreValid()
    {
        Assert.Equal(0, InventorySearch.CountOwnedSlow([], []));
        Assert.Equal(0, InventorySearch.CountOwnedFast([], []));
        Assert.Equal("", LootReport.BuildSlow([])); Assert.Equal("", LootReport.BuildFast([]));
    }
    [Fact] public void ReportsAgreeExactlyNotJustInLength()
    {
        int[] input = [-42, 0, 1, int.MaxValue, int.MinValue];
        Assert.Equal("-42,0,1,2147483647,-2147483648,", LootReport.BuildSlow(input));
        Assert.Equal(LootReport.BuildSlow(input), LootReport.BuildFast(input));
    }
    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(8)][InlineData(1024)][InlineData(1073741824)]
    public void ModuloAndMaskHaveTheSameContract(int buckets)
    {
        int[] hits = [0, 1, 7, 8, 9, 31, int.MaxValue];
        Assert.Equal(DamageBuckets.SumModulo(hits, buckets), DamageBuckets.SumMask(hits, buckets));
    }
    [Theory][InlineData(0)][InlineData(-8)][InlineData(3)]
    public void NonPowerOfTwoBucketsAreRejectedByBoth(int buckets)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DamageBuckets.SumModulo([1], buckets));
        Assert.Throws<ArgumentOutOfRangeException>(() => DamageBuckets.SumMask([1], buckets));
    }
    [Fact] public void NegativeHitsAreRejectedByBoth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DamageBuckets.SumModulo([-1], 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => DamageBuckets.SumMask([-1], 8));
    }
    [Fact] public void NullContractsMatch()
    {
        Assert.Throws<ArgumentNullException>(() => InventorySearch.CountOwnedSlow(null!, []));
        Assert.Throws<ArgumentNullException>(() => InventorySearch.CountOwnedFast(null!, []));
        Assert.Throws<ArgumentNullException>(() => LootReport.BuildSlow(null!));
        Assert.Throws<ArgumentNullException>(() => LootReport.BuildFast(null!));
        Assert.Throws<ArgumentNullException>(() => DamageBuckets.SumModulo(null!, 8));
        Assert.Throws<ArgumentNullException>(() => DamageBuckets.SumMask(null!, 8));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void CounterHasNoLostUpdates(bool batched) => Assert.Equal(80, CounterContention.Run(batched, 20));
    [Fact] public void InvalidCliCannotCreateUnboundedWork()
    {
        foreach (var args in new[] { new[] { "--size", "3001" }, new[] { "--iterations", "0" }, new[] { "--seconds", "30" }, new[] { "--mode", "wrong" }, new[] { "--size" }, new[] { "--size", "2", "--size", "3" } })
            Assert.Throws<ArgumentException>(() => Options.Parse(args));
    }
    [Fact] public void ProfileModeIsExplicit()
    {
        Assert.Throws<ArgumentException>(() => Options.Parse(["--seconds", "4"]));
        var options = Options.Parse(["--mode", "baseline", "--seconds", "4"]);
        Assert.Equal(4, options.Seconds);
    }
    [Fact] public void SelfVerificationUsesActualImplementations() => Assert.Equal(0, GameLabProgram.Run(["--verify"]));
}
