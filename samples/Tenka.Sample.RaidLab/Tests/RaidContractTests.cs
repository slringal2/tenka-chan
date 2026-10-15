using Tenka.Sample.RaidLab.Verification;
using Xunit;

namespace Tenka.Sample.RaidLab.Tests;

public sealed class RaidContractTests
{
    public static IEnumerable<object[]> Cases => ContractChecks.Names.Select(n => new object[] { n });
    [Theory]
    [MemberData(nameof(Cases))]
    public Task Contract(string name) => ContractChecks.RunAsync(name);
}
