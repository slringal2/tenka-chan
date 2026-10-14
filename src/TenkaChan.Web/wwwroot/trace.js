"use strict";
const $ = id => document.getElementById(id);
let csrf = "", capture = null, controller = null, enabled = false, runtimeController = null, runtimeReport = null;
function message(text, error = false) { $("message").textContent = text; $("message").classList.toggle("error", error); }
async function api(path, body, signal) {
  const response = await fetch("/api" + path, { method: body === undefined ? "GET" : "POST", signal,
    headers: body === undefined ? {} : { "Content-Type": "application/json", "X-Tenka-CSRF": csrf },
    body: body === undefined ? undefined : JSON.stringify(body) });
  const data = await response.json();
  if (!response.ok) throw new Error(data.error || "処理に失敗しました: " + response.status);
  return data;
}
function showCapture(result) {
  runtimeController?.abort(); runtimeReport = null;
  $("runtime-results").replaceChildren(); $("runtime-export").disabled = true;
  $("runtime-status").textContent = "未解析。元のnettraceを読み取ってGC・割り当て・競合を分けて表示します。";
  capture = result; $("report-path").value = result.reportPath;
  $("capture-status").textContent = "取得状態: " + result.status + " / " + result.targetFramework + " / " + result.measurementKind;
  $("metadata").textContent = JSON.stringify({ inputs: result.inputs, toolVersion: result.toolVersion,
    runtimeProviderConfiguration: result.runtimeProviderConfiguration ?? "旧記録・設定不明",
    arguments: result.arguments, startedUtc: result.startedUtc, completedUtc: result.completedUtc,
    limitations: result.limitations, output: result.output }, null, 2);
  const select = $("profiles");
  select.replaceChildren(new Option("解析するスレッドを選択してください", ""));
  for (const profile of result.profiles || []) select.add(new Option(profile.index + ": " + profile.name + " / " + profile.unit + " / " + profile.observations + " records", String(profile.index)));
  select.disabled = result.status !== "trace_completed";
  $("import-button").disabled = select.disabled; $("runtime-button").disabled = select.disabled;
}
function bind(id, action) {
  $(id).addEventListener("submit", async event => {
    event.preventDefault();
    try { await action(event.currentTarget); }
    catch (error) { message(error.name === "AbortError" ? "中止を要求しました。" : error.message, true); }
  });
}
bind("capture", async form => {
  if (!enabled || controller) throw new Error("ローカル実行が無効、または取得中です。");
  const values = Object.fromEntries(new FormData(form)); const args = JSON.parse(values.arguments);
  if (!Array.isArray(args) || args.some(arg => typeof arg !== "string")) throw new Error("引数はJSON文字列配列で指定してください。");
  if (values.trustedBinary !== "on") throw new Error("実行への明示的な許可が必要です。");
  if (!confirm("このアプリをOSユーザーの権限で実行します。Dockerによる隔離はありません。実行しますか？")) return;
  controller = new AbortController(); $("capture-button").disabled = true; $("cancel").disabled = false;
  message("アプリの実行と計測結果の変換を行っています。");
  try {
    const result = await api("/traces/capture", { assemblyPath: values.assemblyPath.trim(), outputDirectory: values.outputDirectory.trim(),
      timeoutSeconds: Number(values.timeoutSeconds), arguments: args, trustedBinary: true, runtimeCostEvents: values.runtimeCostEvents === "on" }, controller.signal);
    showCapture(result);
    message(result.status === "trace_completed" ? "取得しました。スレッド解析またはランタイムイベント解析を選択できます。" : "取得は完了していません。状態とログを確認してください。", result.status !== "trace_completed");
  } finally { controller = null; $("capture-button").disabled = !enabled; $("cancel").disabled = true; }
});
$("cancel").addEventListener("click", () => controller?.abort());
bind("load", async form => {
  const path = String(new FormData(form).get("reportPath")).trim();
  showCapture(await api("/traces/report?reportPath=" + encodeURIComponent(path)));
  message("保存結果を確認しました。アプリは再実行していません。");
});
bind("import", async () => {
  if (!capture || $("profiles").value === "") throw new Error("解析するスレッドを選択してください。");
  const result = await api("/traces/import", { reportPath: capture.reportPath, profileIndex: Number($("profiles").value), maxFrames: 200 });
  $("import-status").textContent = result.report.profileName + " / " + result.report.unit + "。待機を含む可能性があるスレッド時間です。";
  const root = $("frames"); root.replaceChildren();
  for (const frame of result.report.frames) {
    const row = document.createElement("tr");
    for (const value of [frame.frameName, frame.exclusivePercent + "%", frame.inclusivePercent + "%", frame.matchStatus]) {
      const cell = document.createElement("td"); cell.textContent = String(value); row.append(cell);
    } root.append(row);
  }
  message("選択スレッドを読み込みました。ダッシュボードの実測優先レポートにも反映されています。");
});
bind("compare", async form => {
  if (!capture) throw new Error("先に保存結果を読み込んでください。");
  const result = await api("/traces/compare-binary", { reportPath: capture.reportPath, assemblyPath: String(new FormData(form).get("assemblyPath")).trim() });
  $("comparison").textContent = result.sameRecordedInputs ? "記録した入力ファイルと一致しました。全依存ライブラリ・C#ソースの一致を示すものではありません。" : "保存時と入力ファイルが異なります。同一ビルドとして扱わないでください。";
});
function node(tag, text) { const n = document.createElement(tag); n.textContent = String(text); return n; }
function table(parent, title, headers, rows) {
  parent.append(node("h4", title)); const wrap = document.createElement("div"); wrap.className = "table-scroll";
  const t = document.createElement("table"); const head = document.createElement("tr");
  for (const h of headers) head.append(node("th", h)); t.append(head);
  for (const row of rows) { const tr = document.createElement("tr"); for (const value of row) tr.append(node("td", value ?? "不明")); t.append(tr); }
  wrap.append(t); parent.append(wrap);
}
const ms = v => Number.isFinite(v) ? v.toFixed(3) : "未計測";
function renderRuntime(report) {
  const root = $("runtime-results"); root.replaceChildren();
  $("runtime-status").textContent = "状態: " + report.status + " / 読み取りイベント: " + report.eventsRead + " / 報告された欠損: " + report.reportedEventsLost;
  root.append(node("p", "CPU時間・その他の待機時間は未計測です。以下の時間を合算・差し引きしてCPU時間を計算しないでください。"));
  if (!report.runtimes.length) root.append(node("p", "対応するイベントが観測されていません。GCや待機がゼロとは判断できません。"));
  for (const r of report.runtimes) {
    const section = document.createElement("article"); section.append(node("h3", "PID " + r.processId + " / CLR " + r.clrInstanceId));
    table(section, "別々の測定値", ["項目", "観測値"], [
      ["対応するGC開始/終了", r.collectionsCompleted + " / 開始 " + r.collectionsStarted],
      ["GC停止区間の合計 / 最大", ms(r.gcSuspensionWindowTotalMs) + " / " + ms(r.maximumGcSuspensionWindowMs) + " ms（停止準備・再開を含む）"],
      ["完全停止フェーズの合計", ms(r.fullySuspendedTotalMs) + " ms / " + r.fullySuspendedIntervals + " 区間"],
      ["割り当てtick", r.allocationTicks + " 件 / tickで報告された総量 " + r.tickReportedAllocationBytes + " bytes"],
      ["Monitor競合待ちの合計", ms(r.contentionWaitMilliseconds) + " ms / " + r.contentionDurationsReported + " 件（スレッド間の重複あり）"]]);
    for (const hint of r.investigationHints) section.append(node("p", hint.priority + " / " + hint.kind + " — " + hint.evidence + " " + hint.nextCheck));
    table(section, "GC停止区間（同じトレース内の相対時刻）", ["開始ms", "終了ms", "区間ms", "完全停止ms"], r.pauses.map(p => [ms(p.startMs), ms(p.endMs), ms(p.suspensionWindowMs), ms(p.fullySuspendedMs)]));
    table(section, "割り当てtickを発生させた型（全割り当ての型別集計ではありません）", ["型", "tick件数", "観測された最後のオブジェクトのbytes合計"], r.allocationTriggerTypes.map(t => [t.triggerType, t.tickCount, t.observedTriggerObjectBytes]));
    table(section, "競合待ちスレッド", ["TID", "Duration件数", "待機合計ms", "最大ms"], r.contentionThreads.map(t => [t.threadId, t.durationsReported, ms(t.waitMilliseconds), ms(t.maximumWaitMilliseconds)]));
    const detail = document.createElement("details"); detail.append(node("summary", "欠損・収集範囲・表示制限"), node("pre", JSON.stringify({ coverage: r.coverage, incompleteEvidence: r.incompleteEvidence, detailsTruncated: r.detailsTruncated }, null, 2))); section.append(detail); root.append(section);
  }
  const notes = document.createElement("details"); notes.append(node("summary", "証拠の識別情報と制約"), node("pre", JSON.stringify({ traceSha256: report.traceSha256, recordedInputs: report.recordedInputs, sourceAssociation: report.sourceAssociation, limitations: report.limitations }, null, 2))); root.append(notes);
}
bind("runtime-costs", async form => {
  if (!capture || capture.status !== "trace_completed" || runtimeController) throw new Error("完了した取得結果を選択してください。解析の同時実行はできません。");
  const currentId = capture.id, values = Object.fromEntries(new FormData(form));
  const body = { reportPath: capture.reportPath, maxRows: 100 };
  if (values.processId.trim()) body.processId = Number(values.processId);
  if (values.pauseBudgetMs.trim()) body.pauseBudgetMs = Number(values.pauseBudgetMs);
  runtimeController = new AbortController(); $("runtime-button").disabled = true; $("runtime-cancel").disabled = false;
  try {
    const report = await api("/traces/runtime-costs", body, runtimeController.signal);
    if (capture?.id !== currentId) return;
    runtimeReport = report; renderRuntime(report); $("runtime-export").disabled = false;
    message("元のnettraceからイベントを解析しました。アプリは再実行していません。");
  } finally { runtimeController = null; $("runtime-button").disabled = capture?.status !== "trace_completed"; $("runtime-cancel").disabled = true; }
});
$("runtime-cancel").addEventListener("click", () => runtimeController?.abort());
$("runtime-export").addEventListener("click", () => {
  if (!runtimeReport) return;
  const url = URL.createObjectURL(new Blob([JSON.stringify(runtimeReport, null, 2)], { type: "application/json" }));
  const link = document.createElement("a"); link.href = url; link.download = "tenka-runtime-costs.json"; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
});
api("/session").then(session => {
  csrf = session.csrfToken; enabled = session.trustedLocalExecutionEnabled === true; $("capture-button").disabled = !enabled;
  $("setup").textContent = enabled ? "ローカル実行は有効です。TENKA_DOTNET_TRACE_PATHに信頼済みdotnet-traceの絶対パスが必要です。" : "実行は無効です。許可する場合はTENKA_ENABLE_LOCAL_EXEC=1とTENKA_DOTNET_TRACE_PATHを設定してください。保存結果の解析には実行許可は不要です。";
}).catch(error => message(error.message, true));
