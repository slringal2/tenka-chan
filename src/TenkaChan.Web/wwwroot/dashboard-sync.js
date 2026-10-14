"use strict";

// Read-only bridge between the preparation workbench and the existing dashboard.
// No workspace/open, scan, build, JIT, test or LLM mutation is sent by this module.
function dashboardRevision(overview) {
  return JSON.stringify([overview?.loaded, overview?.snapshotId, overview?.path,
    overview?.lastOpened, overview?.lastScanned, overview?.findingCount,
    overview?.diagnosticErrors, overview?.diagnosticWarnings,
    overview?.deterministicComplete, overview?.deterministicFindingCount,
    overview?.hotspotCandidates, overview?.hotspotMeasuredFrames]);
}
function dashboardPathValue(current, previousAutomatic, openedPath, dirty = false) {
  if (dirty && current !== openedPath) return current;
  if (!openedPath) return current; // Keep the user's path for a retry after restart/failure.
  return !current || current === previousAutomatic || current === openedPath ? openedPath : current;
}
function dashboardDllSelection(assemblies, explicitlySelected) {
  if (assemblies.some(a => a.assemblyPath === explicitlySelected)) return explicitlySelected;
  return assemblies.length === 1 ? assemblies[0].assemblyPath : "";
}
function dashboardStageText(overview, hotspots) {
  if (!overview?.loaded)
    return "対象プロジェクトは未読込、またはビルド・再読み込み中です。セットアップだけでは対象コードの解析は始まりません。";
  const scan = overview.scanned ? `${overview.findingCount} 件の候補` : "未実行";
  const deterministic = overview.deterministicComplete ? `${overview.deterministicFindingCount} 件の指摘` : "未実行";
  const hotspot = hotspots === undefined ? "確認中" : hotspots?.staticAnalysis ? `${overview.hotspotCandidates} 箇所の候補` : "未実行";
  return `静的スキャン: ${scan} / 決定論的解析: ${deterministic} / 性能ホットスポット: ${hotspot}\n静的な調査候補です。JIT・GC・実測性能の測定結果とは別です。`;
}
class DashboardRefreshSuperseded extends Error {}
class DashboardSyncController {
  constructor({ read, apply, unchanged = () => {}, busy = () => false, context = () => ({}) }) {
    Object.assign(this, { read, apply, unchanged, busy, context });
    this.revision = null;
    this.pending = null;
    this.epoch = 0;
  }
  invalidate() { this.epoch++; this.revision = null; }
  refresh(force = false) {
    if (this.busy()) return Promise.resolve(false);
    if (this.pending) return this.pending; // Coalesce focus/visibility/manual requests.
    this.pending = this.load(force).finally(() => { this.pending = null; });
    return this.pending;
  }
  async load(force) {
    const epoch = this.epoch;
    const first = await this.read("/overview");
    if (typeof first?.loaded !== "boolean") throw new Error("解析状態の応答が不正です。");
    if (first.loaded && (typeof first.snapshotId !== "string" || !first.snapshotId))
      throw new Error("ワークスペースの識別情報がありません。");
    const revision = dashboardRevision(first);
    if (!force && revision === this.revision) { this.unchanged(first); return false; }
    const context = this.context();
    let bundle = { overview: first, context, quick: null, targets: [], methods: null,
      diagnostics: null, deterministic: null, hotspots: null, findings: null };
    if (first.loaded) {
      const methodsPath = "/methods?" + new URLSearchParams({ q: context.methodQuery || "", limit: "80" });
      const results = await Promise.all([
        this.read("/workspace/quick-config"), this.read("/methods?limit=80"),
        this.read("/diagnostics"), this.read("/deterministic/report"), this.read("/hotspots/report"),
        context.view === "methods" && context.methodQuery ? this.read(methodsPath) : null,
        context.view === "findings" ? this.read("/findings?" + new URLSearchParams({
          q: context.findingQuery || "", category: context.findingCategory || "all", offset: "0", limit: "80"
        })) : null
      ]);
      [bundle.quick, bundle.targets, bundle.diagnostics, bundle.deterministic,
        bundle.hotspots, bundle.methods, bundle.findings] = results;
      if (bundle.quick?.snapshotId !== first.snapshotId || !Array.isArray(bundle.quick.assemblies) ||
          !Array.isArray(bundle.quick.jitMethods) || !Array.isArray(bundle.targets))
        throw new DashboardRefreshSuperseded("解析対象が変わりました。「結果を更新」で再取得してください。");
    }
    // Existing endpoints are separate reads, not an atomic server transaction.
    // Never publish a bundle when a detectable workspace/scan change occurred mid-read.
    const last = await this.read("/overview");
    if (dashboardRevision(last) !== revision || this.epoch !== epoch || this.busy())
      throw new DashboardRefreshSuperseded("解析状態が更新中です。完了後にもう一度結果を取得します。");
    await this.apply(bundle);
    this.revision = revision;
    return true;
  }
}

