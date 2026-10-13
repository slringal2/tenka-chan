"use strict";

const state = {
  csrf: "", session: null, overview: null, view: "overview",
  selectedMethod: null, targets: [], findingsOffset: 0
};
const $ = id => document.getElementById(id);

function el(tag, className, value) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (value !== undefined && value !== null) element.textContent = String(value);
  return element;
}
function add(parent, child) { parent.append(child); return parent; }
function clear(parent) { parent.replaceChildren(); }
function clip(value, limit = 130) {
  const text = String(value ?? "");
  return text.length <= limit ? text : text.slice(0, limit) + "…";
}
function shortId(value) { return value ? String(value).slice(0, 10) + "…" : "—"; }
function count(value) { return Number.isFinite(value) ? value.toLocaleString("ja-JP") : "—"; }
function empty(parent, message) {
  clear(parent);
  add(parent, el("div", "empty-mini", message));
}
function badge(value, warn = false, danger = false) {
  return el("span", "status-pill" + (danger ? " danger" : warn ? " warn" : ""), value);
}
function toast(message, isError = false) {
  const root = $("toast-container");
  add(root, el("div", "toast" + (isError ? " error" : ""), message));
  while (root.children.length > 4) root.firstElementChild.remove();
  const entry = root.lastElementChild;
  setTimeout(() => entry.remove(), 5500);
}
async function api(path, options = {}) {
  const method = options.method || "GET";
  const headers = { Accept: "application/json" };
  if (method !== "GET") {
    headers["X-Tenka-CSRF"] = state.csrf;
    headers["Content-Type"] = "application/json";
  }
  const response = await fetch("/api" + path, {
    method, headers, cache: "no-store",
    body: options.body === undefined ? undefined : JSON.stringify(options.body)
  });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || "HTTP " + response.status);
  return data;
}
async function withButton(button, operation) {
  if (button?.disabled) return;
  if (button) button.disabled = true;
  try { return await operation(); }
  catch (error) { toast(error.message || "操作に失敗しました。", true); }
  finally { if (button) button.disabled = false; }
}

function showView(view) {
  const routes = { overview: "Dashboard", findings: "Findings", methods: "Methods",
    evidence: "Evidence", autoreview: "AI Review", experiments: "Experiments" };
  if (!Object.hasOwn(routes, view)) return;
  state.view = view;
  document.querySelectorAll(".view").forEach(item => { item.hidden = item.id !== "view-" + view; });
  document.querySelectorAll(".nav-item").forEach(item => {
    const active = item.dataset.view === view;
    item.classList.toggle("active", active);
    if (active) item.setAttribute("aria-current", "page");
    else item.removeAttribute("aria-current");
  });
  $("breadcrumb").textContent = routes[view];
  if (view === "findings") loadFindings(false).catch(error => toast(error.message, true));
  if (view === "methods") loadTargets().catch(error => toast(error.message, true));
  if (view === "evidence") loadHypotheses().catch(error => toast(error.message, true));
  if (view === "autoreview") {
    refreshReview().catch(error => toast(error.message, true));
    loadReviewHistory().catch(error => toast(error.message, true));
  }
  if (view === "experiments") loadExperiments().catch(error => toast(error.message, true));
  window.scrollTo(0, 0);
}

