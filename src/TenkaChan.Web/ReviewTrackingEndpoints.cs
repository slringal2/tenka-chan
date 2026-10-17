using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;

namespace TenkaChan.Web;

public static class ReviewTrackingEndpoints
{
    public static void MapReviewTrackingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/review-progress/queue", (ReviewTrackingService service,string? snapshotId,
            string? projectId,string? kind,string? state,string? q,int? offset,int? limit,string? cursor,bool? trackableOnly,CancellationToken ct) =>
            Safe(()=>service.Queue(snapshotId,projectId,kind??ReviewQueueViews.DefaultKind,state??"pending",q,offset??0,limit??80,ct,cursor,trackableOnly??false)));
        app.MapGet("/api/review-progress/target", (ReviewTrackingService service,string snapshotId,string targetId,CancellationToken ct) =>
            Safe(()=>service.Target(snapshotId,targetId,ct)));
        app.MapGet("/api/review-progress/diff", (ReviewTrackingService service,string snapshotId,string targetId,
            long? fromRevision,long? toRevision,int? offset,int? limit,CancellationToken ct) =>
            Safe(()=>service.Diff(snapshotId,targetId,fromRevision,toRevision,offset??0,limit??150,ct)));
        app.MapGet("/api/review-progress/badges", (ReviewTrackingService service,string snapshotId,string ids,CancellationToken ct) =>
            Safe(()=>service.Badges(snapshotId,ids.Split(','),ct)));
        // Intentionally no browser write endpoint. External agents record work through authenticated MCP.
    }
    private static async Task<IResult> Safe(Func<Task<object>> run)
    {
        try {return Results.Ok(await run());}
        catch(OperationCanceledException){return Results.Json(new {error="確認状態の取得を中止しました。"},statusCode:408);}
        catch(UnauthorizedAccessException){return Results.Json(new {error="許可されたプロジェクトで確認してください。"},statusCode:403);}
        catch(Exception ex) when(ex is ReviewConflictException or WorkspaceBuildConflictException)
        {return Results.Json(new {error=ex.Message},statusCode:409);}
        catch(Exception ex) when(ex is ArgumentException or KeyNotFoundException or InvalidOperationException or IOException)
        {return Results.Json(new {error=ex.Message},statusCode:400);}
    }
}

