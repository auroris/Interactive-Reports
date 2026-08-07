// The widget's static frame: toolbar (search, view buttons, Actions, and the
// saved-report select), notice slots, chip strip, table, chart container, and
// pager. Builds w.els — the fixed set of mount points every renderer targets —
// and wires toolbar events to the widget and its feature modules.

import { el, icon } from "../core/dom.js";
import { doSearch, openSearchScopeMenu } from "./search.js";
import { openActionsMenu } from "./menus.js";
import { loadSavedById, resetToPrimary } from "./saved.js";

export function buildSkeleton(w) {
    const scopeBtn = el("button", {
        type: "button", class: "ir-btn ir-search-scope",
        title: "Choose search column", "aria-label": "Choose search column",
        onclick: () => openSearchScopeMenu(w, scopeBtn),
    }, icon("search"), icon("caret"));
    const search = el("input", {
        class: "ir-search-input", type: "search", placeholder: "Search",
        onkeydown: e => { if (e.key === "Enter") doSearch(w); },
    });
    const go = el("button", { type: "button", class: "ir-btn ir-go", onclick: () => doSearch(w) }, "Go");

    const viewBtn = (mode, iconName, label) => el("button", {
        type: "button", class: "ir-btn ir-viewbtn", dataset: { mode },
        title: label, "aria-label": label,
        onclick: () => w.switchView(mode),
    }, icon(iconName));
    const views = el("div", { class: "ir-viewbtns", role: "group", "aria-label": "View" },
        viewBtn("grid", "grid", "Grid"),
        viewBtn("groupBy", "group", "Group By"),
        viewBtn("pivot", "pivot", "Pivot"),
        viewBtn("chart", "chart", "Chart"));

    const actionsBtn = el("button", {
        type: "button", class: "ir-btn ir-actionsbtn",
        onclick: () => openActionsMenu(w, actionsBtn),
    }, "Actions", icon("caret"));

    const savedSel = el("select", {
        class: "ir-select ir-saved-select",
        onchange: () => savedSel.value ? loadSavedById(w, savedSel.value) : resetToPrimary(w),
    });
    const savedWrap = el("label", { class: "ir-saved", hidden: true },
        el("span", { class: "ir-saved-label" }, "Saved Report"), savedSel);
    w.els = {
        search, views, savedSel, savedWrap,
        errorSlot: el("div", {}),
        transientSlot: el("div", {}),
        ignoredSlot: el("div", {}),
        chips: el("div", { class: "ir-chips", part: "chips", hidden: true }),
        table: el("table", { class: "ir-table", part: "table" }),
        chartWrap: el("div", { class: "ir-chartwrap", part: "chart-container", hidden: true }),
        pager: el("div", { class: "ir-pager", part: "pager" }),
    };
    w.els.tablewrap = el("div", { class: "ir-tablewrap", part: "table-container" }, w.els.table);

    w._mount.replaceChildren(
        el("div", { class: "ir-toolbar", part: "toolbar" },
            el("div", { class: "ir-search" }, scopeBtn, search, go),
            views, actionsBtn,
            el("span", { class: "ir-spacer" }),
            savedWrap),
        el("div", { class: "ir-busybar" }),
        el("div", { class: "ir-notices", part: "notices" }, w.els.errorSlot, w.els.transientSlot, w.els.ignoredSlot),
        w.els.chips,
        w.els.tablewrap,
        w.els.chartWrap,
        w.els.pager);
}
