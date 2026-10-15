"use strict";
(function(global) {
  const executable = new Set(["build", "jit", "trace", "run-companion"]);
  const actions = new Set(["type", "type-references", "method", "method-references", "test-plan", "scan", "priorities", "binary-methods", "il", "jit", "trace", "result", "native", "compare-native", "runtime-costs", "import-thread", "run-companion", "build"]);
  const pickOne = (items, previous) => items.some(i => i.id === previous) ? previous : items.length === 1 ? items[0].id : "";
  // Names are display labels only; selections always retain per-project IDs.
  function projectLabel(project, projects) {
    if (!project) return "未選択";
    if (projects.filter(p => p.name === project.name).length < 2) return project.name;
    const parts = String(project.path || "").replace(/\\/g, "/").split("/");
    const others = projects.filter(p => p.name === project.name && p.id !== project.id);
    for (let depth = 2; depth <= parts.length; depth++) {
      const suffix = parts.slice(-depth).join("/");
      if (!others.some(p => String(p.path || "").replace(/\\/g, "/").split("/").slice(-depth).join("/") === suffix))
        return project.name + " — " + suffix;
    }
    return project.name + " — " + parts.slice(-2).join("/") + " (" + project.id.slice(0, 8) + ")";
  }
  function projectDependsOn(projects, fromId, targetId) {
    const queue = [fromId], visited = new Set();
    const byId = new Map(projects.map(p => [p.id, p]));
    while (queue.length) {
      const id = queue.pop();
      if (id === targetId) return true;
      if (visited.has(id)) continue;
      visited.add(id);
      for (const reference of byId.get(id)?.projectReferenceIds || []) queue.push(reference);
    }
    return false;
  }
  class StaleExplorerResponse extends Error { constructor() { super("対象が変わったため、古い応答は表示しませんでした。"); this.name = "StaleExplorerResponse"; } }
  class ProjectExplorerClient {
    constructor(fetcher) {
      this.fetcher = fetcher; this.session = null; this.context = null;
      this.projectId = ""; this.scopeProjectId = ""; this.assemblyId = ""; this.launchId = "";
      this.type = null; this.method = null; this.binary = null;
      this.lastArtifact = null; this.baseline = null; this.lastResult = null;
      this.busy = false; this.stale = false; this.epoch = 0; this.active = null; this.searchAbort = null;
    }
    async request(path, body, signal) {
      const headers = { Accept: "application/json" };
      if (body !== undefined) {
        if (!this.session?.csrfToken) throw new Error("接続情報がありません。状態を更新してください。");
        headers["Content-Type"] = "application/json"; headers["X-Tenka-CSRF"] = this.session.csrfToken;
      }
      const r = await this.fetcher(path, { method: body === undefined ? "GET" : "POST", headers,
        body: body === undefined ? undefined : JSON.stringify(body), credentials: "same-origin", cache: "no-store", signal });
      let data; try { data = await r.json(); } catch { throw new Error("サーバーの応答を読み取れませんでした。成功とは判断していません。"); }
      if (!r.ok) { const error = new Error(data?.error || `HTTP ${r.status}`); error.status = r.status; throw error; }
      return data;
    }
    project() { return this.context?.projects.find(p => p.id === this.projectId); }
    assemblies() { return (this.context?.assemblies || []).filter(a => a.projectId === this.projectId); }
    launchers() { return (this.context?.assemblies || []).filter(a => a.runnable); }
    launcherCandidates(projectId = this.projectId) {
      if (!projectId) return [];
      return this.launchers().filter(a => projectDependsOn(this.context?.projects || [], a.projectId, projectId));
    }
    setScope(id) {
      if (this.busy) throw new Error("処理の完了を待ってください。");
      if (!this.context || (id && !this.context.projects.some(p => p.id === id))) throw new Error("検索範囲を選んでください。");
      this.invalidateRead(); this.scopeProjectId = id;
    }
    invalidateRead() { this.epoch++; this.searchAbort?.abort(); this.searchAbort = null; }
    clearSource() { this.type = null; this.method = null; this.binary = null; }
    setProject(id) {
      if (this.busy) throw new Error("処理の完了を待ってください。");
      if (!this.context || (id && !this.context.projects.some(p => p.id === id))) throw new Error("プロジェクトを選んでください。");
      this.invalidateRead(); this.projectId = id; this.clearSource();
      this.assemblyId = pickOne(this.assemblies(), "");
      const local = this.launchers().filter(a => a.projectId === id);
      this.launchId = local.length === 1 ? local[0].id : pickOne(this.launcherCandidates(id), "");
    }
    replaceContext(data) {
      if (!data?.snapshotId || !Array.isArray(data.projects) || !Array.isArray(data.assemblies) || !Array.isArray(data.companions))
        throw new Error("プロジェクト一覧の応答が不正です。");
      const oldProjectPath = this.project()?.path;
      const oldScopePath = this.context?.projects.find(p => p.id === this.scopeProjectId)?.path;
      const oldAssembly = this.context?.assemblies.find(a => a.id === this.assemblyId)?.assemblyPath;
      const changed = this.context?.snapshotId !== data.snapshotId;
      this.invalidateRead(); this.context = data; this.stale = false;
      this.projectId = data.projects.find(p => p.path === oldProjectPath)?.id || pickOne(data.projects, this.projectId);
      this.scopeProjectId = data.projects.find(p => p.path === oldScopePath)?.id || "";
      if (changed) { this.clearSource(); this.lastArtifact = null; this.baseline = null; }
      const outputs = this.assemblies();
      this.assemblyId = outputs.find(a => a.assemblyPath === oldAssembly)?.id || pickOne(outputs, this.assemblyId);
      const ownApps = this.launchers().filter(a => a.projectId === this.projectId);
      const stillSelected = this.launchers().some(a => a.id === this.launchId) ? this.launchId : "";
      this.launchId = stillSelected || (ownApps.length === 1 ? ownApps[0].id : pickOne(this.launcherCandidates(), ""));
    }
    async refresh() {
      if (this.busy) throw new Error("処理の完了を待ってください。");
      const epoch = ++this.epoch;
      const session = await this.request("/api/session");
      const overview = await this.request("/api/overview");
      if (epoch !== this.epoch) throw new StaleExplorerResponse();
      this.session = session;
      if (!overview.loaded) {
        this.context = null; this.clearSource(); this.projectId = ""; this.scopeProjectId = ""; this.assemblyId = ""; this.launchId = "";
        this.lastArtifact = null; this.baseline = null; this.stale = false; return null;
      }
      const context = await this.request("/api/explorer/context");
      if (epoch !== this.epoch) throw new StaleExplorerResponse();
      this.replaceContext(context); return context;
    }
    async open(path) {
      if (this.busy) throw new Error("処理中です。追加の読み込みは予約しません。");
      if (typeof path !== "string" || !path.trim()) throw new Error("最初にプロジェクトを選択してください。");
      this.invalidateRead(); this.busy = true; this.active = new AbortController(); this.clearSource();
      try {
        await this.request("/api/workspace/open", { path: path.trim() }, this.active.signal);
      } finally { this.busy = false; this.active = null; }
      return this.refresh();
    }
    async types(query = "", offset = 0) {
      if (!this.context || this.stale) return null;
      if (query.length > 200) throw new Error("検索は200文字までです。");
      this.searchAbort?.abort(); const controller = new AbortController(); this.searchAbort = controller;
      const epoch = this.epoch, snapshot = this.context.snapshotId;
      const params = new URLSearchParams({ snapshotId: snapshot, projectId: this.scopeProjectId, q: query, offset: String(offset) });
      const result = await this.request("/api/explorer/types?" + params, undefined, controller.signal);
      if (epoch !== this.epoch || snapshot !== result.snapshotId) throw new StaleExplorerResponse();
      if (!Array.isArray(result.items)) throw new Error("クラス一覧の応答が不正です。");
      return result;
    }
    async perform(action, extra = {}, confirmExecution) {
      if (!actions.has(action)) throw new Error("対応していない操作です。");
      if (this.busy) throw new Error("処理中です。二重実行は予約しません。");
      if (!this.context || this.stale) throw new Error("プロジェクトを読み込むか、状態を更新してください。");
      if (action === "build" && !this.project()) throw new Error("ビルドするプロジェクトを選んでください。");
      const methodId = extra.methodId || this.method?.id;
      if (["method", "method-references", "test-plan", "binary-methods", "il", "jit"].includes(action) && !methodId)
        throw new Error("調査するメソッドを選んでください。");
      const typeId = extra.typeId || this.type?.id;
      if (["type", "type-references"].includes(action) && !typeId) throw new Error("クラスを選んでください。");
      const assemblyId = ["jit", "trace"].includes(action) ? this.launchId : this.assemblyId;
      if (["binary-methods", "il", "jit", "trace"].includes(action) && !this.context.assemblies.some(a => a.id === assemblyId))
        throw new Error("対象のビルド出力を一覧から選んでください。まだない場合はReleaseビルドを実行してください。");
      if (["jit", "trace"].includes(action) && !this.launchers().some(a => a.id === assemblyId))
        throw new Error("実行できるアプリを選んでください。ライブラリだけでは起動できません。");
      const body = { snapshotId: this.context.snapshotId, action, projectId: this.projectId || null,
        typeId: typeId || null, methodId: methodId || null, assemblyId: assemblyId || null };
      // Deliberate allowlist: no path, command, token or arbitrary code can be added by form serialization.
      for (const key of ["companionId", "artifactId", "baselineId", "nativeIndex", "baselineIndex", "profileIndex", "metadataToken", "assemblySha256", "allowRestore", "arguments"])
        if (extra[key] !== undefined) body[key] = extra[key];
      if (action === "run-companion" && !this.context.companions.some(c => c.id === body.companionId))
        throw new Error("テストまたはベンチマークを選んでください。");
      if (body.arguments && (!Array.isArray(body.arguments) || body.arguments.length > 16 || body.arguments.some(a => typeof a !== "string" || a.length > 1024 || /[\0\r\n]/.test(a))))
        throw new Error("引数は1行ずつ、最大16個・各1,024文字で入力してください。");
      this.busy = true;
      try {
        if (executable.has(action)) {
          if (typeof confirmExecution !== "function" || await confirmExecution(body) !== true) return null;
          body.approved = true;
        }
        this.invalidateRead(); this.active = new AbortController();
        const data = await this.request("/api/explorer/action", body, this.active.signal);
        if (action === "type") {
          if (!data?.detail?.type?.id || !Array.isArray(data.detail.methods)) throw new Error("クラス詳細の応答が不正です。");
          this.type = data.detail.type;
          if (this.method?.typeId !== this.type.id) { this.method = null; this.binary = null; }
        }
        if (action === "method") {
          if (!data?.method?.id || !data?.inspection) throw new Error("メソッド詳細の応答が不正です。");
          this.method = data.method; this.binary = null;
        }
        if (action === "binary-methods") this.binary = { ...data, methodId };
        if (data?.kind && data?.id && data?.data) this.lastArtifact = data;
        this.lastResult = data;
        return data;
      } catch (error) {
        if (error.status === 409 || (body.approved && ["build", "run-companion"].includes(action))) { this.stale = true; this.clearSource(); }
        throw error;
      } finally { this.busy = false; this.active = null; }
    }
    cancel() { this.active?.abort(); this.searchAbort?.abort(); this.invalidateRead(); }
  }
  const api = { ProjectExplorerClient, StaleExplorerResponse, pickOne, projectLabel, projectDependsOn, executable };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  global.TenkaProjectClient = api;
})(typeof globalThis !== "undefined" ? globalThis : this);