function renderOverview() {
  const o = state.overview;
  const loaded = !!o?.loaded;
  $("metric-projects").textContent = loaded ? count(o.projectCount) : "—";
  $("metric-methods").textContent = loaded ? count(o.methodCount) : "—";
  $("metric-findings").textContent = loaded && o.scanned ? count(o.findingCount) : "—";
  $("nav-findings").textContent = loaded && o.scanned ? count(o.findingCount) : "—";
  $("scan-button").disabled = !loaded;
  $("workspace-help").textContent = loaded
    ? "読み込み済み: " + o.path + (o.scanned ? " • 最終スキャン: " + new Date(o.lastScanned).toLocaleString("ja-JP") : " • 未スキャン")
    : state.session?.msbuildEnabled
      ? "許可済みのソリューションを選択してください。MSBuildの読み込みは有効です。"
      : "TENKA_ALLOWED_ROOTS と TENKA_TRUST_MSBUILD=1 の設定が必要です。";
  $("diagnostics-summary").textContent = !o?.scanned ? "未スキャン"
    : "エラー " + o.diagnosticErrors + " / 警告 " + o.diagnosticWarnings;
  renderCategoryBreakdown();
  renderTopTargets();
  updateCategories();
}
function renderCategoryBreakdown() {
  const root = $("category-breakdown");
  clear(root);
  const entries = state.overview?.findingsByCategory ?? [];
  if (!entries.length) return empty(root, state.overview?.scanned
    ? "今回のスキャンでは候補が見つかりませんでした。" : "スキャン後に集計を表示します。");
  const max = Math.max(1, ...entries.map(entry => entry.count));
  for (const entry of entries) {
    const row = el("div", "category-row");
    const head = el("div", "category-row-head");
    add(head, el("span", "", entry.category));
    add(head, el("span", "muted", count(entry.count) + " 件"));
    const track = el("div", "category-bar");
    const fill = el("span");
    fill.style.width = (entry.count * 100 / max).toFixed(1) + "%";
    add(track, fill);
    row.append(head, track);
    add(root, row);
  }
}
function updateCategories() {
  const selector = $("finding-category");
  const previous = selector.value || "all";
  clear(selector);
  const all = el("option", "", "すべてのカテゴリ");
  all.value = "all";
  add(selector, all);
  for (const item of state.overview?.findingsByCategory ?? []) {
    const option = el("option", "", item.category);
    option.value = item.category;
    add(selector, option);
  }
  if ([...selector.options].some(option => option.value === previous))
    selector.value = previous;
}

function methodRow(item) {
  const button = el("button", "item-row clickable");
  button.type = "button";
  const left = el("div");
  add(left, el("strong", "", item.method.signature));
  add(left, el("small", "", item.method.project + " • " + clip(item.method.file, 70) + ":" + item.method.line));
  const right = el("div", "item-meta");
  add(right, el("span", "score", item.priority.toFixed(1)));
  add(right, el("span", "item-chevron", "↗"));
  button.append(left, right);
  button.addEventListener("click", () => openMethod(item.method.id));
  return button;
}
function renderTopTargets() {
  const root = $("overview-targets");
  clear(root);
  if (!state.overview?.loaded) return empty(root, "ワークスペースが未選択です。");
  if (!state.targets.length) return empty(root, "メソッドが見つかりません。");
  for (const item of state.targets.slice(0, 5)) add(root, methodRow(item));
}
async function loadOverview() {
  state.overview = await api("/overview");
  renderOverview();
  await Promise.allSettled([loadHypotheses(true), loadDiagnostics()]);
}
async function loadTargets() {
  const query = $("method-search").value.trim();
  if (!state.overview?.loaded) {
    state.targets = [];
    empty($("methods-list"), "ワークスペースを読み込んでください。");
    renderTopTargets();
    return;
  }
  const result = await api("/methods?" + new URLSearchParams({ q: query, limit: "80" }));
  if (!query) {
    state.targets = result;
    renderTopTargets();
  }
  const root = $("methods-list");
  clear(root);
  if (!result.length) return empty(root, "一致するメソッドはありません。");
  for (const item of result) add(root, methodRow(item));
  if (result.length >= 80)
    add(root, el("p", "inline-help", "先頭80件を表示中。検索で対象を絞り込めます。"));
}

