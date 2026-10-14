"use strict";
(() => {
  const $ = id => document.getElementById(id);
  const state = { csrf: "", selected: null, dossier: null, serial: 0, busy: false };
  function node(tag, text, className) {
    const result = document.createElement(tag);
    if (text !== undefined) result.textContent = String(text);
    if (className) result.className = className;
    return result;
  }
  const fmt = value => typeof value === "number" ? value.toLocaleString("ja-JP", { maximumSignificantDigits: 6 }) : "未観測";
  const pct = value => typeof value === "number" ? value.toFixed(2) + "%" : "未観測";
  async function api(path, body) {
    const response = await fetch("/api" + path, {
      method: body === undefined ? "GET" : "POST", cache: "no-store",
      headers: body === undefined ? { Accept: "application/json" } :
        { Accept: "application/json", "Content-Type": "application/json", "X-Tenka-CSRF": state.csrf },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    const result = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(result.error || "HTTP " + response.status);
    return result;
  }
  async function action(operation) {
    if (state.busy) return;
    state.busy = true;
    $("error").hidden = true;
    document.querySelector("main").setAttribute("aria-busy", "true");
    try { await operation(); }
    catch (error) { $("error").textContent = error.message; $("error").hidden = false; }
    finally { state.busy = false; document.querySelector("main").removeAttribute("aria-busy"); }
  }
  function section(parent, title, content) {
    const part = node("section"); part.append(node("h3", title));
    part.append(content instanceof Node ? content : node("p", content)); parent.append(part);
  }
  function listText(items) {
    const result = node("div"); for (const item of items || []) result.append(node("p", item)); return result;
  }
  function clearDossier() {
    state.serial++; state.selected = null; state.dossier = null;
    $("export").disabled = true; $("method-title").textContent = "メソッドの根拠";
    $("dossier").replaceChildren(node("div", "メソッドを選択してください。", "empty-mini"));
  }
  async function refreshTriage() {
    const report = await api("/performance/triage", { ranking: $("ranking").value, limit: 100 });
    clearDossier();
    const summary = $("profile-summary"); summary.replaceChildren();
    const metrics = [report.status, "順位基準: " + report.rankingBasis,
      "観測単位: " + (report.unit || "なし"), "全体: " + fmt(report.totalWeight),
      "対応不明のSelf: " + fmt(report.unmappedExclusiveWeight), "空スタック: " + fmt(report.unassignedWeight),
      "観測されたメソッド: " + report.observedMethods,
      "候補表示: " + report.entries.length + " / " + report.totalCandidates];
    if (report.profileFramesTruncated) metrics.push("フレーム表示は省略あり・メソッド集計は全フレーム対象");
    if (report.staticDetailsTruncated) metrics.push("静的な詳細には取得上限あり");
    if (report.profileSha256) metrics.push("プロファイルSHA256: " + report.profileSha256);
    metrics.forEach(text => summary.append(node("span", text)));
    const holder = $("triage-list"); holder.replaceChildren();
    if (!report.entries.length) holder.append(node("div", "現在の観測結果・静的候補はありません。", "empty-mini"));
    for (const entry of report.entries) {
      const button = node("button", undefined, "item-row"); button.type = "button";
      const content = node("div"); content.append(node("strong", entry.displayName));
      content.append(node("small", "Self " + pct(entry.exclusivePercent) + " / Inclusive " + pct(entry.inclusivePercent)));
      content.append(node("small", "静的スコア " + fmt(entry.staticScore) + " · " + entry.observationStatus));
      button.append(content); button.addEventListener("click", () => action(() => openMethod(entry.methodId)));
      holder.append(button);
    }
    $("unmatched").replaceChildren(...report.unmatchedFrames.map(frame =>
      node("p", frame.frameName + " — Self " + pct(frame.exclusivePercent) + " · " + frame.matchStatus)));
  }
  async function openMethod(methodId, compiled) {
    const serial = ++state.serial;
    const report = await api("/performance/investigate", { methodId, ...(compiled || {}) });
    if (serial !== state.serial) return;
    state.selected = methodId; state.dossier = report; $("export").disabled = false;
    renderDossier(report);
  }
  function renderDossier(report) {
    $("method-title").textContent = report.method.method.signature;
    const holder = $("dossier"); holder.replaceChildren();
    section(holder, "読み込み済みソース", node("pre", report.method.source));
    section(holder, "ソースの識別", report.method.method.file + ":" + report.method.method.line +
      " · method text SHA256: " + report.sourceTextSha256);
    if (report.profile) {
      const p = report.profile, cost = p.methodCost;
      section(holder, "ワークロードの観測", p.name + " · " + p.unit + " · " + p.association +
        " · Self " + fmt(cost?.exclusiveWeight) + " / Inclusive " + fmt(cost?.inclusiveWeight));
      section(holder, "観測の出典", "profile #" + p.profileIndex + " · SHA256 " + p.sha256);
    } else section(holder, "ワークロードの観測", "プロファイルが未読込です。実行コストは未確認です。");
    section(holder, "静的候補", listText(report.staticSites.map(site => site.category + " · " + site.expression + " · " + site.verification)));
    section(holder, "制御フロー", (report.method.blocks?.length || 0) + " CFG blocks · " + (report.method.analysisNote || ""));
    const build = report.compiledEvidence;
    if (build) {
      const il = build.ilAndSource;
      section(holder, "保存したビルド", build.targetFramework + " · " + build.assemblyFile +
        " · token 0x" + il.metadataToken.toString(16) + " · DLL SHA256 " + il.assemblySha256 + " · MVID " + il.moduleVersionId);
      section(holder, "PDB検証", il.pdbStatus + " · PDB SHA256 " + (il.pdbSha256 || "埋め込み／未取得"));
      const sourceStatuses = [...new Set(il.instructions.filter(i => i.source).map(i => i.source.localSourceStatus))];
      section(holder, "現在のソースとの照合", sourceStatuses.join(" / ") || "対応するソース範囲なし");
      section(holder, "IL命令とPDBソース範囲", node("pre", il.instructions.slice(0, 600).map(i =>
        "IL_" + i.offset.toString(16).padStart(4, "0") + "  " + i.opCode + " " + i.operand +
        (i.source ? "  // " + i.source.document + ":" + i.source.startLine + "-" + i.source.endLine + " [" + i.source.localSourceStatus + "]" : "  // " + i.sourceMappingStatus)).join("\n")));
      const verified = [...new Set(il.instructions.map(i => i.source?.verifiedSourceExcerpt).filter(Boolean))];
      if (verified.length) section(holder, "チェックサム一致のソース抜粋", node("pre", verified.slice(0, 10).join("\n\n")));
      for (const native of build.nativeMethods) {
        section(holder, "JIT命令: " + native.jitSignature,
          native.architecture + " · " + native.jitMode + " · offset validation " + native.offsetValidation);
        section(holder, "機械語（先頭600命令）", node("pre", native.instructions.slice(0, 600).map(i =>
          (i.nativeOffset === null ? "????" : i.nativeOffset.toString(16).padStart(4, "0")) + "  " +
          i.encodedBytes + "  " + i.mnemonic + " " + i.operands).join("\n")));
      }
      if (!build.nativeMethods.length) section(holder, "JIT命令", "対応を確認できたネイティブメソッドはありません。");
      section(holder, "対応付けの限界", listText(build.associations));
      if (il.truncated) section(holder, "取得上限", "IL命令は一部のみ返されています。表示はさらに600命令までです。完全な取得結果はJSONをご確認ください。");
    }
    section(holder, "解釈上の制約", listText(report.limitations));
  }
  function setOptions(select, items, placeholder) {
    select.replaceChildren(); const first = node("option", placeholder); first.value = ""; select.append(first);
    for (const item of items) { const option = node("option", item.label); option.value = String(item.value); select.append(option); }
    select.disabled = items.length === 0;
  }
  async function refreshExperiments() {
    const items = await api("/experiments?limit=100");
    setOptions($("experiment"), items.filter(x => x.kind === "jit" && x.status === "jit_completed")
      .map(x => ({ value: x.id, label: (x.targetFramework || "") + " · " + x.projectRelativePath + " · " + x.id })), "JIT実験を選択");
    setOptions($("assembly"), [], "DLLを選択"); setOptions($("binary-method"), [], "メソッドを選択");
  }
  async function loadAssemblies() {
    setOptions($("assembly"), [], "DLLを選択"); setOptions($("binary-method"), [], "メソッドを選択");
    const id = $("experiment").value; if (!id) return;
    const files = await api("/performance/experiments/" + encodeURIComponent(id) + "/files");
    setOptions($("assembly"), files.filter(f => f.fileName.endsWith(".dll"))
      .map(f => ({ value: f.fileName, label: f.fileName + " · " + f.sha256.slice(0, 12) })), "DLLを選択");
  }
  async function loadMethods() {
    setOptions($("binary-method"), [], "メソッドを選択");
    const id = $("experiment").value, file = $("assembly").value; if (!id || !file) return;
    const result = await api("/performance/experiments/" + encodeURIComponent(id) + "/methods?" + new URLSearchParams({ assemblyFile: file }));
    setOptions($("binary-method"), result.methods.map(m => ({ value: m.metadataToken, label: "0x" + m.metadataToken.toString(16) + " · " + m.signature })), "メソッドを選択");
    if (result.truncated) { $("error").textContent = "先頭200件のみ表示中です。MCP jit_binary_methodsの検索で対象を絞れます。"; $("error").hidden = false; }
  }
  document.addEventListener("DOMContentLoaded", () => {
    $("triage-form").addEventListener("submit", event => { event.preventDefault(); action(refreshTriage); });
    $("profile-form").addEventListener("submit", event => {
      event.preventDefault(); const form = Object.fromEntries(new FormData(event.currentTarget));
      action(async () => {
        await api("/hotspots/profile/import", { filePath: form.filePath.trim(), profileIndex: Number(form.profileIndex), maxFrames: 100 });
        await refreshTriage();
      });
    });
    $("refresh-experiments").addEventListener("click", () => action(refreshExperiments));
    $("experiment").addEventListener("change", () => action(loadAssemblies));
    $("assembly").addEventListener("change", () => action(loadMethods));
    $("attach-form").addEventListener("submit", event => {
      event.preventDefault(); action(async () => {
        if (!state.selected) throw new Error("先に調査するメソッドを選んでください。");
        const methodToken = Number($("binary-method").value);
        if (!Number.isInteger(methodToken) || methodToken <= 0) throw new Error("バイナリのメソッドを選択してください。");
        await openMethod(state.selected, { jitExperimentId: $("experiment").value, assemblyFile: $("assembly").value, methodToken });
      });
    });
    $("export").addEventListener("click", () => {
      if (!state.dossier) return;
      const url = URL.createObjectURL(new Blob([JSON.stringify(state.dossier, null, 2)], { type: "application/json" }));
      const link = node("a"); link.href = url; link.download = "tenka-performance-investigation.json";
      document.body.append(link); link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000);
    });
    action(async () => {
      const session = await api("/session"); state.csrf = session.csrfToken;
      const overview = await api("/overview");
      $("connection").textContent = overview.loaded ? "読み込み済み: " + overview.path : "先にダッシュボードでプロジェクトを読み込んでください。";
      await refreshExperiments(); if (overview.loaded) await refreshTriage();
    });
  });
})();
