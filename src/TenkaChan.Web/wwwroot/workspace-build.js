"use strict";

// Kept independent of the DOM so the actual HTTP/consent sequence can be tested with node:test.
class WorkspaceBuildClient {
  constructor(fetcher) {
    this.fetcher = fetcher;
    this.session = null;
    this.options = null;
    this.busy = false;
    this.lastResult = null;
  }
  async request(path, body) {
    const init = { method: body === undefined ? "GET" : "POST", credentials: "same-origin", headers: { Accept: "application/json" } };
    if (body !== undefined) {
      if (!this.session?.csrfToken) throw new Error("接続情報がありません。ページを再読み込みしてください。");
      init.headers["Content-Type"] = "application/json";
      init.headers["X-Tenka-CSRF"] = this.session.csrfToken;
      init.body = JSON.stringify(body);
    }
    const response = await this.fetcher(path, init);
    let data;
    try { data = await response.json(); }
    catch { throw new Error(`応答を読み取れませんでした（HTTP ${response.status}）。`); }
    if (!response.ok) {
      const error = new Error(data?.error || `HTTP ${response.status}`);
      error.status = response.status;
      throw error; // Never retry a build automatically: MSBuild may have already changed files.
    }
    return data;
  }
  async connect() {
    this.session = await this.request("/api/session");
    return this.refresh();
  }
  async refresh() {
    if (this.busy) throw new Error("操作中です。完了後に更新してください。");
    this.options = null;
    const options = await this.request("/api/workspace/build-options");
    if (!options || !Array.isArray(options.projects)) throw new Error("プロジェクト候補の応答が不正です。");
    this.options = options;
    return options;
  }
  async open(path) {
    if (this.busy) throw new Error("操作中です。");
    if (typeof path !== "string" || !path.trim()) throw new Error("プロジェクトの絶対パスを入力してください。");
    this.busy = true;
    this.options = null;
    try { await this.request("/api/workspace/open", { path: path.trim() }); }
    finally { this.busy = false; }
    return this.refresh();
  }
  async build(values, confirmBuild) {
    if (this.busy) throw new Error("ビルドはすでに処理中です。追加の実行は予約していません。");
    const options = this.options;
    if (!options?.loaded || !options.snapshotId) throw new Error("先にプロジェクトを読み込んでください。");
    if (!options.buildEnabled) throw new Error("ローカルビルドが無効です。信頼済みプロジェクト用の起動設定を確認してください。");
    if (!options.projects.some(p => p.path === values.projectPath)) throw new Error("読み込み済みのプロジェクトを選択してください。");
    const timeoutSeconds = values.timeoutSeconds ?? 180;
    if (!Number.isInteger(timeoutSeconds) || timeoutSeconds < 10 || timeoutSeconds > 300)
      throw new Error("ビルドの制限時間は10〜300秒の整数にしてください。");
    if (typeof confirmBuild !== "function") throw new Error("ビルドの承認が必要です。");
    this.busy = true;
    try {
      const approved = await confirmBuild(
        "このプロジェクトを隔離なしでReleaseビルドします。独自のMSBuild処理は現在のユーザー権限で動きます。\n" +
        values.projectPath + "\nNuGet復元: " + (values.allowRestore === true ? "許可" : "無効（独自処理の通信は制限されません）") +
        "\n実行してよろしいですか？");
      if (approved !== true) return null;
      this.lastResult = null;
      this.options = null; // A failed or interrupted request may still mutate output files.
      const result = await this.request("/api/workspace/build", {
        expectedSnapshotId: options.snapshotId,
        projectPath: values.projectPath,
        targetFramework: values.targetFramework?.trim() || null,
        allowRestore: values.allowRestore === true,
        approveUnisolatedBuild: true,
        analyzeAfterBuild: values.analyzeAfterBuild !== false,
        timeoutSeconds
      });
      if (!result?.build || typeof result.build.status !== "string")
        throw new Error("ビルド結果の応答が不正です。成功とは判断していません。");
      this.lastResult = result;
      return result;
    } finally { this.busy = false; }
  }
}

function describeWorkspaceBuild(result, requestedAnalysis) {
  const status = result.build.status;
  const labels = {
    build_failed: "ビルドに失敗しました。下のログを確認してください。",
    timed_out: "ビルドが制限時間に達しました。子プロセスの終了はベストエフォートです。",
    cancelled: "ビルドは中止されました。",
    dotnet_unavailable: "dotnetを起動できませんでした。SDKとPATHを確認してください。",
    build_io_error: "ビルド中にファイルまたは入出力のエラーが発生しました。"
  };
  if (status !== "build_succeeded") return labels[status] || `未完了のビルド状態: ${status}`;
  if (!result.workspaceReloaded) return "ビルドは成功しましたが、ソースの再読み込みは完了していません。";
  if (requestedAnalysis && !result.analysis) return "ビルドと再読み込みは成功しましたが、まとめて解析は完了していません。";
  if (result.analysis) {
    const a = result.analysis;
    return `ビルドと静的解析が完了しました。指摘 ${a.findings}件 / コンパイラーエラー ${a.compilerErrors}件 / 決定論的指摘 ${a.deterministicFindings}件 / 性能調査候補 ${a.hotspotCandidates}件。`;
  }
  return "Releaseビルドとソースの再読み込みが完了しました。";
}

