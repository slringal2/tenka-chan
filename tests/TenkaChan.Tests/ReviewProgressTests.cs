using Microsoft.Data.Sqlite;
using TenkaChan.Mcp.Tracking;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ReviewProgressTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"tenka-review-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_root,"progress.sqlite");
    private ReviewTarget Target(string source="class C { void M() {} }",string context="ctx",string kind="method") =>
        new(ReviewProgress.TargetId(Path.Combine(_root,"Demo.csproj"),kind,"C.M"),"selection","method","p","Demo",
            Path.Combine(_root,"Demo.csproj"),"C.M",Path.Combine(_root,"C.cs"),1,ReviewProgress.Hash(source),context,true,null,source);
    private static ReviewMarkRequest Mark(ReviewTarget t,long revision=0,string status="checked")=>
        new("snapshot",t.Id,t.SourceHash,t.ContextHash,revision,status,"確認した範囲と未確認事項を記録しました。");
    private ReviewCheckpoint Save(ReviewProgressStore store,ReviewTarget t,long version=0,string status="checked")=>
        store.Save(t,Mark(t,version,status),"agent-a","session",[]);

    [Fact] public void UnreviewedIsNotInferredAsCheckedFromReading()
    {var state=ReviewProgress.State(Target(),null,false);Assert.Equal("unreviewed",state.Status);Assert.Equal(0,state.Revision);}
    [Theory][InlineData("checked")][InlineData("optimized")]
    public void PersistedMarksSurviveOpeningANewStore(string status)
    {
        var target=Target();var row=Save(new(Database),target,0,status);
        var reopened=new ReviewProgressStore(Database);var mark=reopened.Latest()[target.Id];
        Assert.Equal(row.Revision,mark.Revision);Assert.Equal(status,ReviewProgress.State(target,mark,false).Status);
        Assert.Equal(target.Source,reopened.Read(target.Id,row.Revision).Source);
    }
    [Fact] public void ChangedCodeInvalidatesOptimizedWithoutDestroyingHistory()
    {
        var db=new ReviewProgressStore(Database);var t=Target();var m=Save(db,t,0,"optimized");
        var state=ReviewProgress.State(Target("class C { void M(){ Work(); } }"),m,false);
        Assert.Equal("needs_review",state.Status);Assert.Equal("optimized",state.PreviousStatus);Assert.Equal("source_changed",state.Reason);
        Assert.Single(db.History(t.Id));
    }
    [Fact] public void ChangedContextRequiresRecheckEvenWhenMethodTextIsIdentical()
    {
        var t=Target();var m=Save(new(Database),t);var state=ReviewProgress.State(t with {ContextHash="new"},m,false);
        Assert.Equal("needs_review",state.Status);Assert.False(state.SourceChanged);Assert.True(state.ContextChanged);Assert.Equal("context_changed",state.Reason);
    }
    [Fact] public void StaleLoadedSourceCannotReceiveAMark()
    {var t=Target();Assert.Throws<ReviewConflictException>(()=>ReviewProgress.ValidateMark(t,Mark(t),true));}
    [Fact] public void BothContentAndContextHashesMustMatch()
    {
        var t=Target();Assert.Throws<ReviewConflictException>(()=>ReviewProgress.ValidateMark(t,Mark(t) with {SourceHash="other"},false));
        Assert.Throws<ReviewConflictException>(()=>ReviewProgress.ValidateMark(t,Mark(t) with {ContextHash="other"},false));
    }
    [Theory][InlineData("verified")][InlineData("fixed")][InlineData("needs_review")][InlineData("")]
    public void UnsupportedStatusesCannotBeWritten(string status)
    {var t=Target();Assert.Throws<ArgumentException>(()=>Save(new(Database),t,0,status));}
    [Fact] public void ReviewingAfterChangeCreatesNewVersionAndRestoresCurrentState()
    {
        var db=new ReviewProgressStore(Database);var t=Target();var a=Save(db,t);var next=Target("class C { void M(){ Work(); } }","new-context");
        var b=Save(db,next,a.Revision,"optimized");Assert.Equal("optimized",ReviewProgress.State(next,b,false).Status);
        Assert.Equal(2,db.History(t.Id).Count);Assert.Equal(t.Source,db.Read(t.Id,a.Revision).Source);
    }
    [Fact] public void StaleWriterDoesNotOverwriteANewerAgent()
    {
        var db=new ReviewProgressStore(Database);var t=Target();var a=Save(db,t);
        Assert.Throws<ReviewConflictException>(()=>Save(new(Database),t,0,"optimized"));
        Assert.Equal(a.Revision,db.Latest()[t.Id].Revision);Assert.Single(db.History(t.Id));
    }
    [Fact] public void ExplicitResetKeepsPriorCheckedSource()
    {
        var db=new ReviewProgressStore(Database);var t=Target();var a=Save(db,t);var b=Save(db,t,a.Revision,"unreviewed");
        Assert.Equal("unreviewed",ReviewProgress.State(t,b,false).Status);Assert.Equal(2,db.History(t.Id).Count);
    }
    [Fact] public void DifferentTargetsCannotReadEachOthersSourceRevisions()
    {
        var db=new ReviewProgressStore(Database);var t=Target();var m=Save(db,t);
        Assert.Throws<KeyNotFoundException>(()=>db.Read("different-target",m.Revision));
    }
    [Fact] public void IdentityDoesNotDependOnSnapshotButDoesDependOnProjectAndKind()
    {
        var path=Path.Combine(_root,"p.csproj");
        Assert.Equal(ReviewProgress.TargetId(path,"method","M:N.C.Run(System.Int32)"),ReviewProgress.TargetId(path,"method","M:N.C.Run(System.Int32)"));
        Assert.NotEqual(ReviewProgress.TargetId(path,"type","X"),ReviewProgress.TargetId(path,"method","X"));
        Assert.NotEqual(ReviewProgress.TargetId(path,"method","M:X(int)"),ReviewProgress.TargetId(path,"method","M:X(string)"));
        Assert.NotEqual(ReviewProgress.TargetId(path,"type","X"),ReviewProgress.TargetId(Path.Combine(_root,"other.csproj"),"type","X"));
    }
    [Fact] public void EquivalentLineEndingsDoNotInvalidateCheckpoints()
    {Assert.Equal(ReviewProgress.Hash("a\r\nb\r\n"),ReviewProgress.Hash("a\nb\n"));}
    [Fact] public void EmptySummaryAndTruncatedSubjectsAreNotAccepted()
    {
        var t=Target();Assert.Throws<ArgumentException>(()=>ReviewProgress.ValidateMark(t,Mark(t) with {Summary=" "},false));
        Assert.Throws<InvalidOperationException>(()=>ReviewProgress.ValidateMark(t with {Trackable=false,UnavailableReason="too large"},Mark(t),false));
    }
    [Fact] public void RecordedEvidenceMetadataSurvivesSessionLifetimes()
    {
        var db=new ReviewProgressStore(Database);var t=Target();var evidence=new ReviewEvidenceLink("instance","call","run_companion","returned","tests_passed","2026-01-01T00:00:00Z");
        var m=db.Save(t,Mark(t),"external-agent","old-session",[evidence]);
        Assert.Equal(evidence,Assert.Single(new ReviewProgressStore(Database).Read(t.Id,m.Revision).Checkpoint.Evidence));
    }
    [Fact] public void IdenticalSourcesHaveNoDiff()
    {var d=ReviewSourceDiff.Compare("a\nb","a\nb");Assert.Empty(d.Rows);Assert.Equal(0,d.Added+d.Removed);}
    [Fact] public void ReplacementShowsBothLineNumbersAndSigns()
    {
        var d=ReviewSourceDiff.Compare("a\nb\nc","a\nx\nc");Assert.Equal(1,d.Added);Assert.Equal(1,d.Removed);
        Assert.Contains(d.Rows,r=>r.Kind=="remove"&&r.OldLine==2&&r.Text=="b");
        Assert.Contains(d.Rows,r=>r.Kind=="add"&&r.NewLine==2&&r.Text=="x");
    }
    [Theory][InlineData("","x",1,0)][InlineData("x","",0,1)][InlineData("a","a\nb",1,0)]
    public void AddAndRemoveBoundariesAreCorrect(string before,string after,int add,int remove)
    {var d=ReviewSourceDiff.Compare(before,after);Assert.Equal(add,d.Added);Assert.Equal(remove,d.Removed);}
    [Fact] public void LargeDiffUsesExplicitBoundedFallback()
    {
        var d=ReviewSourceDiff.Compare(string.Join("\n",Enumerable.Repeat("a",1100)),string.Join("\n",Enumerable.Repeat("b",1100)));
        Assert.Equal("bounded_replacement",d.Mode);Assert.True(d.HasMore);Assert.Equal(1100,d.Added);Assert.Equal(1100,d.Removed);
    }
    [Fact] public void PagedDiffDoesNotDropChanges()
    {
        var a=string.Join("\n",Enumerable.Range(0,100).Select(i=>"old"+i));var b=a.Replace("old","new");
        var all=ReviewSourceDiff.Compare(a,b,0,500);var parts=new List<ReviewDiffLine>();
        for(int offset=0;offset<all.TotalRows;offset+=30)parts.AddRange(ReviewSourceDiff.Compare(a,b,offset,30).Rows);
        Assert.Equal(all.Rows,parts);
    }
    [Fact] public void DiffRejectsExcessivePageSizes()
    {Assert.Throws<ArgumentException>(()=>ReviewSourceDiff.Compare("a","b",0,501));}
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