function installDashboardSync() {
  if (typeof state === "undefined" || !document.getElementById("dashboard-refresh")) return;
  const get = id => document.getElementById(id);
  const path = get("workspace-path");
  const refreshButton = get("dashboard-refresh");
  let automaticPath = "", selectedDll = "", quickRevision = "", activeOperations = 0;
  let disposed = false, timer = null, refreshAfterOperation = null;
  let lastSnapshot = state.overview?.snapshotId || null;
  let lastHotspots;
  let pathDirty = false;
  path.addEventListener("input", () => { pathDirty = true; });
  function status(message) { get("dashboard-sync-status").textContent = message; }
  function renderSummary() {
    const next = dashboardPathValue(path.value, automaticPath, state.overview?.path, pathDirty);
    if (state.overview?.loaded) {
      if (next === state.overview.path) { automaticPath = next; pathDirty = false; }
      path.value = next;
    }
    get("dashboard-stage-summary").textContent = dashboardStageText(state.overview, lastHotspots);
  }
  function clearAssociations() {
    state.selectedMethod = null; state.targets = []; state.quick = null;
    state.performanceTriage = null;
    renderPerformanceTriage();
    for (const id of ["hyp-method", "hotspot-dossier-method", "hotspot-dossier-jit",
      "hotspot-dossier-local-jit", "hotspot-evidence-jit", "hotspot-evidence-local-jit",
      "hotspot-evidence-benchmark-id"]) get(id).value = "";
    const dialog = get("method-dialog");
    if (dialog.open) dialog.close();
    empty(get("hotspot-dossier-result"), "解析対象が更新されました。メソッドを選び直してください。");
    empty(get("methods-list"), "解析対象が更新されました。メソッド一覧を再取得してください。");
    empty(get("findings-list"), "解析対象が更新されました。検出候補を再取得してください。");
    selectedDll = ""; quickRevision = "";
  }
  function renderQuick(quick) {
    const fingerprint = JSON.stringify(quick);
    if (fingerprint === quickRevision) return; // Do not overwrite manual fields on a no-op refresh.
    quickRevision = fingerprint;
    state.quick = quick;
    const assemblies = quick?.assemblies || [];
    const list = get("quick-dll");
    clear(list);
    if (assemblies.length !== 1) {
      const option = el("option", "", assemblies.length ? "DLL候補を選択してください" : "DLL候補なし");
      option.value = ""; add(list, option);
    }
    for (const a of assemblies) {
      const option = el("option", "", `${a.project} / ${a.configuration} / ${a.targetFramework} / ${a.runnable ? "実行用候補" : "Library"}`);
      option.value = a.assemblyPath; add(list, option);
    }
    list.value = dashboardDllSelection(assemblies, selectedDll);
    get("quick-static").disabled = !state.overview?.loaded;
    syncQuickMethods();
    if (!quick || !assemblies.length || !list.value) {
      get("quick-jit").disabled = true;
      get("quick-status").textContent = !state.overview?.loaded ? "プロジェクトを読み込んでください。"
        : assemblies.length ? "DLL候補が複数あります。対象を選択してからJIT取得へ進んでください。"
        : "DLL候補はありません。静的解析は可能です。「ビルドと解析の準備」で出力を作成できます。";
    }
  }
  function renderDiagnostics(report) {
    const root = get("diagnostics-list"); clear(root);
    if (!state.overview?.scanned) return empty(root, "静的スキャンは未実行です。");
    if (!report?.diagnostics?.length) return empty(root, "コンパイラのエラー・警告はありません。");
    for (const entry of report.diagnostics.slice(0, 12)) {
      const row = el("div", "diagnostic-item");
      add(row, el("div", "diagnostic-level" + (entry.severity === "Error" ? " error" : ""), entry.severity));
      const detail = el("div", "diagnostic-detail", entry.diagnosticId + " · " + entry.message);
      add(detail, el("span", "", entry.file ? entry.file + ":" + entry.line : entry.project));
      row.append(detail); root.append(row);
    }
    if (report.diagnostics.length > 12) add(root, el("p", "inline-help", "先頭12件を表示しています。"));
  }
  async function read(apiPath) {
    const abort = new AbortController();
    const deadline = setTimeout(() => abort.abort(), 15000);
    try {
      const response = await fetch("/api" + apiPath, { method: "GET", credentials: "same-origin", cache: "no-store",
        headers: { Accept: "application/json" }, signal: abort.signal });
      const data = await response.json();
      if (!response.ok) throw new Error(data?.error || `HTTP ${response.status}`);
      return data;
    } finally { clearTimeout(deadline); }
  }
  const controller = new DashboardSyncController({ read,
    busy: () => activeOperations > 0 || !state.session || disposed,
    context: () => ({ view: state.view, methodQuery: get("method-search").value.trim(),
      findingQuery: get("finding-search").value.trim(), findingCategory: get("finding-category").value }),
    apply: bundle => {
      if (disposed) return;
      const snapshot = bundle.overview.loaded ? bundle.overview.snapshotId : null;
      if (snapshot !== lastSnapshot) { clearAssociations(); lastSnapshot = snapshot; }
      state.overview = bundle.overview; state.targets = bundle.targets;
      state.deterministic = bundle.deterministic;
      state.hotspotStatic = bundle.hotspots?.staticAnalysis || null;
      state.hotspotMeasured = bundle.hotspots?.sampledProfile || null;
      lastHotspots = bundle.hotspots;
      renderOverview(); renderDiagnostics(bundle.diagnostics);
      renderDeterministic(); renderHotspotReports(); renderQuick(bundle.quick);
      if (state.view === "methods" && get("method-search").value.trim() === bundle.context.methodQuery) {
        const items = bundle.methods || bundle.targets;
        const root = get("methods-list"); clear(root);
        for (const item of items) add(root, methodRow(item));
        if (!items.length) empty(root, bundle.overview.loaded ? "一致するメソッドはありません。" : "プロジェクトを読み込んでください。");
      }
      if (bundle.findings && state.view === "findings" &&
        get("finding-search").value.trim() === bundle.context.findingQuery &&
        get("finding-category").value === bundle.context.findingCategory) {
        const root = get("findings-list"); clear(root);
        get("finding-total").textContent = count(bundle.findings.total) + " 件";
        for (const item of bundle.findings.items) add(root, findingRow(item));
        state.findingsOffset = bundle.findings.items.length;
        if (state.findingsOffset < bundle.findings.total) {
          const more = el("button", "button button-outline", "さらに表示");
          more.dataset.loadMore = "true";
          more.addEventListener("click", () => withButton(more, () => loadFindings(true))); add(root, more);
        }
        if (!bundle.findings.total) empty(root, "該当する候補はありません。");
      }
      renderSummary();
      status(bundle.overview.loaded ? "同じサーバーの解析結果を反映しました。読み込み直す必要はありません。"
        : "対象の解析結果はありません。プロジェクトを読み込んでください。ビルド中の場合は完了を待って結果を更新してください。");
    }
  });
  async function refresh(force = false) {
    if (disposed || document.hidden) return;
    if (force) status("解析結果を再取得しています…（解析は実行しません）");
    refreshButton.disabled = true;
    try { await controller.refresh(force); }
    catch (error) {
      status(error instanceof DashboardRefreshSuperseded ? error.message
        : "結果の再取得が完了していません。前回の表示が残っている場合があります: " + error.message);
    } finally { refreshButton.disabled = false; }
  }
  // Existing explicit actions keep their behavior. Only observe their lifetime and
  // suppress refresh application while an action is changing the workspace.
  const originalWithButton = withButton;
  withButton = async function(button, operation) {
    activeOperations++; controller.invalidate();
    try { return await originalWithButton(button, operation); }
    finally {
      activeOperations--;
      if (!activeOperations && !disposed) {
        clearTimeout(refreshAfterOperation);
        refreshAfterOperation = setTimeout(() => refresh(true), 0);
      }
    }
  };
  const originalUpdateQuickConfig = updateQuickConfig;
  updateQuickConfig = async function() {
    await originalUpdateQuickConfig();
    quickRevision = "";
    renderQuick(state.quick);
  };
  const originalRenderOverview = renderOverview;
  renderOverview = function() { originalRenderOverview(); renderSummary(); };
  get("quick-dll").addEventListener("change", () => { selectedDll = get("quick-dll").value; });
  refreshButton.addEventListener("click", () => refresh(true));
  window.addEventListener("focus", () => refresh(true));
  document.addEventListener("visibilitychange", () => { if (!document.hidden) refresh(true); });
  function route() {
    const view = window.location.hash.slice(1);
    if (["overview", "findings", "deterministic", "hotspots", "methods", "evidence", "autoreview", "experiments"].includes(view))
      showView(view);
  }
  window.addEventListener("hashchange", route);
  window.addEventListener("pageshow", () => { disposed = false; refresh(true); });
  window.addEventListener("pagehide", () => { disposed = true; controller.invalidate(); });
  timer = window.setInterval(() => refresh(false), 5000);
  route(); renderSummary();
  // initialize() has its own asynchronous session request; the periodic observer
  // also covers initial loading even when that request has not completed yet.
  refresh(true);
}
if (typeof module !== "undefined" && module.exports) module.exports = {
  dashboardRevision, dashboardPathValue, dashboardDllSelection, dashboardStageText,
  DashboardSyncController, DashboardRefreshSuperseded
};
if (typeof document !== "undefined") document.addEventListener("DOMContentLoaded", installDashboardSync);