function startWorkspaceBuildPage() {
  const $ = id => document.getElementById(id);
  const client = new WorkspaceBuildClient((...args) => fetch(...args));
  let pageBusy = false;
  function status(message) { $("build-status").textContent = message; }
  function controls(busy) {
    pageBusy = busy;
    for (const item of document.querySelectorAll("input, select, button")) item.disabled = busy;
    $("build-button").disabled = busy || !client.options?.loaded || !client.options?.buildEnabled;
    $("save-result").disabled = busy || !client.lastResult;
  }
  function renderOptions(options) {
    const select = $("build-project");
    const previous = select.value;
    select.replaceChildren();
    if (options.path) $("workspace-path").value = options.path;
    if (options.projects.length !== 1) select.add(new Option("ビルドするプロジェクトを選択", ""));
    for (const p of options.projects) select.add(new Option(`${p.name} — ${p.path}`, p.path));
    if (options.projects.some(p => p.path === previous)) select.value = previous;
    if (!options.loaded) status("信頼済みプロジェクトを読み込んでください。");
    else if (!options.buildEnabled) status("ローカルビルドが無効です。TENKA_TRUST_MSBUILD=1 と TENKA_ENABLE_LOCAL_EXEC=1 が必要です。");
    else status("ビルドの準備ができました。実行前に確認画面が表示されます。");
  }
  function renderResult(result, requestedAnalysis) {
    $("build-result").hidden = false;
    const summary = describeWorkspaceBuild(result, requestedAnalysis);
    $("result-summary").textContent = summary;
    $("result-note").textContent = result.note + (result.build.outputTruncated ? " ログは末尾のみを保持しています。" : "");
    $("build-output").textContent = result.build.output || "（出力なし）";
    $("build-stderr").textContent = result.build.stderr || "（出力なし）";
    const list = $("build-assemblies");
    list.replaceChildren();
    for (const a of result.assemblies || []) {
      const entry = document.createElement("p");
      entry.textContent = `${a.project} / ${a.targetFramework} / ${a.configuration} / ${a.runnable ? "実行用候補" : "ライブラリまたは直接実行不可"}\n${a.assemblyPath}`;
      list.append(entry);
    }
    if (!list.childElementCount) list.textContent = "DLL候補はありません。カスタム出力先が検出対象外の場合もあります。";
    status(summary);
  }
  async function refresh() { renderOptions(await client.refresh()); }
  $("refresh-options").addEventListener("click", async () => {
    if (pageBusy) return;
    controls(true);
    try { await refresh(); } catch (error) { status(error.message); } finally { controls(false); }
  });
  $("open-workspace").addEventListener("submit", async event => {
    event.preventDefault();
    if (pageBusy) return;
    controls(true); status("プロジェクトを読み込んでいます…");
    $("build-result").hidden = true;
    client.lastResult = null;
    try { renderOptions(await client.open($("workspace-path").value)); }
    catch (error) { status(error.message); }
    finally { controls(false); }
  });
  $("build-workspace").addEventListener("submit", async event => {
    event.preventDefault();
    if (pageBusy) return;
    const values = {
      projectPath: $("build-project").value, targetFramework: $("build-framework").value,
      allowRestore: $("allow-restore").checked, analyzeAfterBuild: $("analyze-after-build").checked,
      timeoutSeconds: Number($("build-timeout").value)
    };
    controls(true);
    let result = null;
    let errorMessage = null;
    try {
      result = await client.build(values, message => {
        const approved = window.confirm(message);
        if (approved) { $("build-result").hidden = true; status("Releaseビルドと解析の準備を実行しています…"); }
        return approved;
      });
    } catch (error) { errorMessage = error.message; }
    finally {
      // Only read state after completion. Never resubmit a failed/cancelled build.
      try { await refresh(); } catch (error) { errorMessage = errorMessage || error.message; }
      if (result) renderResult(result, values.analyzeAfterBuild);
      if (errorMessage) status(errorMessage);
      controls(false);
    }
  });
  $("save-result").addEventListener("click", () => {
    if (!client.lastResult) return;
    const url = URL.createObjectURL(new Blob([JSON.stringify(client.lastResult, null, 2)], { type: "application/json" }));
    const link = document.createElement("a");
    link.href = url; link.download = "tenka-workspace-build.json"; link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  });
  controls(true);
  client.connect().then(renderOptions).catch(error => status(error.message)).finally(() => controls(false));
}

if (typeof module !== "undefined" && module.exports) module.exports = { WorkspaceBuildClient, describeWorkspaceBuild };
if (typeof document !== "undefined") {
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", startWorkspaceBuildPage);
  else startWorkspaceBuildPage();
}
