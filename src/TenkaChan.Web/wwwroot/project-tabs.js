"use strict";
(function (global) {
  const tabs = ["prepare", "code", "run", "results"];
  function install(doc) {
    const buttons = Array.from(doc.querySelectorAll("[data-workspace-tab]"));
    if (!buttons.length || doc.body.dataset.workspaceTabs === "ready") return;
    let initialized = false;
    const requested = (doc.defaultView?.location.hash || "").slice(1);
    let active = tabs.includes(requested) ? requested : "prepare";
    if (tabs.includes(requested)) initialized = true;
    function show(key, focus = false) {
      if (!tabs.includes(key)) return false;
      active = key;
      for (const button of buttons) {
        const selected = button.dataset.workspaceTab === key;
        button.setAttribute("aria-selected", String(selected));
        button.tabIndex = selected ? 0 : -1;
        if (selected && focus) button.focus();
      }
      for (const panel of doc.querySelectorAll("[data-workspace-panel]"))
        panel.hidden = panel.dataset.workspacePanel !== key;
      return true;
    }
    for (const button of buttons) {
      button.addEventListener("click", () => { initialized = true; show(button.dataset.workspaceTab); });
      button.addEventListener("keydown", event => {
        const index = tabs.indexOf(button.dataset.workspaceTab);
        const next = event.key === "ArrowRight" ? (index + 1) % tabs.length
          : event.key === "ArrowLeft" ? (index + tabs.length - 1) % tabs.length
          : event.key === "Home" ? 0 : event.key === "End" ? tabs.length - 1 : null;
        if (next === null) return;
        event.preventDefault(); initialized = true; show(tabs[next], true);
      });
    }
    for (const button of doc.querySelectorAll("[data-open-workspace-tab]"))
      button.addEventListener("click", () => { initialized = true; show(button.dataset.openWorkspaceTab, true); });
    const api = { show, get active() { return active; }, observeLoaded(loaded) {
      if (!initialized) { show(loaded ? "code" : "prepare"); initialized = true; }
      // A running server losing its workspace does not silently hide a failure result.
    }};
    doc.body.dataset.workspaceTabs = "ready";
    show(active); global.TenkaProjectTabs = api; return api;
  }
  if (typeof module !== "undefined" && module.exports) module.exports = { tabs, install };
  if (typeof document !== "undefined") {
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", () => install(document));
    else install(document);
  }
})(typeof globalThis !== "undefined" ? globalThis : this);
