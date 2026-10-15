"use strict";
(function(global) {
  const statusNames = {
    build_succeeded:"ビルド完了",build_failed:"ビルド失敗",tests_passed:"テスト成功",tests_failed:"失敗したテストがあります",
    benchmark_completed:"ベンチマーク結果を取得",trace_completed:"プロファイルを取得",jit_completed:"JIT機械語を取得",
    jit_no_matching_method:"対象メソッドのJIT出力がありません",completed_no_native_listing:"アプリは終了しましたがJIT出力がありません",
    timed_out:"制限時間に達しました",cancelled:"中止しました",no_test_report:"テスト結果ファイルがありません",
    no_tests_discovered:"実行対象のテストがありません",unverified:"検証できる結果がありません",execution_failed:"実行に失敗しました",
    target_exited_nonzero:"対象アプリがエラー終了しました",capture_failed:"計測に失敗しました",invalid_report:"結果ファイルを確認できませんでした",runner_unavailable:"dotnetを起動できませんでした"
  };
  function statusText(s) { return statusNames[s] || s || "結果を取得しました"; }
  function argumentLines(text) { return text.replace(/\r/g, "").split("\n").filter(s => s.length > 0); }
  function sampleEndpoint(kind) {
    if (kind === "game-lab") return "/api/samples/game-lab";
    if (kind === "raid-lab") return "/api/samples/raid-lab";
    if (kind === "ilspy") return "/api/samples/ilspy";
    throw new Error("サンプルを一覧から選んでください。");
  }
  function samplePresetArguments(kind) {
    if (!kind) return [];
    if (["raid-run", "raid-batch"].includes(kind))
      return ["--scope",kind === "raid-batch" ? "batch" : "pipeline","--players","200","--events","6000","--concurrency",kind === "raid-batch" ? "4" : "1","--seconds","4"];
    if (["inventory", "contention"].includes(kind))
      return ["--scenario",kind,"--seconds","4",...(kind === "contention" ? ["--size","300"] : [])];
    throw new Error("起動条件を一覧から選んでください。古い比較用プリセットは廃止しました。");
  }
  function start() {
    const $ = id => document.getElementById(id);
    const Client = global.TenkaProjectClient.ProjectExplorerClient;
    const showTab = key => global.TenkaProjectTabs?.show(key);
    const labelProject = p => global.TenkaProjectClient.projectLabel(p, client.context?.projects || []);
    const client = new Client((...args) => fetch(...args));
    let pageBusy = false, currentType = null, types = [], typeTotal = 0, searchTimer, exportData = null;
    let artifact = null, nativeIndex = "", threadIndex = "";
    const node = (tag, text, cls) => { const n = document.createElement(tag); if (text !== undefined) n.textContent = String(text); if (cls) n.className = cls; return n; };
    const message = (text, error = false) => { $("project-status").textContent = text; $("project-status").parentElement.dataset.error = String(error); };
    function options(element, rows, value, placeholder) {
      element.replaceChildren();
      if (rows.length !== 1) element.add(new Option(placeholder, ""));
      for (const row of rows) element.add(new Option(row.label, row.id));
      if (rows.some(r => r.id === value)) element.value = value;
      else if (rows.length === 1) element.value = rows[0].id;
    }
    function controls(busy) {
      pageBusy = busy;
      document.querySelectorAll(".project-workbench input,.project-workbench select,.project-workbench button").forEach(n => n.disabled = busy);
      document.querySelectorAll("[data-workspace-tab],[data-open-workspace-tab]").forEach(n => n.disabled = false);
      $("cancel-action").hidden = !busy; $("cancel-action").disabled = false;
      if (busy) return;
      const loaded = !!client.context && !client.stale;
      $("project-build").disabled = !loaded || !client.project() || !client.context.capabilities.build;
      $("project-scan").disabled = !loaded;
      $("selected-project").disabled = !loaded;
      $("selected-assembly").disabled = !loaded || !client.assemblies().length;
      $("class-scope").disabled = !loaded;
      $("class-search").disabled = !loaded; $("type-references").disabled = !loaded || !client.type;
      for (const id of ["method-references", "test-plan"]) $(id).disabled = !loaded || !client.method;
      $("inspect-il").disabled = !loaded || !client.method || !client.assemblyId;
      $("load-il").disabled = !loaded || !$("binary-method").value || !client.binary;
      $("capture-jit").disabled = !loaded || !client.method?.jitFilter || !client.launchId || !client.context.capabilities.localExecution;
      $("capture-trace").disabled = !loaded || !client.launchId || !client.context.capabilities.trace;
      $("run-companion").disabled = !loaded || !$("companion-project").value || !client.context.capabilities.build;
      $("export-result").disabled = !exportData;
      for (const id of ["decode-native", "pin-native"]) $(id).disabled = !artifact || nativeIndex === "";
      $("compare-native").disabled = !artifact || nativeIndex === "" || !client.baseline;
      $("import-thread").disabled = !artifact || threadIndex === "" || artifact.data.status !== "trace_completed";
      $("runtime-costs").disabled = !artifact || artifact.kind !== "trace" || !artifact.data.netTrace;
    }
    function resetDetail() {
      currentType = null; $("class-content").hidden = true; $("class-empty").hidden = false;
      $("method-section").hidden = true; $("binary-choice-panel").hidden = true;
    }
    function renderContext() {
      const c = client.context;
      global.TenkaProjectTabs?.observeLoaded(!!c);
      $("workspace-title").textContent = c ? `${c.projects.length}プロジェクトを読み込み済み` : "プロジェクト未読込";
      $("workspace-selection").textContent = c ? "選択中：" + (client.method?.signature || client.type?.name || client.project()?.name || "ソリューション全体") : "「準備」でソリューションまたはプロジェクトを選んでください。";
      if (!c) {
        resetDetail(); types = []; renderTypes();
        $("project-switch").open = true; $("project-summary").textContent = "① プロジェクトを選ぶ";
        $("build-help").textContent = "プロジェクトを読み込んでください。";
        for (const id of ["selected-project","selected-assembly","launch-assembly","companion-project","result-history","class-scope"])
          options($(id), [], "", "プロジェクト未読込");
        $("solution-projects").replaceChildren();
        return;
      }
      $("project-switch").open = false;
      $("project-summary").textContent = "読み込み済み：" + (client.project()?.name || "ソリューション全体") + "（別のプロジェクトを選ぶ）";
      // Do not erase a path the user is editing; setup query and an explicitly selected path remain intact.
      if (!$("project-path").value) $("project-path").value = c.path;
      options($("selected-project"), c.projects.map(p => ({id:p.id,label:labelProject(p)})), client.projectId, "ビルド・DLLの対象を選択");
      options($("class-scope"), [{id:"",label:"すべてのプロジェクト"}, ...c.projects.map(p => ({id:p.id,label:labelProject(p)}))], client.scopeProjectId, "");
      // The explicit all-projects option must not be duplicated by the generic placeholder helper.
      if ($("class-scope").options[0]?.value === "" && $("class-scope").options[1]?.value === "") $("class-scope").remove(0);
      const structure = $("solution-projects"); structure.replaceChildren();
      for (const p of c.projects) {
        const row = node("article",undefined,"solution-project");
        const refs = (p.projectReferenceIds || []).map(id => c.projects.find(q => q.id === id)?.name || "未解決の参照");
        const text = node("div"); text.append(node("strong",labelProject(p)),node("p",`C#文書 ${p.documents}件 · 参照先：${refs.length ? refs.join(" / ") : "なし／取得情報なし"}`,"small"));
        const choose = button("このプロジェクトを選択", () => busyTask("対象プロジェクトを切り替えています…", async () => { client.setProject(p.id); resetDetail(); renderContext(); await loadTypes(); }));
        row.append(text,choose); structure.append(row);
      }
      const assetRows = client.assemblies().map(a => ({id:a.id,label:`${a.project} / ${a.configuration} / ${a.targetFramework}${a.runnable ? " / 起動可" : " / ライブラリ等"}`}));
      options($("selected-assembly"), assetRows, client.assemblyId, assetRows.length ? "ビルド出力を選択" : "未検出：Releaseビルドしてください");
      client.assemblyId = $("selected-assembly").value;
      options($("launch-assembly"), client.launchers().map(a => ({id:a.id,label:`${labelProject(c.projects.find(p => p.id === a.projectId))} / ${a.configuration} / ${a.targetFramework}`})), client.launchId, "実行するアプリを選択");
      if (!client.launchId) {
        if (!Array.from($("launch-assembly").options).some(o => o.value === "")) $("launch-assembly").insertBefore(new Option("実行するアプリを選択", ""), $("launch-assembly").firstChild);
        $("launch-assembly").value = "";
      }
      client.launchId = $("launch-assembly").value;
      options($("companion-project"), c.companions.map(p => ({id:p.id,label:`${p.kind === "test" ? "テスト" : "ベンチマーク"}：${p.name}`})), $("companion-project").value, c.companions.length ? "実行するプロジェクトを選択" : "検出なし（ソリューションに含めるかTests/Benchmarksへ）");
      options($("result-history"), c.history.map(h => ({id:h.id,label:`${h.kind.toUpperCase()} / ${new Date(h.createdUtc).toLocaleTimeString("ja-JP")} / ${h.label}`})), artifact?.id || "", c.history.length ? "取得済みの結果を選択" : "まだ取得していません");
      $("build-help").textContent = !c.capabilities.build ? "ローカルビルドが無効です。setup.batの起動設定を確認してください。"
        : !client.projectId ? "複数プロジェクトがあります。ビルドする対象だけ上で選択してください。"
        : c.scanned ? `静的解析済み：${c.summary?.totalFindings ?? 0}件の指摘。候補でありバグ確定ではありません。`
        : "まだ静的解析していません。「準備」でビルド対象を選び、ビルド＋全体解析を実行してください。";
      $("trace-help").textContent = !c.capabilities.trace ? "プロファイルツールが未準備、またはローカル実行が無効です。setup.batで準備してください。"
        : !client.launchId ? "実行アプリを選んでください。ライブラリには呼び出し元アプリが必要です。"
        : "保存先を自動で作成します。取得後にGC・割り当て・スレッドをボタンで調べられます。";
      $("resolved-files").textContent = ["プロジェクト: " + (client.project()?.path || c.path),
        "IL用DLL: " + (c.assemblies.find(a => a.id === client.assemblyId)?.assemblyPath || "未選択"),
        "起動DLL: " + (c.assemblies.find(a => a.id === client.launchId)?.assemblyPath || "未選択"),
        "PDB: 選択DLLと同じフォルダーから自動検出", "プロファイル保存先: 実行アプリのプロジェクト内 .tenka/profiles"].join("\n");
    }
    function button(text, operation, cls = "button button-outline") {
      const b = node("button", text, cls); b.type = "button"; b.addEventListener("click", operation); return b;
    }
    function renderTypes() {
      const list = $("class-list"); list.replaceChildren();
      for (const row of types) {
        const b = button("", () => busyTask("クラスの構成を調べています…", async () => {
          if (client.projectId !== row.projectId) client.setProject(row.projectId);
          renderContext();
          const result = await client.perform("type", {typeId:row.id}); renderType(result); await loadTypes();
        }), "choice");
        b.append(node("strong", row.name), node("span", `${row.kind} · ${row.methodCount}メソッド · ${labelProject(client.context.projects.find(p => p.id === row.projectId))}`));
        b.setAttribute("aria-pressed", String(client.type?.id === row.id)); list.append(b);
      }
      $("class-count").textContent = client.context ? `${types.length} / ${typeTotal}件` : "プロジェクトを読み込むと表示されます。";
      $("more-classes").hidden = types.length >= typeTotal;
      if (client.context && !types.length) list.append(node("p", "一致する型がありません。検索条件を変えてください。", "small"));
    }
    async function loadTypes(append = false) {
      const result = await client.types($("class-search").value, append ? types.length : 0);
      if (!result) { types = []; typeTotal = 0; renderTypes(); return; }
      types = append ? [...types, ...result.items] : result.items; typeTotal = result.total; renderTypes();
      if (result.indexLimited) $("class-count").textContent += "（索引の上限に達しています）";
    }
    function textLines(target, lines) {
      target.replaceChildren();
      if (!lines?.length) { target.append(node("p", "該当なし／未観測", "small")); return; }
      for (const line of lines) target.append(node("div", line, "facts-line"));
    }
    function table(target, headings, rows) {
      const wrap = node("div",undefined,"result-scroll"), t = node("table"), head = node("thead"), tr = node("tr");
      for (const h of headings) tr.append(node("th",h)); head.append(tr); t.append(head);
      const body = node("tbody");
      for (const row of rows.slice(0,1000)) { const r = node("tr"); for (const value of row) { const cell=node("td"); value instanceof Node ? cell.append(value) : cell.textContent=String(value ?? "—"); r.append(cell); } body.append(r); }
      t.append(body); wrap.append(t); target.append(wrap);
      if (rows.length > 1000) target.append(node("p","表示は先頭1,000行までです。JSONに含まれる結果を確認できます。","small"));
    }
    function renderType(result) {
      showTab("code");
      currentType = result.detail; $("class-empty").hidden = true; $("class-content").hidden = false;
      $("type-kind").textContent = currentType.type.kind; $("type-name").textContent = currentType.type.name;
      $("type-structure").textContent = "基底型: " + (currentType.baseType || "なし") + " / インターフェース: " + (currentType.interfaces.join(", ") || "なし");
      textLines($("type-members"), currentType.members.map(m => m.kind + " · " + m.signature));
      $("type-sources").replaceChildren();
      for (const s of currentType.sources) { $("type-sources").append(node("p",`${s.file}:${s.line}${s.truncated ? "（一部省略）" : ""}`,"small"),node("pre",s.text)); }
      const findings = $("type-findings"); findings.replaceChildren();
      if (!result.findings.scanned) findings.append(node("p","静的解析はまだです。「準備」の「ソリューション全体を静的解析」でこのクラスの指摘も確認できます。","small"));
      else if (!result.findings.findings.length) findings.append(node("p","この型の索引済みメソッドへの指摘はありません。正しさや軽さを保証するものではありません。","small"));
      else table(findings,["ルール","指摘"],result.findings.findings.map(f => [f.ruleId,f.message]));
      if (result.findings.limited) findings.append(node("p","全体の指摘件数が上限に達しているため、表示は網羅的ではありません。","small"));
      $("type-notes").textContent = currentType.notes.join(" "); $("method-search").value = ""; renderMethods();
      if (!client.method) $("method-section").hidden = true;
      $("workspace-selection").textContent = "選択中：" + currentType.type.project + " / " + currentType.type.name;
    }
    function renderMethods() {
      const root=$("method-list"); root.replaceChildren();
      if (!currentType) return;
      const q=$("method-search").value.toLowerCase();
      for(const method of currentType.methods.filter(m=>m.signature.toLowerCase().includes(q))) {
        const b=button("",()=>selectMethod(method.id),"choice"); b.append(node("strong",method.signature),node("span",`${method.kind} · ${method.line}行目`));
        b.setAttribute("aria-pressed",String(client.method?.id===method.id));root.append(b);
      }
    }
    async function selectMethod(id) {
      await busyTask("メソッドのソースと呼び出しを調べています…",async()=>{
        const result=await client.perform("method",{methodId:id});
        if (client.type?.id !== result.method.typeId) {
          // Reference navigation may lead to a different class/project; update the visible selection too.
          client.setProject(result.method.projectId);
          const detail=await client.perform("type",{typeId:result.method.typeId}); renderType(detail);
          client.method=result.method; renderContext();
        }
        renderMethod(result); renderMethods();
      });
      $("method-section").scrollIntoView({behavior:"smooth",block:"start"});
    }
    function renderMethod(result) {
      showTab("code");
      const m=result.method,r=result.inspection;
      $("workspace-selection").textContent = "選択中：" + labelProject(client.project()) + " / " + m.signature; $("method-section").hidden=false; $("binary-choice-panel").hidden=true;
      $("method-title").textContent=m.signature;$("method-location").textContent=m.file+":"+m.line;$("method-source").textContent=r.source;
      $("method-help").textContent=m.jitFilter ? "ILはDLLと隣のPDBを自動取得。JITは選択したアプリを実行します。同名オーバーロードは複数の出力が含まれる場合があります。" : m.jitUnavailableReason;
      textLines($("method-callees"),r.callees);textLines($("method-callers"),r.callers);
      textLines($("method-flow"),["読み取り: "+(r.reads.join(", ")||"なし"),"書き込み: "+(r.writes.join(", ")||"なし"),...(r.diagnostics||[]),...(r.analysisNote?[r.analysisNote]:[])]);
      table($("method-flow"),["ブロック","処理","条件","次のブロック"],r.blocks.map(b=>[b.ordinal,b.operations.join("\n"),b.branchCondition,`${b.conditionalSuccessor ?? "—"} / ${b.fallThroughSuccessor ?? "—"}`]));
    }
    function rawDetails(root,data) {
      const d=node("details",undefined,"json-details");d.append(node("summary","詳細データを確認"));const text=JSON.stringify(data,null,2);d.append(node("pre",text.length>160000?text.slice(0,160000)+"\n表示上限。JSONで保存して確認してください。":text));root.append(d);
    }
    function renderResult(action,data) {
      showTab("results");
      exportData=data;$("native-actions").hidden=true;$("trace-actions").hidden=true;
      const root=$("result-body");root.replaceChildren();
      let result=data;
      if (data?.kind && data?.data) {artifact=data;client.lastArtifact=data;result=data.data;$("result-title").textContent=statusText(result.status)+" — "+data.label;}
      else $("result-title").textContent=data?.build ? statusText(data.build.status) : "調査結果";
      if (data?.build) {
        root.append(node("p",data.note));root.append(node("p",data.analysis?`解析完了: 指摘${data.analysis.findings}件 / コンパイラーエラー${data.analysis.compilerErrors}件` : "ビルド後の解析が完了したか、上の状態とログを確認してください。"));
        root.append(node("pre",data.build.output||data.build.stderr||"出力なし"));
      } else if (action==="type-references"||action==="method-references") {
        root.append(node("p",data.note));table(root,["場所","コード","調査"],data.items.map(r=>[`${r.project} / ${r.file}:${r.line}`,r.preview,r.methodId?button("このメソッドへ",()=>selectMethod(r.methodId)):"型・フィールド等の参照"]));
        if(data.limited)root.append(node("p","表示は先頭300件です。"));
      } else if(action==="il") {
        root.append(node("p",`${data.signature} / IL ${data.ilSizeBytes} bytes / PDB照合: ${data.pdbStatus}`));
        root.append(node("p","メソッド名から選択したバイナリの結果です。同じソースのビルドかどうかはPDBとチェックサムの照合結果で確認してください。","small"));
        table(root,["ILオフセット","命令","オペランド","ソース対応"],data.instructions.map(i=>[i.offset,i.opCode,i.operand,i.sourceMappingStatus]));
      } else if(action==="test-plan") {
        root.append(node("p","型に基づく入力候補です。期待する正しい出力・インスタンスの初期状態は自動では決められません。"));
        table(root,["引数","型","入力候補","補足"],data.inputs.map(i=>[i.parameter,i.type,i.cSharpExpressions.join(" / "),i.reason]));
      } else if(action==="scan") {
        const s=data.summary;root.append(node("p",`静的解析完了：${s.totalFindings}件。コンパイラーエラー${s.compilerErrors}件、警告${s.compilerWarnings}件。`));
        table(root,["ルール","種類","指摘","場所"],data.findings.map(f=>[f.ruleId,f.evidenceLevel,f.message,`${f.relativePath}:${f.line}`]));
        root.append(node("p",data.note,"small"));
      } else if(data?.kind==="jit") {
        root.append(node("p","命令は生成されたコードの情報です。実行時間ではありません。対象メソッドが呼ばれない・インライン化された場合は出力されないことがあります。","small"));
        const methods=result.nativeDisassembly?.methods||[];
        options($("native-method"),methods.map((m,i)=>({id:String(i),label:m.jitSignature})),"","取得メソッドを選択");nativeIndex=$("native-method").value;
        $("native-actions").hidden=result.status!=="jit_completed"||!methods.length;
        table(root,["メソッド","コードサイズ","命令数"],methods.map(m=>[m.jitSignature,m.codeSizeBytes,m.instructions.length]));
        if(result.stderr)root.append(node("pre",result.stderr));
      } else if(data?.kind==="trace") {
        root.append(node("p","スレッドのサンプリングには待機も含まれます。CPU使用率とは異なります。GCや割り当ては別のボタンで確認します。","small"));
        options($("trace-thread"),(result.profiles||[]).map(p=>({id:String(p.index),label:`${p.name} / ${p.observations}観測`})),"","解析するスレッドを選択");threadIndex=$("trace-thread").value;
        $("trace-actions").hidden=false;if(result.output)root.append(node("pre",result.output));
      } else if(data?.kind==="test"||data?.kind==="benchmark") {
        if(result.tests)root.append(node("p",`全${result.tests.total}件 / 成功${result.tests.passed}件 / 失敗${result.tests.failed}件`));
        if(result.benchmarks?.length)table(root,["ケース","平均時間(ns)","割り当て(bytes/op)","サンプル数"],result.benchmarks.map(b=>[b.caseId,b.meanNanoseconds,b.allocatedBytesPerOperation,b.sampleCount]));
        root.append(node("p",result.note,"small"),node("pre",[result.output,result.stderr].filter(Boolean).join("\n")));
      } else if(action==="native") {
        const a=data.analysis;root.append(node("p",`命令解析: ${a.status} / ${a.architecture} / ${a.codeSizeBytes} bytes`));
        table(root,["位置","機械語","命令","制御フロー"],a.instructions.map(i=>[i.offset,i.bytes,i.assembly,i.flowControl]));
        table(root,["調査箇所","種類","理由"],a.investigationSites.map(i=>[i.offset,i.kind,i.reason]));
      } else if(action==="compare-native") {
        root.append(node("p",`同一署名: ${data.sameSelectionSignature ? "はい" : "いいえ"} / コードサイズ差: ${data.codeSizeDelta ?? "比較不可"} / 命令数差: ${data.instructionCountDelta ?? "比較不可"}`));
        root.append(node("p","コード生成の比較です。命令数が減っただけで速くなったとは判定しません。"));
      } else {
        root.append(node("p",action==="import-thread"?"計測スレッドを性能ホットスポットへ反映しました。":action==="runtime-costs"?"保存したイベントを解析しました。未観測の項目を負荷ゼロとは判定しません。":"取得した根拠を表示します。"));
        const labels={status:"状態",processId:"プロセス",totalEvents:"イベント数",totalWeight:"観測重み",linkedFrames:"対応したフレーム",measurementKind:"測定の種類",summary:"要約",gc:"GC",allocation:"割り当て",contention:"ロック競合",note:"補足"};
        const rows=[];
        function flatten(value,prefix,depth){ if(depth>4||rows.length>=80)return;if(value===null||typeof value!=="object"){if(value!==undefined)rows.push([prefix,String(value)]);return;}for(const [key,v]of Object.entries(value)){if(/path|sha256|signature|methodId|snapshotId|fingerprint/i.test(key))continue;if(Array.isArray(v)){v.slice(0,5).forEach((item,i)=>flatten(item,prefix+" / "+(labels[key]||key)+" "+(i+1),depth+1));}else flatten(v,prefix+(prefix?" / ":"")+(labels[key]||key),depth+1);}}
        flatten(result,"",0);table(root,["項目","観測値／情報"],rows);
      }
      if(result.limitations?.length)root.append(node("p",result.limitations.join(" "),"small"));
      if (artifact?.kind === "jit" && ["jit","result","native","compare-native"].includes(action))
        $("native-actions").hidden = artifact.data.status !== "jit_completed" || !(artifact.data.nativeDisassembly?.methods?.length);
      if (artifact?.kind === "trace" && ["trace","result","runtime-costs","import-thread"].includes(action))
        $("trace-actions").hidden = false;
      rawDetails(root,data);controls(pageBusy);$("result-section").scrollIntoView({behavior:"smooth",block:"start"});
    }
    function confirmExecution(body) {
      const c=client.context;
      const target=body.action==="build"?client.project()?.name:body.action==="run-companion"?c.companions.find(p=>p.id===body.companionId)?.name:c.assemblies.find(a=>a.id===body.assemblyId)?.project;
      return window.confirm(`${target}\n${body.action==="build"?"Releaseビルドと静的解析":body.action==="run-companion"?"既存のテスト／ベンチマークのビルドと実行":"アプリの通常起動と計測"}を実行します。\n隔離はなく、対象コードは現在のユーザー権限でファイル・ネットワークを操作できます。\n${body.allowRestore?"不足パッケージの復元を許可します。\n":""}${body.arguments?.length?"引数: "+body.arguments.join(" | ")+"\n":""}実行しますか？`);
    }
    async function busyTask(text,operation) {
      if(pageBusy)return;controls(true);message(text);
      try { await operation(); if ($("project-status").textContent === text) message("操作が完了しました。"); }
      catch(error){ if(client.stale)resetDetail(); if(error.name!=="StaleExplorerResponse")message(error.name==="AbortError"?"処理を中止しました。実行の終了状態はログでも確認してください。":error.message,true); }
      finally{controls(false);}
    }
    async function refreshAll() {
      const old=client.context?.snapshotId;
      await client.refresh();renderContext();
      if(old!==client.context?.snapshotId){resetDetail();artifact=null;$("native-actions").hidden=true;$("trace-actions").hidden=true;}
      await loadTypes();
    }
    async function run(action,extra={}) {
      await busyTask("処理中です。結果を取得するまでお待ちください…",async()=>{
        const response=await client.perform(action,{allowRestore:$("allow-restore").checked,...extra},confirmExecution);
        if(response===null){message("実行を取り消しました。新しい処理は開始していません。");return;}
        let refreshError = null;
        try {
          if(["build","scan","run-companion"].includes(action)) {
            const selected=client.type?.id;
            await refreshAll();
            if(action==="scan"&&selected){const detail=await client.perform("type",{typeId:selected});renderType(detail);}
          } else if(response?.kind){ await client.refresh();renderContext(); }
        } catch(error) { refreshError=error; }
        // A completed execution result remains visible even when the later read fails.
        renderResult(action,response);
        const execution=response.kind ? response.data : response.build;
        if(execution?.status) {
          const successful=["build_succeeded","tests_passed","benchmark_completed","jit_completed","trace_completed"].includes(execution.status);
          message(statusText(execution.status),!successful);
          if(response.build?.status==="build_succeeded" && (!response.workspaceReloaded || !response.analysis))
            message("ビルドは完了しましたが、再読み込みまたは解析は未完了です。結果の案内を確認してください。",true);
        }
        if(refreshError) message("結果は下に表示しましたが、プロジェクト状態の更新に失敗しました："+refreshError.message,true);
      });
    }
    $("project-open").addEventListener("submit",e=>{e.preventDefault();busyTask("プロジェクトを読み込んでいます…",async()=>{await client.open($("project-path").value);resetDetail();renderContext();await loadTypes();showTab("code");});});
    $("refresh-project").addEventListener("click",()=>busyTask("状態を更新しています…",refreshAll));
    $("use-sample").addEventListener("click",()=>busyTask("サンプルを探しています…",async()=>{const sample=await client.request(sampleEndpoint($("sample-kind").value));if(!sample.available||!sample.projectPath)throw new Error(sample.message || "付属サンプルがありません。本体を更新してください。");$("project-path").value=sample.projectPath;$("project-switch").open=true;showTab("prepare");}));
    $("selected-project").addEventListener("change",()=>busyTask("クラス一覧を更新しています…",async()=>{client.setProject($("selected-project").value);resetDetail();renderContext();await loadTypes();}));
    $("class-scope").addEventListener("change",()=>busyTask("検索範囲を更新しています…",async()=>{client.setScope($("class-scope").value);await loadTypes();}));
    $("selected-assembly").addEventListener("change",()=>{client.assemblyId=$("selected-assembly").value;client.binary=null;$("binary-choice-panel").hidden=true;renderContext();controls(false);});
    $("launch-assembly").addEventListener("change",()=>{client.launchId=$("launch-assembly").value;renderContext();controls(false);});
    $("companion-project").addEventListener("change",()=>controls(false));
    $("class-search").addEventListener("input",()=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>loadTypes().catch(e=>{if(e.name!=="AbortError"&&e.name!=="StaleExplorerResponse")message(e.message,true);}),240);});
    $("more-classes").addEventListener("click",()=>busyTask("クラスを読み込んでいます…",()=>loadTypes(true)));
    $("method-search").addEventListener("input",renderMethods);
    $("type-references").addEventListener("click",()=>run("type-references"));
    $("method-references").addEventListener("click",()=>run("method-references"));
    $("test-plan").addEventListener("click",()=>run("test-plan"));
    $("project-build").addEventListener("click",()=>run("build"));
    $("project-scan").addEventListener("click",()=>run("scan"));
    $("capture-jit").addEventListener("click",()=>run("jit",{arguments:argumentLines($("runtime-arguments").value)}));
    $("capture-trace").addEventListener("click",()=>run("trace",{arguments:argumentLines($("runtime-arguments").value)}));
    $("run-companion").addEventListener("click",()=>run("run-companion",{companionId:$("companion-project").value}));
    $("inspect-il").addEventListener("click",()=>busyTask("DLL内のメソッドを探しています…",async()=>{
      const result=await client.perform("binary-methods");
      options($("binary-method"),result.methods.map(m=>({id:String(m.metadataToken),label:m.signature})),"","署名を選択");
      $("binary-choice-panel").hidden=false;$("binary-note").textContent=result.methods.length?result.note:"一致するバイナリのメソッドがありません。Releaseビルド後に試してください。生成メソッドなどは自動対応できない場合があります。";
      if(result.methods.length===1){const il=await client.perform("il",{metadataToken:result.methods[0].metadataToken,assemblySha256:result.assemblySha256});renderResult("il",il);}
    }));
    $("binary-method").addEventListener("change",()=>controls(false));
    $("load-il").addEventListener("click",()=>run("il",{metadataToken:Number($("binary-method").value),assemblySha256:client.binary?.assemblySha256}));
    $("result-history").addEventListener("change",()=>{if($("result-history").value)run("result",{artifactId:$("result-history").value});});
    $("native-method").addEventListener("change",()=>{nativeIndex=$("native-method").value;controls(false);});
    $("trace-thread").addEventListener("change",()=>{threadIndex=$("trace-thread").value;controls(false);});
    $("decode-native").addEventListener("click",()=>run("native",{artifactId:artifact?.id,nativeIndex:Number(nativeIndex)}));
    $("pin-native").addEventListener("click",()=>{client.baseline={id:artifact.id,index:Number(nativeIndex)};$("native-baseline").textContent="比較元: "+artifact.label+" / "+$("native-method").selectedOptions[0].textContent;controls(false);});
    $("compare-native").addEventListener("click",()=>run("compare-native",{artifactId:artifact?.id,nativeIndex:Number(nativeIndex),baselineId:client.baseline?.id,baselineIndex:client.baseline?.index}));
    $("import-thread").addEventListener("click",()=>run("import-thread",{artifactId:artifact?.id,profileIndex:Number(threadIndex)}));
    $("runtime-costs").addEventListener("click",()=>run("runtime-costs",{artifactId:artifact?.id}));
    $("cancel-action").addEventListener("click",()=>{client.cancel();message("中止を要求しました。子プロセスの終了はベストエフォートです。");});
    $("argument-preset").addEventListener("change",()=>{$("runtime-arguments").value=samplePresetArguments($("argument-preset").value).join("\n");});
    $("export-result").addEventListener("click",()=>{if(!exportData)return;const url=URL.createObjectURL(new Blob([JSON.stringify(exportData,null,2)],{type:"application/json"}));const link=node("a");link.href=url;link.download="tenka-investigation.json";link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);});
    global.addEventListener("beforeunload",event=>{if(client.busy){event.preventDefault();event.returnValue="";}});
    busyTask("接続を確認しています…",refreshAll);
  }
  if(typeof module!=="undefined"&&module.exports)module.exports={statusText,argumentLines,sampleEndpoint,samplePresetArguments};
  if(typeof document!=="undefined")document.addEventListener("DOMContentLoaded",start);
})(typeof globalThis!=="undefined"?globalThis:this);