function findingRow(item) {
  const root = el("article", "finding-card");
  const left = el("div");
  const title = el("div", "item-rule");
  add(title, el("span", "rule-code", item.ruleId));
  add(title, el("span", "rule-pill", item.category));
  add(left, title);
  add(left, el("h3", "", item.message));
  add(left, el("p", "", "検証方法: " + item.verification));
  add(left, el("div", "finding-location", item.file + ":" + item.line + ":" + item.column));
  add(root, left);
  if (item.methodId) {
    const inspect = el("button", "finding-action", "解析する ↗");
    inspect.addEventListener("click", () => openMethod(item.methodId));
    add(root, inspect);
  } else add(root, badge("要調査", true));
  return root;
}
async function loadFindings(append) {
  if (!append) state.findingsOffset = 0;
  const search = new URLSearchParams({
    q: $("finding-search").value.trim(), category: $("finding-category").value || "all",
    offset: String(state.findingsOffset), limit: "80"
  });
  const data = await api("/findings?" + search);
  const root = $("findings-list");
  if (!append) clear(root);
  $("finding-total").textContent = count(data.total) + " 件";
  root.querySelector("[data-load-more]")?.remove();
  for (const item of data.items) add(root, findingRow(item));
  if (!data.total) return empty(root, state.overview?.scanned
    ? "該当する候補はありません。" : "先にRoslynスキャンを実行してください。");
  state.findingsOffset += data.items.length;
  if (state.findingsOffset < data.total) {
    const more = el("button", "button button-outline", "さらに表示（残り " + count(data.total - state.findingsOffset) + " 件）");
    more.dataset.loadMore = "true";
    more.addEventListener("click", () => withButton(more, () => loadFindings(true)));
    add(root, more);
  }
}
async function loadDiagnostics() {
  const root = $("diagnostics-list");
  if (!state.overview?.scanned) return empty(root, "スキャンを実行すると診断結果が表示されます。");
  const report = await api("/diagnostics");
  clear(root);
  if (!report?.diagnostics?.length) return empty(root, "コンパイラのエラー・警告はありません。");
  for (const entry of report.diagnostics.slice(0, 12)) {
    const row = el("div", "diagnostic-item");
    add(row, el("div", "diagnostic-level" + (entry.severity === "Error" ? " error" : ""), entry.severity));
    const detail = el("div", "diagnostic-detail", entry.diagnosticId + " · " + entry.message);
    add(detail, el("span", "", entry.file ? entry.file + ":" + entry.line : entry.project));
    add(row, detail);
    add(root, row);
  }
  if (report.diagnostics.length > 12) add(root,
    el("p", "inline-help", "先頭12件を表示しています。"));
}

function detailSection(parent, heading, content) {
  const section = el("section");
  add(section, el("h3", "", heading));
  add(section, content instanceof Node ? content : el("p", "", content));
  add(parent, section);
  return section;
}
function tags(items) {
  const root = el("div");
  if (!items?.length) add(root, el("p", "", "なし"));
  else for (const item of items) add(root, el("span", "tag", item));
  return root;
}
async function openMethod(id) {
  try {
    state.selectedMethod = id;
    const report = await api("/methods/" + encodeURIComponent(id));
    $("method-detail-title").textContent = report.method.signature;
    $("method-detail-sub").textContent = report.method.file + ":" + report.method.line;
    const root = $("method-detail-body");
    clear(root);
    const code = el("pre");
    add(code, el("code", "", report.source));
    detailSection(root, "Source code", code);
    detailSection(root, "Called methods", tags(report.callees));
    detailSection(root, "Dataflow • reads", tags(report.reads));
    detailSection(root, "Dataflow • writes", tags(report.writes));
    detailSection(root, "Control flow graph", report.blocks.length + " CFG blocks");
    if (report.diagnostics.length) detailSection(root, "Diagnostics", tags(report.diagnostics));
    if (report.analysisNote) detailSection(root, "解析メモ", report.analysisNote);
    $("method-dialog").showModal();
  } catch (error) { toast(error.message, true); }
}
async function showTestPlan() {
  if (!state.selectedMethod) return;
  await withButton($("method-plan-button"), async () => {
    const plan = await api("/methods/" + encodeURIComponent(state.selectedMethod) + "/plan");
    const root = $("method-detail-body");
    root.querySelector("[data-test-plan]")?.remove();
    const section = el("section");
    section.dataset.testPlan = "true";
    add(section, el("h3", "", "境界値テストの候補"));
    add(section, el("div", "json-note", "これは入力候補です。期待値は独立した仕様・不変条件に基づいて判断してください。"));
    for (const input of plan.inputs) {
      add(section, el("p", "", input.parameter + " (" + input.type + ")"));
      add(section, tags(input.cSharpExpressions));
      add(section, el("p", "muted", input.reason));
    }
    add(section, tags(plan.notes));
    add(root, section);
    section.scrollIntoView({ block: "nearest" });
  });
}

