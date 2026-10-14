"use strict";
// A single navigation definition for the dashboard AND the four workbenches.
// Run as the first defer script, before existing DOMContentLoaded handlers attach.
(function (global) {
  const pages = [
    ["overview", "ダッシュボード", "◫", "/#overview", "解析の全体状況を見る"],
    ["findings", "検出候補", "◇", "/#findings", "気になるコードを探す"],
    ["deterministic", "決定論的解析", "▦", "/#deterministic", "規則とコンパイラーの根拠を確認"],
    ["hotspots", "性能ホットスポット", "◉", "/#hotspots", "重い処理の候補を絞る"],
    ["methods", "メソッド解析", "⌘", "/#methods", "ソースと呼び出しを調べる"],
    ["evidence", "仮説・証拠", "◎", "/#evidence", "推測と検証結果を分けて保存"],
    ["autoreview", "AI自律レビュー", "✧", "/#autoreview", "ローカルLLMに調査させる"],
    ["experiments", "テストと性能", "◈", "/#experiments", "正しさと性能の比較を行う"],
    ["project", "プロジェクト調査", "⌕", "/project.html", "クラスを選び、追加パスなしで調べる"],
    ["build", "ビルドと解析の準備", "▧", "/workspace-build.html", "対象を選んでビルド・解析"],
    ["assembly", "アセンブリ解析", "⌘", "/assembly.html", "DLLのIL・JIT命令を確認"],
    ["trace", "プロファイル取得", "◉", "/trace.html", "アプリを動かして負荷を観測"],
    ["priorities", "調査優先度", "◇", "/priorities.html", "次に調べる箇所を整理"],
    ["guide", "使い方・サンプル", "?", "/guide.html", "比較教材で使い方を覚える"]
  ];
  const aliases = { "/project": "project", "/workspace-build": "build", "/assembly": "assembly" };
  function route(path, hash) {
    if (path === "/" || path === "/index.html")
      return pages.slice(0, 8).some(p => p[0] === hash.slice(1)) ? hash.slice(1) : "overview";
    return aliases[path] || pages.find(p => p[3] === path)?.[0] || null;
  }
  function install(doc, location) {
    if (doc.body.dataset.tenkaShell === "ready") return;
    const dashboard = location.pathname === "/" || location.pathname === "/index.html";
    const active = route(location.pathname, location.hash);
    if (!active) return;
    const el = (tag, cls, text) => {
      const node = doc.createElement(tag); if (cls) node.className = cls;
      if (text !== undefined) node.textContent = text; return node;
    };
    let shell = doc.querySelector(".shell");
    if (!dashboard) {
      const work = doc.querySelector("body > main");
      if (!work) return;
      // Move existing nodes, never clone them or replace their IDs/form state.
      work.querySelector(":scope > nav")?.remove();
      const oldHeader = work.querySelector(":scope > header");
      oldHeader?.querySelectorAll(":scope > a").forEach(n => n.remove());
      oldHeader && [...oldHeader.childNodes].filter(n => n.nodeType === 3 && /^[\s·]*$/.test(n.textContent)).forEach(n => n.remove());
      work.classList.add("workbench");
      work.querySelectorAll(":scope > section").forEach(n => n.classList.add("panel"));
      work.querySelectorAll("button").forEach(n => {
        n.classList.add("button");
        n.classList.add(n.type === "submit" ? "button-primary" : "button-outline");
      });
      shell = el("div", "shell");
      const sidebar = el("aside", "sidebar"); sidebar.setAttribute("aria-label", "ナビゲーション");
      const brand = el("div", "brand");
      const mark = el("div", "brand-mark", "T"); mark.append(el("span", "", "✦")); brand.append(mark);
      const name = el("div"); name.append(el("div", "brand-name", "tenka."), el("div", "brand-sub", "DEEP REVIEW CONSOLE"));
      brand.append(name); sidebar.append(brand, el("div", "nav-label", "WORKSPACE"), el("nav", "nav-list"));
      const bottom = el("div", "sidebar-bottom");
      bottom.append(el("p", "sidebar-note", "同じサーバーの解析結果を共有します。画面移動だけでは処理を実行しません。"));
      sidebar.append(el("div", "sidebar-spacer"), bottom);
      const area = el("div", "main-area"), top = el("header", "topbar");
      const crumb = el("div", "breadcrumb");
      crumb.append(el("span", "muted", "TENKA / "), el("strong", "", pages.find(p => p[0] === active)[1]));
      const right = el("div", "topbar-right"); right.append(el("span", "privacy-chip", "● LOCAL ONLY"));
      top.append(crumb, right);
      const content = el("div", "content");
      doc.body.insertBefore(shell, work); content.append(work); area.append(top, content); shell.append(sidebar, area);
    }
    if (!shell) return;
    const nav = shell.querySelector(".nav-list");
    const oldCount = doc.getElementById("nav-findings")?.textContent || "—";
    nav.replaceChildren();
    pages.forEach((p, i) => {
      // Preserve existing dashboard event bindings: its first eight entries remain buttons.
      const n = el(dashboard && i < 8 ? "button" : "a", "nav-item");
      if (n.tagName === "BUTTON") { n.type = "button"; n.dataset.view = p[0]; }
      else n.href = p[3];
      n.dataset.page = p[0]; n.title = p[4];
      const icon = el("span", "nav-icon", p[2]); icon.setAttribute("aria-hidden", "true");
      n.append(icon, el("span", "", p[1]));
      if (p[0] === "findings") { const count = el("span", "nav-count", oldCount); count.id = "nav-findings"; n.append(count); }
      if (p[0] === active) { n.classList.add("active"); n.setAttribute("aria-current", "page"); }
      nav.append(n);
    });
    const guide = el("a", "shell-guide-link", "プロジェクト調査"); guide.href = "/project.html";
    shell.querySelector(".topbar-right")?.prepend(guide);
    if (dashboard) {
      const overview = doc.getElementById("view-overview");
      if (overview && !doc.getElementById("project-explorer-entry")) {
        const entry = el("article", "panel guide-banner"); entry.id = "project-explorer-entry";
        const copy = el("div");
        copy.append(el("h2", "", "まずはプロジェクト調査から"),
          el("p", "", "プロジェクトを一度選ぶと、クラス・参照・IL・JIT・計測へボタンで進めます。"));
        const link = el("a", "button button-primary", "クラスを選んで調べる"); link.href = "/project.html";
        entry.append(copy, link); overview.prepend(entry);
      }
    }
    doc.body.dataset.tenkaShell = "ready";
  }
  const api = { pages, route, install };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  global.TenkaShell = api;
  if (typeof document !== "undefined") install(document, location);
})(typeof globalThis !== "undefined" ? globalThis : this);
