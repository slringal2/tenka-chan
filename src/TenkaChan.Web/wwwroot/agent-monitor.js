"use strict";
(function(global) {
  const labels = { tool_started:"ツールを実行中", tool_result:"ツールの結果", failed:"ツールの失敗", cancelled:"処理中止",
    agent_plan:"LLMの計画（自己申告）", agent_progress:"LLMの進捗報告（自己申告）", agent_hypothesis:"LLMの仮説（未検証）",
    agent_conclusion:"LLMのまとめ（未検証）",session_started:"セッション開始",session_finished:"終了通知" };
  const toolNames = {context:"プロジェクト状態を確認",sample:"付属サンプルを探す",open_project:"プロジェクトを読み込む",list_types:"クラスを探す",
    inspect_type:"クラスの構成を調査",inspect_method:"メソッドを調査",type_references:"クラスの参照を調査",method_references:"メソッドの参照を調査",
    scan:"静的解析",rules:"検知ルールを確認",priorities:"調査順序を確認",binary_methods:"DLLのメソッドを探す",il:"IL・PDBを調査",jit:"JIT機械語を取得",trace:"実行を計測",
    build:"ビルドと解析",test_inputs:"テスト入力を検討",native:"命令を解析",compare_native:"機械語を比較",artifact:"取得結果を確認",runtime_costs:"GC・割り当て・ロックを調査",
    import_thread:"計測スレッドを反映",run_companion:"既存テスト・ベンチマークを実行",prepare_experiment:"生成した実験を準備",run_experiment:"生成した実験を実行",
    experiment_result:"実験結果を確認",compare_benchmarks:"ベンチマークを比較",read_result:"結果の詳細を読む",note:"調査メモを報告"};
  function presentEvent(event) {
    const note=String(event.kind).startsWith("agent_");
    return {label:labels[event.kind]||event.kind,tool:toolNames[event.tool]||event.tool,agentNote:note,
      failed:["failed","cancelled"].includes(event.kind),message:event.message,
      observed:event.observedStatus||null,canRead:!!event.callId&&["tool_result","failed","cancelled"].includes(event.kind)};
  }
  class AgentMonitorClient {
    constructor(fetcher){this.fetcher=fetcher;this.data=null;this.stale=false;this.pending=null;this.controller=null;}
    async read(path,signal) {
      if(path!=="/api/agent/monitor"&&!path.startsWith("/api/agent/result?callId="))throw new Error("表示用エンドポイントではありません。");
      const r=await this.fetcher(path,{method:"GET",credentials:"same-origin",cache:"no-store",headers:{Accept:"application/json"},signal});
      if(!r.ok)throw new Error(`活動の表示に失敗しました（HTTP ${r.status}）。解析を再実行する操作は行っていません。`);
      return r.json();
    }
    poll(){
      if(this.pending)return this.pending;
      this.controller=new AbortController();
      this.pending=this.read("/api/agent/monitor",this.controller.signal).then(data=>{
        if(!data?.instanceId||!Array.isArray(data.events)||!Array.isArray(data.sessions))throw new Error("活動データの形式が不正です。");
        this.data=data;this.stale=false;return data;
      }).catch(error=>{this.stale=true;throw error;}).finally(()=>{this.pending=null;this.controller=null;});
      return this.pending;
    }
    result(callId){if(typeof callId!=="string"||!/^[a-fA-F0-9]{32}$/.test(callId))throw new Error("結果IDが不正です。");return this.read("/api/agent/result?callId="+encodeURIComponent(callId));}
    pause(){this.controller?.abort();}
  }
  function start() {
    const $=id=>document.getElementById(id),client=new AgentMonitorClient((...args)=>fetch(...args));
    let timer=null,instance=null,selected="";
    const node=(tag,text,cls)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=String(text);if(cls)n.className=cls;return n;};
    function render() {
      const data=client.data;if(!data)return;
      if(instance!==data.instanceId){instance=data.instanceId;selected="";$("agent-result-panel").hidden=true;}
      const list=$("agent-session");list.replaceChildren(new Option("すべて",""));
      for(const s of data.sessions)list.add(new Option(s.clientName+" / "+s.id.slice(0,8),s.id));
      if(data.sessions.some(s=>s.id===selected))list.value=selected;else selected="";
      const running=data.sessions.filter(s=>s.status==="running");
      $("agent-status").textContent=!data.enabled?"外部エージェントの受付は無効です。":running.length?`${running.length}件の処理が実行中です。`:data.sessions.length?"最新の活動を表示しています。次の操作は外部エージェントが決めます。":"外部エージェントからの最初のツール操作を待っています。";
      $("agent-status").dataset.stale="false";
      $("agent-permissions").textContent=`サーバー側の許可：ビルド・プロジェクト読み込み ${data.allowBuild?"許可":"未許可"} / 対象アプリ・テストの実行 ${data.allowExecution?"許可":"未許可"}。実行時はエージェント側の承認も必要です。`;
      $("agent-scope").textContent=data.note;
      const sessions=$("agent-sessions");sessions.replaceChildren();
      for(const s of data.sessions.filter(s=>!selected||s.id===selected)) {
        const card=node("article",undefined,"agent-session-card");
        card.append(node("strong",s.clientName+"（自己申告名）"),node("p",s.goal||"調査の目的は未報告です。"),node("p",s.activeTool?toolNames[s.activeTool]||s.activeTool:({idle:"次の操作待ち",expired:"期限切れ",finished:"終了通知あり"}[s.status]||s.status),"agent-muted"),node("p",`最終通信 ${new Date(s.lastSeen).toLocaleString("ja-JP")} / ${s.calls}回の操作`,"agent-muted"));sessions.append(card);
      }
      const kind=$("agent-kind").value,root=$("agent-timeline");root.replaceChildren();
      const events=data.events.filter(e=>(!selected||e.sessionId===selected)&&(kind==="all"||(kind==="notes")===e.kind.startsWith("agent_"))).slice(-150).reverse();
      for(const e of events) {
        const p=presentEvent(e),row=node("article",undefined,"agent-event");row.dataset.agentNote=String(p.agentNote);row.dataset.failed=String(p.failed);
        row.append(node("small",`${new Date(e.at).toLocaleTimeString("ja-JP")} · ${p.label}`),node("h3",p.tool||p.label),node("p",p.message));
        if(p.observed)row.append(node("p","返却された状態："+p.observed,"agent-muted"));
        if(p.canRead){const b=node("button","結果を見る","button button-outline");b.type="button";b.addEventListener("click",async()=>{b.disabled=true;try{const data=await client.result(e.callId);$("agent-result-panel").hidden=false;$("agent-result-note").textContent=data.note;$("agent-result").textContent=JSON.stringify(data.reply,null,2);$("agent-result-panel").scrollIntoView({block:"start",behavior:"smooth"});}catch(error){$("agent-status").textContent=error.message;}finally{b.disabled=false;}});row.append(b);}
        root.append(row);
      }
      if(!events.length)root.append(node("p","まだ該当する活動はありません。外部エージェントで調査を開始すると表示されます。"));
    }
    async function update(){if(document.hidden)return;try{await client.poll();render();}catch(error){if(error.name!=="AbortError"){$("agent-status").textContent=error.message+" 最後に取得できた履歴を残しています。";$("agent-status").dataset.stale="true";}}}
    function schedule(){clearInterval(timer);if(!document.hidden){update();timer=setInterval(update,2000);}else{client.pause();timer=null;}}
    $("agent-refresh").addEventListener("click",update);$("agent-session").addEventListener("change",()=>{selected=$("agent-session").value;render();});$("agent-kind").addEventListener("change",render);
    document.addEventListener("visibilitychange",schedule);global.addEventListener("pagehide",()=>{clearInterval(timer);client.pause();});schedule();
  }
  if(typeof module!=="undefined"&&module.exports)module.exports={AgentMonitorClient,presentEvent,toolNames};
  if(typeof document!=="undefined")document.addEventListener("DOMContentLoaded",start);
})(typeof globalThis!=="undefined"?globalThis:this);