async function loadHypotheses(silent = false) {
  const entries = await api("/hypotheses?limit=80");
  const root = $("hypotheses-list");
  clear(root);
  if (!entries.length) empty(root, "仮説はまだ登録されていません。");
  for (const item of entries) {
    const button = el("button", "item-row clickable");
    const left = el("div");
    add(left, el("strong", "", clip(item.claim)));
    add(left, el("small", "", item.category + " • " + shortId(item.methodId) + " • " + new Date(item.updatedUtc).toLocaleString("ja-JP")));
    button.append(left, badge(item.status, ["suspected", "needs_review"].includes(item.status),
      item.status === "reported_reproduced"));
    button.addEventListener("click", () => openHypothesis(item.id));
    add(root, button);
  }
  $("metric-hypotheses").textContent = count(entries.filter(item =>
    item.status === "suspected" || item.status === "needs_review").length);
  if (!silent) toast("レビュー台帳を更新しました。");
}
async function openHypothesis(id) {
  try {
    const result = await api("/hypotheses/" + encodeURIComponent(id));
    $("evidence-detail-title").textContent = clip(result.hypothesis.claim, 90);
    const root = $("evidence-detail-body");
    clear(root);
    detailSection(root, "現在の状態", tags([result.hypothesis.status, result.hypothesis.category]));
    detailSection(root, "仮説", result.hypothesis.claim);
    detailSection(root, "失敗条件", result.hypothesis.failureCondition);
    if (result.hypothesis.verdictRationale) detailSection(root, "判定理由", result.hypothesis.verdictRationale);
    const list = el("div", "stack-list");
    if (!result.evidence.length) add(list, el("div", "empty-mini", "証拠はまだありません。"));
    for (const item of result.evidence) {
      const row = el("div", "item-row");
      const content = el("div");
      add(content, el("strong", "", item.kind + " · " + item.summary));
      add(content, el("small", "", "出典: " + item.provenance + (item.artifactId ? " · " + item.artifactId : "")));
      add(row, content);
      add(list, row);
    }
    detailSection(root, "証拠", list);
    $("evidence-dialog").showModal();
  } catch (error) { toast(error.message, true); }
}

