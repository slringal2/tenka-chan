using System.Text.Json;
using TenkaChan.Mcp.Agent;
using Xunit;
namespace TenkaChan.Tests;

public sealed class ReviewAttributionTests
{
    [Fact] public void PersistableAttributionKeepsFailedTestStatusWithoutPromotingIt()
    {
        var journal=new AgentJournal();var session=journal.Begin("review-agent","");
        var call=journal.Start(new(session.Id,Guid.NewGuid().ToString("N"),"run_companion",JsonSerializer.SerializeToElement(new {})));
        journal.Complete(call.CallId,new {status="tests_failed"});
        var copied=journal.ReviewAttribution(session.Id,[call.CallId]);
        Assert.Equal("review-agent",copied.Agent);
        var item=Assert.Single(copied.Evidence);Assert.Equal("tests_failed",item.ObservedStatus);
        Assert.Equal(journal.InstanceId,item.InstanceId);Assert.Equal("returned",item.Outcome);
    }
    [Fact] public void CheckpointEvidenceCannotReferToAnotherAgentsSession()
    {
        var journal=new AgentJournal();var a=journal.Begin("a","");var b=journal.Begin("b","");
        var call=journal.Start(new(a.Id,Guid.NewGuid().ToString("N"),"inspect_method",JsonSerializer.SerializeToElement(new {})));
        journal.Complete(call.CallId,new {});
        Assert.Throws<ArgumentException>(()=>journal.ReviewAttribution(b.Id,[call.CallId]));
    }
}
