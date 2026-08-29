// The widget's static frame: toolbar (search, view buttons, Actions, and the
// saved-report select), notice slots, chip strip, table, chart container, and
// pager. Builds w.els — the fixed set of mount points every renderer targets —
// and wires toolbar events to the widget and its feature modules.

import { el, icon } from "../core/dom.js";
import { featureEnabled } from "./schema.js";
import { doSearch, openSearchScopeMenu } from "./search.js";
import { actionsMenuItems, openActionsMenu } from "./menus.js";
import { loadSavedById, refreshSavedSelect, resetToPrimary } from "./saved.js";

export function buildSkeleton(w) {
    const scopeBtn = el("button", {
        type: "button", class: "ir-btn ir-search-scope",
        title: w.t("toolbar.chooseSearchColumn"), "aria-label": w.t("toolbar.chooseSearchColumn"),
        "aria-haspopup": "menu", "aria-expanded": "false",
        onclick: () => openSearchScopeMenu(w, scopeBtn),
    }, icon("search"), icon("caret"));
    const search = el("input", {
        class: "ir-search-input", type: "search", placeholder: w.t("toolbar.search"),
        name: "search", "aria-label": w.t("toolbar.search"),
    });
    const go = el("button", { type: "submit", class: "ir-btn ir-go" }, w.t("toolbar.go"));

    const viewBtn = (mode, iconName, label) => el("button", {
        type: "button", class: "ir-btn ir-viewbtn", dataset: { mode },
        title: label, "aria-label": label,
        "aria-pressed": "false",
        onclick: () => w.switchView(mode),
    }, icon(iconName));
    const views = el("div", { class: "ir-viewbtns", role: "group", "aria-label": w.t("toolbar.view") },
        viewBtn("grid", "grid", w.t("toolbar.grid")),
        viewBtn("groupBy", "group", w.t("toolbar.groupBy")),
        viewBtn("pivot", "pivot", w.t("toolbar.pivot")),
        viewBtn("chart", "chart", w.t("toolbar.chart")));

    const actionsBtn = el("button", {
        type: "button", class: "ir-btn ir-actionsbtn",
        "aria-haspopup": "menu", "aria-expanded": "false",
        onclick: () => openActionsMenu(w, actionsBtn),
    }, w.t("toolbar.actions"), icon("caret"));

    const savedSel = el("select", {
        class: "ir-select ir-saved-select",
        onchange: () => savedSel.value ? loadSavedById(w, savedSel.value) : resetToPrimary(w),
    });
    const savedWrap = el("label", { class: "ir-saved", hidden: true },
        el("span", { class: "ir-saved-label" }, w.t("toolbar.savedReport")), savedSel);
    const searchWrap = el("form", {
        class: "ir-search", role: "search",
        onsubmit: event => { event.preventDefault(); doSearch(w); },
    }, scopeBtn, search, go);
    w.els = {
        search, searchWrap, views, actionsBtn, savedSel, savedWrap,
        errorSlot: el("div", { role: "alert", "aria-atomic": "true" }),
        transientSlot: el("div", { role: "status", "aria-live": "polite", "aria-atomic": "true" }),
        ignoredSlot: el("div", { role: "status", "aria-live": "polite", "aria-atomic": "true" }),
        chips: el("div", { class: "ir-chips", part: "chips", hidden: true }),
        table: el("table", { class: "ir-table", part: "table" }),
        chartWrap: el("div", { class: "ir-chartwrap", part: "chart-container", hidden: true }),
        pager: el("div", { class: "ir-pager", part: "pager" }),
    };
    w.els.tablewrap = el("div", { class: "ir-tablewrap", part: "table-container" }, w.els.table);

    w._mount.replaceChildren(
        el("div", { class: "ir-toolbar", part: "toolbar" },
            searchWrap, views, actionsBtn,
            el("span", { class: "ir-spacer" }),
            savedWrap),
        el("div", { class: "ir-busybar" }),
        el("div", { class: "ir-notices", part: "notices" }, w.els.errorSlot, w.els.transientSlot, w.els.ignoredSlot),
        w.els.chips,
        w.els.tablewrap,
        w.els.chartWrap,
        w.els.pager);
}

/// Fit the toolbar to the report's feature whitelist, once the schema has
/// delivered it. The skeleton builds full-width (features are unknown until
/// then); this pares it down: search bar, per-mode view buttons (the whole
/// group goes when grid is the only choice left), the Actions button when no
/// menu entry survives, and the saved-report select.
export function applyFeatureChrome(w) {
    w.els.searchWrap.hidden = !featureEnabled(w, "search");
    let anyAlternateView = false;
    for (const btn of w.els.views.children) {
        if (btn.dataset.mode === "grid") continue;
        btn.hidden = !featureEnabled(w, btn.dataset.mode);
        anyAlternateView ||= !btn.hidden;
    }
    w.els.views.hidden = !anyAlternateView;
    w.els.actionsBtn.hidden = actionsMenuItems(w).length === 0;
    refreshSavedSelect(w);
}
