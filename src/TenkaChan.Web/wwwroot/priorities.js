"use strict";
const $ = id => document.getElementById(id);
let csrf = "", active = null, lastReport = null;
const evidenceNames = { compiler: "コンパイラー診断", analyzer: "Analyzer診断", semantic_fact: "言語仕様上の挙動", candidate: "静的候補・実害未確認", observed_profile: "実測で観測・原因未確定" };
const actions = { fix_build: "ビルドを修復", investigate: "優先して調査", review: "通常の確認", reference: "参考情報" };
function node(tag, text, className = "") { const n = document.createElement(tag); n.textContent = text; n.className = className; return n; }
function status(text, error = false) { $("priority-status").textContent = text; $("priority-status").dataset.error = String(error); }
async function api(path, body, signal) {
  const response = await fetch("/api" + path, { method: body === undefined ? "GET" : "POST", signal,
    headers: body === undefined ? {} : { "Content-Type": "application/json", "X-Tenka-CSRF": csrf },
    body: body === undefined ? undefined : JSON.stringify(body) });
  const data = await response.json();
  if (!response.ok) throw new Error(data.error || "APIエラー: " + response.status);
  return data;
}
function collectOptions() {
  const form = new FormData($("priority-form"));
  const suppressions = JSON.parse($("suppressions").value);
  if (!Array.isArray(suppressions)) throw new Error("非表示設定はJSON配列で指定してください。");
  return { hotSharePercent: Number(form.get("hotSharePercent")), minimumObservations: Number(form.get("minimumObservations")),
    staticSiteThreshold: Number(form.get("staticSiteThreshold")), maxItems: Number(form.get("maxItems")),
    priorityFilter: form.get("priorityFilter") || null, evidenceFilter: form.get("evidenceFilter") || null,
    includeSuppressed: form.has("includeSuppressed"), suppressions };
}
function render(report) {
  const summary = $("priority-summary"); summary.replaceChildren(); summary.hidden = false;
  summary.append(node("h2", "非表示・絞り込み前の集計"), node("p",
    "P1: " + report.priorityCounts.P1 + " / P2: " + report.priorityCounts.P2 + " / P3: " + report.priorityCounts.P3 +
    " ｜ 重複統合: " + report.duplicateCount + " ｜ 非表示: " + report.suppressedCount), node("p",
    "全 " + report.distinctCount + " 件 → 条件一致 " + report.matchingCount + " 件 → 表示 " + report.returnedCount + " 件"),
    node("p", "ポリシー: " + report.policyVersion + " / " + report.profileStatus + " / コンパイルエラー: " + report.compilerErrors));
  if (report.sourceTruncated || report.outputTruncated) summary.append(node("p", "元データまたは表示に件数上限があります。未表示は問題なしを意味しません。", "note"));
  if (report.unmatchedSuppressionCount) summary.append(node("p", "今回の結果にない非表示ID: " + report.unmatchedSuppressionCount, "note"));
  const root = $("priority-results"); root.replaceChildren();
  if (!report.items.length) root.append(node("p", "条件に一致する項目がありません。未検出・未観測の問題がないという意味ではありません。"));
  for (const item of report.items) {
    const card = node("article", "", "item" + (item.suppressed ? " suppressed" : ""));
    const chips = node("div", "", "chips");
    chips.append(node("span", item.priority + " / " + (actions[item.action] || item.action), "chip " + item.priority.toLowerCase()),
      node("span", evidenceNames[item.evidenceLevel] || item.evidenceLevel, "chip"), node("span", "問題確定ではありません", "chip"));
    card.append(chips, node("h2", item.title));
    if (item.file) card.append(node("p", item.file + ":" + item.line + (item.column ? ":" + item.column : "")));
    const lines = ["根拠: " + item.evidenceLevel, "影響の扱い: " + item.impact];
    if (item.selfWeight !== null) lines.push("実測Self: " + item.selfWeight + " " + item.unit + " (" + item.selfPercent + "%)", "Inclusive: " + item.inclusivePercent + "%", "観測数: " + item.observations + " / " + item.observationCountKind, "対応付け: " + item.profileAssociation);
    if (item.staticScore !== null) lines.push("静的スコア: " + item.staticScore + "（実測値とは合算しません）");
    if (item.originalSeverity) lines.push("元の診断レベル: " + item.originalSeverity);
    if (item.ruleIds.length) lines.push("ルール: " + item.ruleIds.join(", "));
    card.append(node("div", lines.join("\n"), "metrics"));
    const details = node("details", ""); details.append(node("summary", "判定理由と検証方法"));
    for (const reason of item.reasons) details.append(node("p", reason));
    details.append(node("p", "ID: " + item.id, "note")); card.append(details);
    if (item.suppressed) card.append(node("p", "非表示の理由: " + item.suppressionReason));
    const ignore = node("button", item.suppressed ? "非表示を解除" : "理由を付けて非表示"); ignore.type = "button";
    ignore.addEventListener("click", async () => {
      if (active) return;
      try {
        let list = JSON.parse($("suppressions").value);
        if (!Array.isArray(list)) throw new Error("非表示設定は配列で指定してください。");
        list = list.filter(x => x.id !== item.id);
        if (!item.suppressed) {
          const reason = window.prompt("この指摘を非表示にする理由（500文字以内）");
          if (reason === null) return;
          if (!reason.trim() || reason.length > 500) throw new Error("空でない理由を500文字以内で入力してください。");
          list.push({ id: item.id, reason: reason.trim() });
        }
        $("suppressions").value = JSON.stringify(list, null, 2); await build();
      } catch (error) { status(error.message, true); }
    }); card.append(ignore); root.append(card);
  }
  const notes = $("priority-notes"); notes.replaceChildren();
  for (const text of report.limitations) notes.append(node("p", text));
  $("priority-limitations").hidden = false;
}
async function build() {
  if (active || !$("priority-form").reportValidity()) return;
  let options;
  try { options = collectOptions(); } catch (error) { status(error.message, true); return; }
  const controller = new AbortController(); active = controller;
  $("priority-run").disabled = true; $("priority-cancel").disabled = false; $("priority-export").disabled = true;
  lastReport = null; $("priority-results").replaceChildren(); $("priority-summary").hidden = true; $("priority-limitations").hidden = true;
  status("現在のワークスペースの根拠を集計しています。");
  try {
    const session = await api("/session", undefined, controller.signal); csrf = session.csrfToken;
    const report = await api("/priorities/report", options, controller.signal);
    lastReport = report; render(report); $("priority-export").disabled = false;
    status("集計完了。優先度は調査の順番であり、不具合の確定度ではありません。");
  } catch (error) { status(error.name === "AbortError" ? "集計を中止しました。" : error.message, true); }
  finally { active = null; $("priority-run").disabled = false; $("priority-cancel").disabled = true; }
}
$("priority-form").addEventListener("submit", event => { event.preventDefault(); build(); });
$("priority-cancel").addEventListener("click", () => active?.abort());
$("priority-export").addEventListener("click", () => {
  if (!lastReport) return;
  const url = URL.createObjectURL(new Blob([JSON.stringify(lastReport, null, 2)], { type: "application/json" }));
  const anchor = document.createElement("a"); anchor.href = url; anchor.download = "tenka-review-priorities.json";
  document.body.append(anchor); anchor.click(); anchor.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000);
});