async function loadExperiments() {
  const entries = await api("/experiments?limit=80");
  const root = $("experiments-list");
  clear(root);
  if (!entries.length) return empty(root, "保存された実験はありません。");
  for (const item of entries) {
    const row = el("div", "item-row");
    const left = el("div");
    add(left, el("strong", "", (item.kind === "test" ? "Test" : "Benchmark") + " · " + shortId(item.id)));
    add(left, el("small", "", item.projectRelativePath + " • " + item.status +
      (item.testsPassed !== null ? " • 成功 " + item.testsPassed + " / 失敗 " + item.testsFailed : "")));
    add(row, left);
    const actions = el("div", "item-actions");
    if (item.status === "prepared") {
      const button = el("button", "mini-button", "実行");
      button.disabled = !state.session?.dockerEnabled;
      button.title = button.disabled ? "TENKA_ENABLE_DOCKER=1 が必要です" : "Dockerで隔離実行";
      button.addEventListener("click", () => runExperiment(item.id, button));
      add(actions, button);
    } else {
      const button = el("button", "mini-button", "結果を見る");
      button.addEventListener("click", () => openExperiment(item.id));
      add(actions, button);
    }
    add(row, actions);
    add(root, row);
  }
}
async function runExperiment(id, button) {
  if (!window.confirm("生成したコードをDockerの隔離環境で実行します。よろしいですか？")) return;
  await withButton(button, async () => {
    toast("Dockerでの検証を開始しました。");
    const result = await api("/experiments/" + encodeURIComponent(id) + "/run", { method: "POST", body: {} });
    toast("実行結果: " + result.status, result.status.includes("failed") || result.status.includes("unverified"));
    await loadExperiments();
    await openExperiment(id);
  });
}
async function openExperiment(id) {
  try {
    const result = await api("/experiments/" + encodeURIComponent(id));
    $("experiment-detail-title").textContent = "実験 " + shortId(result.experimentId);
    const root = $("experiment-detail-body");
    clear(root);
    detailSection(root, "実行状態", tags([result.kind, result.status, "exit=" + (result.exitCode ?? "unknown")]));
    if (result.tests) {
      detailSection(root, "Test counters", tags([
        "Total " + result.tests.total, "Passed " + result.tests.passed,
        "Failed " + result.tests.failed, "Skipped " + result.tests.skipped]));
      const failed = el("div", "stack-list");
      for (const item of result.tests.failures ?? []) {
        const row = el("div", "item-row");
        const detail = el("div");
        add(detail, el("strong", "", item.name));
        add(detail, el("small", "", item.message || "詳細なし"));
        add(row, detail);
        add(failed, row);
      }
      if (failed.childNodes.length) detailSection(root, "失敗したテスト", failed);
    }
    if (result.benchmarks?.length) {
      const rows = el("div", "stack-list");
      for (const item of result.benchmarks) {
        const row = el("div", "item-row");
        const detail = el("div");
        add(detail, el("strong", "", item.caseId));
        const mean = Number.isFinite(item.meanNanoseconds)
          ? item.meanNanoseconds.toFixed(2) + " ns/op" : "未計測";
        const allocation = Number.isFinite(item.allocatedBytesPerOperation)
          ? item.allocatedBytesPerOperation.toFixed(0) + " B/op" : "不明";
        add(detail, el("small", "", "Mean " + mean + " • Allocation " + allocation +
          " • Samples " + item.sampleCount));
        add(row, detail);
        add(rows, row);
      }
      detailSection(root, "BenchmarkDotNet", rows);
    }
    if (result.output) detailSection(root, "Console output", el("pre", "", clip(result.output, 16000)));
    if (result.stderr) detailSection(root, "Standard error", clip(result.stderr, 6000));
    detailSection(root, "Evidence note", result.note);
    $("experiment-dialog").showModal();
  } catch (error) { toast(error.message, true); }
}
async function compareBenchmarks(event) {
  event.preventDefault();
  const form = event.currentTarget;
  await withButton(form.querySelector("button"), async () => {
    const result = await api("/benchmarks/compare", {
      method: "POST", body: Object.fromEntries(new FormData(form))
    });
    const root = $("comparison-result");
    clear(root);
    const block = el("div", "compare-card");
    add(block, el("strong", "", result.status));
    add(block, el("small", "", result.note));
    for (const item of result.cases) {
      const row = el("div", "item-row");
      const content = el("div");
      add(content, el("strong", "", item.caseId));
      const change = (item.meanChangePercent > 0 ? "+" : "") + item.meanChangePercent + "%";
      add(content, el("small", "", item.baselineMeanNanoseconds.toFixed(2) + " → " +
        item.candidateMeanNanoseconds.toFixed(2) + " ns/op (" + change + ")"));
      row.append(content, badge(item.status, item.status === "slower_observed"));
      add(block, row);
    }
    add(root, block);
  });
}


let reviewPolling = false;
let lastReviewStatus = null;

function activeReview(status) {
  return status && ["queued", "running", "cancelling"].includes(status.status);
}

function renderReview(status) {
  if (status) state.review = status;
  const current = state.review;
  const running = activeReview(current);
  const total = current?.totalMethods ?? 0;
  const completed = current?.completedMethods ?? 0;
  const percent = total ? Math.round(completed * 100 / total) : 0;

  $("auto-status").textContent = current?.status ?? "未開始";
  $("auto-completed").textContent = completed + " / " + total;
  $("auto-progress-percent").textContent = percent + "%";
  $("auto-progress-bar").style.width = percent + "%";
  $("auto-hypotheses").textContent = count(current?.hypothesisCount ?? 0);
  $("auto-errors").textContent = count(current?.errorCount ?? 0);
  $("auto-current").textContent = current?.currentMethod ||
    (current?.status === "completed" ? "すべての調査を終了しました。" : "待機中");
  $("auto-run-id").textContent = current ? "RUN ID: " + current.runId : "";
  $("auto-start").disabled = running || !state.overview?.loaded;
  $("auto-stop").disabled = !running || current?.status === "cancelling";
  if ($("open-button")) $("open-button").disabled = running;
  if ($("scan-button")) $("scan-button").disabled = running || !state.overview?.loaded;

  const root = $("auto-activity");
  clear(root);
  const activity = current?.activity ?? [];
  if (!activity.length) {
    empty(root, "レビューを開始すると履歴が表示されます。");
  } else {
    for (const entry of activity.slice(-45).reverse()) {
      const row = el("div", "activity-item");
      add(row, el("span", "activity-time", new Date(entry.timeUtc).toLocaleTimeString("ja-JP")));
      const details = el("div");
      add(details, el("strong", "", entry.kind));
      add(details, el("small", "", clip(entry.message, 350)));
      if (entry.hypothesisId) {
        const link = el("button", "text-link", "仮説を見る ↗");
        link.addEventListener("click", () => openHypothesis(entry.hypothesisId));
        add(details, link);
      }
      add(row, details);
      add(root, row);
    }
  }
}

