"use strict";
class TenkaPathPicker {
  constructor(fetcher) { this.fetcher = fetcher; this.busy = false; }
  async json(path, init) {
    const response = await this.fetcher(path, { credentials: "same-origin", ...init });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || `HTTP ${response.status}`);
    return data;
  }
  async pick(kind, initialPath) {
    if (this.busy) throw new Error("選択画面が開いています。タスクバーを確認してください。");
    this.busy = true;
    try {
      const session = await this.json("/api/session");
      if (!session.csrfToken) throw new Error("接続情報を取得できません。ページを更新してください。");
      const result = await this.json("/api/dialogs/pick", {
        method: "POST", headers: { "Content-Type": "application/json", "X-Tenka-CSRF": session.csrfToken },
        body: JSON.stringify({ kind, initialPath })
      });
      if (result.cancelled === true) return null;
      if (result.cancelled !== false || typeof result.path !== "string" || !result.path)
        throw new Error("選択結果を読み取れませんでした。パスは変更していません。");
      return result.path;
    } finally { this.busy = false; }
  }
}
function attachTenkaPathPickers() {
  const picker = new TenkaPathPicker((...args) => fetch(...args));
  const elements = [...document.querySelectorAll("input[data-path-kind]")];
  for (const input of elements) {
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = "参照…";
    button.className = "button button-outline tenka-path-pick";
    button.setAttribute("aria-label", "Windowsの標準ダイアログで選択");
    const message = document.createElement("span");
    message.className = "tenka-path-message";
    message.setAttribute("role", "status");
    input.after(button, message);
    button.addEventListener("click", async () => {
      if (input.disabled) return;
      const previous = input.value;
      button.disabled = true;
      message.textContent = "Windowsの選択画面を開いています…（背面にある場合はタスクバーを確認）";
      try {
        const path = await picker.pick(input.dataset.pathKind, previous);
        if (path === null) { message.textContent = "キャンセルしました。"; return; }
        if (input.disabled || input.value !== previous) {
          message.textContent = "入力が途中で変更されたため、選択結果では上書きしませんでした。"; return;
        }
        input.value = path;
        input.dispatchEvent(new Event("input", { bubbles: true }));
        input.dispatchEvent(new Event("change", { bubbles: true }));
        message.textContent = "選択しました。読み込み・実行はボタンを押して開始してください。";
      } catch (error) { message.textContent = error.message; }
      finally { button.disabled = false; }
    });
  }
  // The setup launcher may prefill a project, but never auto-evaluates MSBuild.
  const project = new URL(location.href).searchParams.get("project");
  const field = document.querySelector('input[data-path-kind="project"]');
  if (project && project.length <= 4096 && field && !field.value) field.value = project;
}
if (typeof module !== "undefined" && module.exports) module.exports = { TenkaPathPicker, attachTenkaPathPickers };
if (typeof document !== "undefined") {
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", attachTenkaPathPickers);
  else attachTenkaPathPickers();
}