public sealed class ReviewTrackingService(DashboardState dashboard,WorkspaceGuard guard,ReviewProgressStore store)
{
    private readonly ReviewDiffCache _diffCache = new();
    private Task<object> With(string? snapshot,Func<ReviewSourceCatalog,Task<object>> run,CancellationToken ct,bool waitForRead=true) =>
        dashboard.ExploreAsync(snapshot,async(index,_)=>await run(await index.ReviewCatalogAsync(guard,ct)),ct,waitForRead:waitForRead);
    private async Task<ReviewQueueItem[]> Items(ReviewSourceCatalog catalog,CancellationToken ct)
    {
        var marks=store.Latest();var stale=await catalog.StaleProjectsAsync(guard,false,ct);
        return catalog.Targets.Select(t=>new ReviewQueueItem(t,ReviewProgress.State(t,marks.GetValueOrDefault(t.Id),stale.Contains(t.ProjectId)))).ToArray();
    }
    public Task<object> Queue(string? snapshot,string? project,string kind,string state,string? query,int offset,int limit,CancellationToken ct,string? cursor=null,bool trackableOnly=false) =>
        With(snapshot,async catalog => (object)ReviewQueueViews.Build(catalog.SnapshotId,
            await Items(catalog,ct),project,kind,state,query,offset,limit,cursor,trackableOnly,catalog.Limited),ct);
    public Task<object> Target(string snapshot,string targetId,CancellationToken ct) => With(snapshot,async catalog=>
    {
        var target=catalog.Require(targetId);var stale=await catalog.StaleProjectsAsync(guard,false,ct);
        var history=store.History(target.Id);var state=ReviewProgress.State(target,history.FirstOrDefault(),stale.Contains(target.ProjectId));
        return new {snapshotId=catalog.SnapshotId,target,state,history,note=ReviewProgress.Note};
    },ct);
    public Task<object> Badges(string snapshot,string[] ids,CancellationToken ct) => With(snapshot,async catalog=>
    {
        if(ids.Length>200||ids.Any(id=>id.Length>100))throw new ArgumentException("At most 200 selection IDs are supported.");
        var wanted=ids.ToHashSet(StringComparer.Ordinal);
        return new {snapshotId=catalog.SnapshotId,items=(await Items(catalog,ct)).Where(r=>wanted.Contains(r.Target.SelectionId))
            .Select(r=>new {selectionId=r.Target.SelectionId,targetId=r.Target.Id,r.State,r.Target.Trackable,r.Target.UnavailableReason}).ToArray()};
    },ct);
    public Task<object> History(string snapshot,string targetId,int offset,int limit,CancellationToken ct) => With(snapshot,catalog=>
    {
        var target=catalog.Require(targetId);
        return Task.FromResult((object)new {snapshotId=catalog.SnapshotId,targetId=target.Id,offset,items=store.History(target.Id,offset,limit),note=ReviewProgress.Note});
    },ct);
    public Task<object> Diff(string snapshot,string targetId,long? from,long? to,int offset,int limit,CancellationToken ct) => With(snapshot,async catalog=>
    {
        var target=catalog.Require(targetId);
        if(!target.Trackable)throw new InvalidOperationException(target.UnavailableReason);
        if(to is null && (await catalog.StaleProjectsAsync(guard,false,ct)).Contains(target.ProjectId))
            throw new ReviewConflictException("読み込み後にファイルが変わりました。現在のコードとの差分を見るにはプロジェクトを再読み込みしてください。保存履歴同士の比較は可能です。");
        var leftRevision=from??store.History(target.Id,0,1).FirstOrDefault()?.Revision;
        var left=leftRevision is long l?store.Read(target.Id,l):((ReviewCheckpoint?)null,"");
        var right=to is long r?store.Read(target.Id,r):((ReviewCheckpoint?)null,target.Source);
        return new {snapshotId=catalog.SnapshotId,targetId,hasBaseline=leftRevision is not null,
            fromRevision=leftRevision,toRevision=to,fromHash=left.Item1?.SourceHash,
            toHash=right.Item1?.SourceHash??target.SourceHash,
            diff=_diffCache.Compare(left.Item2,right.Item2,offset,limit),
            note=leftRevision is null?"未確認の対象です。比較元がないため、表示は現在のソース全体です。変更量とはみなしません。":ReviewProgress.Note};
    },ct);
    public Task<object> Mark(ReviewMarkRequest request,string agent,string session,IReadOnlyList<ReviewEvidenceLink> evidence,CancellationToken ct,
        Func<TenkaChan.Mcp.Tracking.ReviewTarget, ReviewBenchmarkEvidence?>? measurementFactory=null) =>
        With(request.SnapshotId,async catalog=>
        {
            var target=catalog.Require(request.TargetId);
            var stale=await catalog.StaleProjectsAsync(guard,true,ct);
            ReviewProgress.ValidateMark(target,request,stale.Contains(target.ProjectId));
            ct.ThrowIfCancellationRequested();
            var measuredRequest = request with { Measurement = measurementFactory?.Invoke(target) };
            if (measuredRequest.Measurement is not null)
                ReviewProgress.ValidateMark(target,measuredRequest,(await catalog.StaleProjectsAsync(guard,true,ct)).Contains(target.ProjectId));
            ct.ThrowIfCancellationRequested();
            var checkpoint=store.Save(target,measuredRequest,agent,session,evidence);
            return new {snapshotId=catalog.SnapshotId,targetId=target.Id,status="review_mark_recorded",checkpoint,
                state=ReviewProgress.State(target,checkpoint,false),note=ReviewProgress.Note};
        },ct,waitForRead:false);
}
