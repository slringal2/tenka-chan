"use strict";
(function(global){
  const labels={unreviewed:"未確認",checked:"チェック済み",optimized:"最適化済み",needs_review:"要再確認"};
  const reasons={not_checked:"まだ確認の記録がありません。",current:"確認した版と一致しています。",source_changed:"確認後にこの対象のソースが変わっています。",context_changed:"対象本体は同じでも、同じプロジェクトまたは参照先のソース・明示的なプロジェクト設定が変わっています。",snapshot_outdated:"読み込み後にファイルが変わっています。プロジェクトを再読み込みしてください。"};
  function label(state){return labels[state]||"状態不明";}
  function queueRequest(filters,current,append,loadedCount=0){
    if(!append)return {...filters,offset:0};
    if(!current?.snapshotId)throw new Error("先に確認一覧を読み込んでください。");
    if(current.hasMore===false)return null;
    if(typeof current.nextCursor==="string"&&current.nextCursor.length>0)
      return {...filters,snapshotId:current.snapshotId,cursor:current.nextCursor,offset:0};
    if(current.hasMore===true)throw new Error("一覧の続き情報がありません。表示を更新してください。");
    // Older servers do not advertise cursor support. Keep their read-only UI compatible.
    return {...filters,snapshotId:current.snapshotId,offset:loadedCount};
  }
  function mergeQueueRows(previous,incoming,append){
    const rows=new Map((append?previous:[]).map(r=>[r.target.id,r]));
    for(const row of incoming)rows.set(row.target.id,row);
    return [...rows.values()];
  }
  class ReviewProgressClient {
    constructor(fetcher){this.fetcher=fetcher;this.epoch=0;this.data=null;}
    async read(route,params={}){
      if(!["queue","target","diff","badges"].includes(route))throw new Error("Unsupported review read route.");
      const q=new URLSearchParams();for(const [k,v]of Object.entries(params))if(v!==null&&v!==undefined)q.set(k,String(v));
      const response=await this.fetcher("/api/review-progress/"+route+"?"+q,{method:"GET",credentials:"same-origin",cache:"no-store",headers:{Accept:"application/json"}});
      let data;try{data=await response.json();}catch{throw new Error("確認記録の応答を読み取れませんでした。");}
      if(!response.ok)throw new Error(data.error||"確認記録を取得できませんでした。");
      return data;
    }
    async queue(filters={}){
      const epoch=++this.epoch;const data=await this.read("queue",filters);
      if(epoch!==this.epoch)return null;
      if(!data?.snapshotId||!Array.isArray(data.items)||!data.summary)throw new Error("確認一覧の形式が不正です。");
      if(filters.cursor&&data.snapshotId!==filters.snapshotId)
        throw new Error("ソースが更新されました。古い一覧の続きは追加せず、先頭から更新してください。");
      this.data=data;return data;
    }
  }
  function start(){
    const $=id=>document.getElementById(id),client=new ReviewProgressClient((...args)=>fetch(...args));
    let rows=[],selection=null,detail=null,diff=null,diffRows=[],busy=false,pendingRefresh=false,detailEpoch=0,timer,queryTimer;
    const el=(tag,text,cls)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=String(text);if(cls)n.className=cls;return n;};
    const badge=state=>{const b=el("span",label(state),"review-badge");b.dataset.reviewState=state;return b;};
    let initialTarget=new URLSearchParams(location.search).get("targetId");
    if(initialTarget){$("review-kind").value="all";$("review-filter").value="all";}
    function renderRows(){
      const list=$("review-list");list.replaceChildren();
      for(const row of rows){const t=row.target,s=row.state,b=el("button",undefined,"review-choice");b.type="button";b.setAttribute("aria-pressed",String(selection===t.id));
        b.append(el("strong",t.name),el("small",t.project+" / "+(t.kind==="type"?"型":"メソッド")),badge(s.status));
        if(s.status==="needs_review")b.append(el("small",s.reason==="source_changed"?"本体のソース変更":s.reason==="snapshot_outdated"?"再読み込みが必要":"周辺コードの変更"));
        if(!t.trackable)b.append(el("small","個別記録の対象外"));
        b.addEventListener("click",()=>select(t.id));list.append(b);
      }
      if(!rows.length)list.append(el("p","この条件に一致する対象はありません。『すべて』で確認済みの対象も表示できます。"));
      $("review-list-count").textContent=`表示済み ${rows.length}件 / 現在の条件一致 ${client.data?.total??0}件`;
      $("review-more").hidden=client.data?.hasMore!==undefined ? !client.data.hasMore : rows.length>=(client.data?.total??0);
    }
    async function load(append=false){
      if(busy||document.hidden)return;busy=true;
      try{
        const old=client.data?.snapshotId;
        const request=queueRequest({kind:$("review-kind").value,state:$("review-filter").value,q:$("review-query").value,limit:80},client.data,append,rows.length);
        if(!request)return;
        const data=await client.queue(request);
        if(!data)return;
        if(old&&old!==data.snapshotId){selection=null;detail=null;detailEpoch++;$("review-detail").hidden=true;}
        rows=mergeQueueRows(rows,data.items,append);renderRows();
        $("review-status").textContent=data.limited?"索引が上限に達しています。表示範囲内の記録です。":"確認記録を表示しています。閲覧だけではチェックは付きません。";
        if(data.summary.blockedPending>0)$("review-status").textContent+=` ${data.summary.blockedPending}件は再読み込みが必要、または個別記録の対象外です。完了扱いにはしていません。`;
        $("review-reload").hidden=!data.requiresReload;$("review-scope").textContent=data.scope;
        const counts=$("review-counts");counts.replaceChildren();for(const [key,text]of [["unreviewed","未確認"],["checkedCount","チェック済み"],["optimized","最適化済み"],["needsReview","要再確認"]]){const n=el("div");n.append(el("span",text),el("strong",data.summary[key]??0));counts.append(n);}
        if(selection&&detail){const updated=data.items.find(r=>r.target.id===selection);if(updated&&(updated.state.revision!==detail.state.revision||updated.state.reason!==detail.state.reason))await select(selection);}
        if(!selection&&initialTarget){const id=initialTarget;initialTarget=null;await select(id);}
      }catch(error){$("review-status").textContent=error.message+" プロジェクトを読み込んでから表示を更新してください。";}
      finally{busy=false;if(pendingRefresh){pendingRefresh=false;load();}}
    }
    async function select(id){
      if(!client.data)return;const epoch=++detailEpoch;selection=id;renderRows();
      $("review-detail").hidden=true;$("review-diff-body").replaceChildren();$("review-diff-more").hidden=true;diffRows=[];
      try{
        const value=await client.read("target",{snapshotId:client.data.snapshotId,targetId:id});if(epoch!==detailEpoch)return;
        detail=value;$("review-detail").hidden=false;const t=value.target,s=value.state;
        $("review-target-kind").textContent=t.kind==="type"?"CLASS / TYPE":"METHOD";$("review-target-name").textContent=t.name;
        $("review-target-status").replaceChildren(badge(s.status));$("review-reason").textContent=reasons[s.reason]||s.reason;
        $("review-attribution").textContent=s.checkpoint?`${new Date(s.checkpoint.recordedUtc).toLocaleString("ja-JP")} / ${s.checkpoint.agent}（エージェント申告名） / 記録 #${s.revision}`:"確認者の記録はまだありません。";
        $("review-summary").textContent=s.checkpoint?.summary||"";
        $("review-evidence").replaceChildren();for(const evidence of s.checkpoint?.evidence||[])$("review-evidence").append(el("p",`${evidence.tool}：${evidence.observedStatus||evidence.outcome}（参考にした呼出し。対象との一致・改善の証明は別途必要）`,"review-muted"));
        const prior=$("review-before").value,next=$("review-after").value;
        $("review-before").replaceChildren();$("review-after").replaceChildren(new Option("読み込み済みのソース",""));
        if(!value.history.length)$("review-before").add(new Option("比較元なし（未確認）",""));
        for(const h of value.history){const text=`#${h.revision} ${label(h.status)} / ${new Date(h.recordedUtc).toLocaleString("ja-JP")}`;$("review-before").add(new Option(text,String(h.revision)));$("review-after").add(new Option(text,String(h.revision)));}
        if(value.history.some(h=>String(h.revision)===prior))$("review-before").value=prior;
        if(value.history.some(h=>String(h.revision)===next))$("review-after").value=next;
        $("review-history").textContent=JSON.stringify({targetId:t.id,sourceHash:t.sourceHash,contextHash:t.contextHash,state:s,history:value.history},null,2);
        $("review-compare").disabled=!t.trackable;
        if(!t.trackable)$("review-diff-status").textContent=t.unavailableReason;else await compare(false,epoch);
      }catch(error){if(epoch===detailEpoch)$("review-diff-status").textContent=error.message;}
    }
    async function compare(append=false,expected=detailEpoch){
      if(!detail||!detail.target.trackable)return;
      const epoch=expected,from=$("review-before").value,to=$("review-after").value;
      $("review-compare").disabled=true;
      try{
        const data=await client.read("diff",{snapshotId:detail.snapshotId,targetId:detail.target.id,fromRevision:from||null,toRevision:to||null,offset:append?diffRows.length:0,limit:150});
        if(epoch!==detailEpoch)return;diff=data;diffRows=append?[...diffRows,...data.diff.rows]:data.diff.rows;
        $("review-diff-status").textContent=(data.hasBaseline?`追加 ${data.diff.added}行 / 削除 ${data.diff.removed}行。`:"比較元の記録がないため、変更量ではなく現在のコードを表示します。 ")+data.diff.note;
        const body=$("review-diff-body");body.replaceChildren();for(const line of diffRows){const tr=el("tr");tr.dataset.kind=line.kind;tr.append(el("td",line.oldLine??""),el("td",line.newLine??""),el("td",line.kind==="add"?"+":line.kind==="remove"?"−":""),el("td",line.text));body.append(tr);}
        $("review-diff-more").hidden=!data.diff.hasMore;
        if(data.hasBaseline&&data.diff.added===0&&data.diff.removed===0)$("review-diff-status").textContent="この比較範囲のソースに差分はありません。最適化前後を見るには、比較元に以前の確認記録を選んでください。";
      }catch(error){if(epoch===detailEpoch){$("review-diff-status").textContent=error.message;$("review-diff-body").replaceChildren();$("review-diff-more").hidden=true;}}
      finally{if(epoch===detailEpoch)$("review-compare").disabled=!detail.target.trackable;}
    }
    function requestRefresh(){client.epoch++;if(busy){pendingRefresh=true;return;}load();}
    $("review-refresh").addEventListener("click",requestRefresh);
    for(const id of ["review-kind","review-filter"])$(id).addEventListener("change",()=>{selection=null;detailEpoch++;$("review-detail").hidden=true;requestRefresh();});
    $("review-query").addEventListener("input",()=>{clearTimeout(queryTimer);queryTimer=setTimeout(requestRefresh,250);});
    $("review-more").addEventListener("click",()=>load(true));$("review-compare").addEventListener("click",()=>compare());$("review-diff-more").addEventListener("click",()=>compare(true));
    for(const id of ["review-before","review-after"])$(id).addEventListener("change",()=>{detailEpoch++;compare();});
    function schedule(){clearInterval(timer);if(!document.hidden){load();timer=setInterval(()=>{if(rows.length<=80)load();},5000);}}
    document.addEventListener("visibilitychange",schedule);global.addEventListener("pagehide",()=>clearInterval(timer));schedule();
  }
  if(typeof module!=="undefined"&&module.exports)module.exports={ReviewProgressClient,label,reasons,queueRequest,mergeQueueRows};
  if(typeof document!=="undefined")document.addEventListener("DOMContentLoaded",start);
})(typeof globalThis!=="undefined"?globalThis:this);
