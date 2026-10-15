"use strict";
(function (global) {
  function sampleLinks(data) {
    if (data?.available !== true || typeof data.projectPath !== "string" || !data.projectPath || data.projectPath.length > 4096)
      throw new Error("サンプルを取得できません。本体を更新・再起動し、setup.batで付属サンプルを選択してください。");
    return { start: "/workspace-build.html?" + new URLSearchParams({ project: data.projectPath }),
      project: data.projectPath, directory: data.sampleDirectory, dll: data.assemblyPath, traces: data.traceOutputDirectory };
  }
  async function load(doc, fetcher) {
    const status = doc.getElementById("guide-status");
    try {
      const result = await fetcher("/api/samples/game-lab", { method: "GET", credentials: "same-origin", cache: "no-store" });
      if (!result.ok) throw new Error("サンプルの場所を取得できません。本体を更新して再起動してください（HTTP " + result.status + "）。");
      const data = await result.json(), links = sampleLinks(data);
      doc.getElementById("guide-start").href = links.start;
      for (const key of ["project", "directory", "dll", "traces"])
        doc.getElementById("guide-" + key).textContent = links[key] || "未確認";
      status.textContent = data.assemblyExists
        ? "サンプルがあります。ビルド済みDLLもありますが、最新ソースとの一致を保証するものではありません。"
        : "サンプルが見つかりました。①からビルド・解析を始めてください。";
    } catch (error) { status.textContent = error.message; }
  }
  if (typeof module !== "undefined" && module.exports) module.exports = { sampleLinks, load };
  if (typeof document !== "undefined") load(document, (...args) => fetch(...args));
})(typeof globalThis !== "undefined" ? globalThis : this);
