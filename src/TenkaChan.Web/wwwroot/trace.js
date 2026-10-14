"use strict";
const $ = id => document.getElementById(id);
let csrf = "", capture = null, controller = null, enabled = false;
function message(text, error = false) { $("message").textContent = text; $("message").classList.toggle("error", error); }
async function api(path, body, signal) {
  const response = await fetch("/api" + path, {
    method: body === undefined ? "GET" : "POST", signal,
    headers: body === undefined ? {} : { "Content-Type": "application/json", "X-Tenka-CSRF": csrf },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const data = await response.json();
  if (!response.ok) throw new Error(data.error || "処理に失敗しました: " + response.status);
  return data;
}
function showCapture(result) {
  capture = result;
  $("report-path").value = result.reportPath;
  $("capture-status").textContent = "取得状態: " + result.status + " / " + result.targetFramework + " / " + result.measurementKind;
  $("metadata").textContent = JSON.stringify({ inputs: result.inputs, toolVersion: result.toolVersion,
    arguments: result.arguments, startedUtc: result.startedUtc, completedUtc: result.completedUtc,
    limitations: result.limitations, output: result.output }, null, 2);
  const select = $("profiles");
  select.replaceChildren(new Option("解析するスレッドを選択してください", ""));
  for (const profile of result.profiles || [])
    select.add(new Option(profile.index + ": " + profile.name + " / " + profile.unit + " / " + profile.observations + " records", String(profile.index)));
  select.disabled = result.status !== "trace_completed";
  $("import-button").disabled = select.disabled;
}
function bind(id, action) {
  $(id).addEventListener("submit", async event => {
    event.preventDefault();
    try { await action(event.currentTarget); }
    catch (error) { message(error.name === "AbortError" ? "中止を要求しました。サーバー側でプロセスツリーの終了を試みます。" : error.message, true); }
  });
}
bind("capture", async form => {
  if (!enabled) throw new Error("ローカル実行が無効です。");
  const values = Object.fromEntries(new FormData(form));
  const args = JSON.parse(values.arguments);
  if (!Array.isArray(args) || args.some(arg => typeof arg !== "string")) throw new Error("引数はJSON文字列配列で指定してください。");
  if (values.trustedBinary !== "on") throw new Error("実行への明示的な許可が必要です。");
  if (!confirm("このアプリをOSユーザーの権限で実行します。Dockerによる隔離はありません。実行しますか？")) return;
  controller = new AbortController();
  $("capture-button").disabled = true; $("cancel").disabled = false;
  message("アプリの実行と計測結果の変換を行っています。");
  try {
    const result = await api("/traces/capture", { assemblyPath: values.assemblyPath.trim(),
      outputDirectory: values.outputDirectory.trim(), timeoutSeconds: Number(values.timeoutSeconds),
      arguments: args, trustedBinary: true }, controller.signal);
    showCapture(result);
    message(result.status === "trace_completed" ? "取得しました。解析するスレッドを選択してください。" : "取得は完了していません。状態とログを確認してください。", result.status !== "trace_completed");
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
    }
    root.append(row);
  }
  message("選択スレッドを読み込みました。ダッシュボードの実測優先レポートにも反映されています。");
});
bind("compare", async form => {
  if (!capture) throw new Error("先に保存結果を読み込んでください。");
  const result = await api("/traces/compare-binary", { reportPath: capture.reportPath, assemblyPath: String(new FormData(form).get("assemblyPath")).trim() });
  $("comparison").textContent = result.sameRecordedInputs ? "記録した入力ファイルと一致しました。全依存ライブラリ・C#ソースの一致を示すものではありません。" : "保存時と入力ファイルが異なります。同一ビルドとして扱わないでください。";
});
api("/session").then(session => {
  csrf = session.csrfToken;
  enabled = session.trustedLocalExecutionEnabled === true;
  $("capture-button").disabled = !enabled;
  $("setup").textContent = enabled ? "ローカル実行は有効です。TENKA_DOTNET_TRACE_PATHに信頼済みdotnet-traceの絶対パスが必要です。" : "実行は無効です。許可する場合はTENKA_ENABLE_LOCAL_EXEC=1とTENKA_DOTNET_TRACE_PATHを設定してください。保存結果の確認には実行許可は不要です。";
}).catch(error => message(error.message, true));