async function refreshReview() {
  if (reviewPolling) return;
  reviewPolling = true;
  try {
    const status = await api("/reviews/current");
    if (status?.runId !== state.review?.runId ||
        status?.status !== lastReviewStatus ||
        status?.completedMethods !== state.review?.completedMethods) {
      if (status) state.review = status;
      lastReviewStatus = status?.status || null;
      renderReview(status);
      if (status && !activeReview(status) && state.view === "autoreview")
        await loadReviewHistory();
    }
  } finally { reviewPolling = false; }
}

async function loadReviewHistory() {
  const history = await api("/reviews?limit=20");
  const root = $("auto-history");
  clear(root);
  if (!history.length) return empty(root, "履歴はありません。");
  for (const run of history) {
    const button = el("button", "item-row clickable");
    const content = el("div");
    add(content, el("strong", "", run.model + " / " + run.status));
    add(content, el("small", "", new Date(run.startedUtc).toLocaleString("ja-JP") +
      " • " + run.completedMethods + "/" + run.totalMethods +
      " methods • " + run.hypothesisCount + " hypotheses"));
    button.append(content, badge(run.status, run.status !== "completed"));
    button.addEventListener("click", async () => {
      try {
        const detail = await api("/reviews/" + encodeURIComponent(run.runId));
        state.review = detail;
        renderReview(detail);
      } catch (error) { toast(error.message, true); }
    });
    add(root, button);
  }
}

async function startReview(event) {
  event.preventDefault();
  const button = $("auto-start");
  if (button.disabled) return;
  button.disabled = true;
  try {
    if (!state.overview?.loaded)
      throw new Error("先にC#プロジェクトを読み込んでください。");
    const form = event.currentTarget;
    const values = Object.fromEntries(new FormData(form));
    const maxMethods = Number(values.maxMethods);
    const maxMinutes = Number(values.maxMinutes);
    if (!Number.isInteger(maxMethods) || maxMethods < 1 || maxMethods > 30 ||
        !Number.isInteger(maxMinutes) || maxMinutes < 1 || maxMinutes > 30)
      throw new Error("対象メソッド数と時間は1〜30で指定してください。");
    const status = await api("/reviews/start", {
      method: "POST", body: { maxMethods, maxMinutes }
    });
    state.review = status;
    renderReview(status);
    toast("自律レビューを開始しました。");
    await loadReviewHistory();
  } catch (error) { toast(error.message, true); }
  finally { renderReview(state.review); }
}

async function stopReview() {
  const status = state.review;
  if (!activeReview(status)) return;
  await withButton($("auto-stop"), async () => {
    const result = await api("/reviews/" + encodeURIComponent(status.runId) + "/stop",
      { method: "POST", body: {} });
    state.review = result;
    renderReview(result);
    toast("レビューの停止を要求しました。");
  });
}
async function initialize() {
  document.querySelectorAll(".nav-item").forEach(button =>
    button.addEventListener("click", () => showView(button.dataset.view)));
  document.querySelectorAll("[data-go]").forEach(button =>
    button.addEventListener("click", () => showView(button.dataset.go)));
  $("close-method").addEventListener("click", () => $("method-dialog").close());
  $("close-evidence").addEventListener("click", () => $("evidence-dialog").close());
  $("close-experiment").addEventListener("click", () => $("experiment-dialog").close());
  $("method-plan-button").addEventListener("click", showTestPlan);
  $("method-hyp-button").addEventListener("click", () => {
    $("hyp-method").value = state.selectedMethod || "";
    $("method-dialog").close();
    showView("evidence");
    document.querySelector("#hypothesis-form textarea").focus();
  });
  $("refresh-hypotheses").addEventListener("click", () =>
    loadHypotheses().catch(error => toast(error.message, true)));
  $("refresh-experiments").addEventListener("click", () =>
    loadExperiments().catch(error => toast(error.message, true)));

  $("workspace-form").addEventListener("submit", event => {
    event.preventDefault();
    withButton($("open-button"), async () => {
      const path = $("workspace-path").value.trim();
      const summary = await api("/workspace/open", { method: "POST", body: { path } });
      toast("読み込み成功: " + summary.projects.length + " projects / " + summary.methodCount + " methods");
      await loadOverview();
      await loadTargets();
      const root = path.replace(/[\\/][^\\/]+$/, "");
      if (!$("experiment-root").value) $("experiment-root").value = root;
    });
  });
  $("scan-button").addEventListener("click", () =>
    withButton($("scan-button"), async () => {
      toast("Roslynのスキャンを開始しました。");
      const report = await api("/workspace/scan", { method: "POST", body: {} });
      toast(count(report.findingCount) + " 件の候補を検出しました。");
      await loadOverview();
      await loadFindings(false);
    }));
  let findingTimer, methodTimer;
  $("finding-search").addEventListener("input", () => {
    clearTimeout(findingTimer);
    findingTimer = setTimeout(() => loadFindings(false).catch(error => toast(error.message, true)), 220);
  });
  $("finding-category").addEventListener("change", () =>
    loadFindings(false).catch(error => toast(error.message, true)));
  $("method-search").addEventListener("input", () => {
    clearTimeout(methodTimer);
    methodTimer = setTimeout(() => loadTargets().catch(error => toast(error.message, true)), 250);
  });

  $("hypothesis-form").addEventListener("submit", event => {
    event.preventDefault();
    const form = event.currentTarget;
    withButton(form.querySelector("button[type=submit]"), async () => {
      if (!state.overview?.snapshotId)
        throw new Error("仮説を保存する前にワークスペースを開いてください。");
      const body = Object.fromEntries(new FormData(form));
      body.snapshotId = state.overview.snapshotId;
      await api("/hypotheses", { method: "POST", body });
      form.reset();
      toast("仮説を保存しました。");
      await loadHypotheses(true);
    });
  });
  $("experiment-form").addEventListener("submit", event => {
    event.preventDefault();
    const form = event.currentTarget;
    withButton(form.querySelector("button[type=submit]"), async () => {
      const body = Object.fromEntries(new FormData(form));
      const receipt = await api("/experiments/prepare", { method: "POST", body });
      toast("実験を準備しました: " + shortId(receipt.id));
      form.querySelector("textarea").value = "";
      await loadExperiments();
    });
  });
  $("auto-review-form").addEventListener("submit", startReview);
  $("auto-stop").addEventListener("click", stopReview);
  $("auto-history-refresh").addEventListener("click", () =>
    loadReviewHistory().catch(error => toast(error.message, true)));
  $("compare-form").addEventListener("submit", compareBenchmarks);

  try {
    state.session = await api("/session");
    state.csrf = state.session.csrfToken;
    $("auto-model").textContent = state.session.model || "local";
    $("auto-endpoint").textContent = state.session.llmEndpoint || "127.0.0.1";

    $("runtime-status").textContent = "ローカルサーバー接続済み";
    $("docker-note").textContent = state.session.dockerEnabled
      ? "Docker実行が有効です。生成コードは隔離コンテナ内で実行されます。"
      : "Docker実行は無効です。TENKA_ENABLE_DOCKER=1で明示的に有効にしてください。";
    await loadOverview();
    await loadExperiments();
    if (state.overview?.loaded) await loadTargets();
    await refreshReview();
    if (state.review) await loadReviewHistory();
    window.setInterval(() => {
      if (activeReview(state.review))
        refreshReview().catch(error => toast(error.message, true));
    }, 2000);

  } catch (error) {
    document.querySelector(".runtime-dot")?.classList.add("offline");
    $("runtime-status").textContent = "接続できません";
    toast("Web APIへの接続に失敗しました: " + error.message, true);
  }
}
document.addEventListener("DOMContentLoaded", initialize);
