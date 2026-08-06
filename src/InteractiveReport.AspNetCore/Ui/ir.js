// <interactive-report report="orders"></interactive-report>
//
// The packaged Interactive Report widget: an APEX-style consumer of the report
// protocol. The report state document is the single source of truth; the widget
// builds it, POSTs it, and renders the response. Client-only annotations
// (underscore-prefixed keys, disabled chips) are stripped before anything is
// sent — the server only ever sees the canonical state document.
//
// Attributes:
//   report — report definition name (required)
//   base   — API prefix; defaults to the prefix this script was served from

import { api, download, saveBlob } from "./ir-api.js";
import { el, icon, banner, ensureCss, popupMenu, confirmDialog } from "./ir-ui.js";
import { renderChips, renderGrid, renderPager } from "./ir-render.js";
import {
    columnsDialog, filterDialog, sortDialog, breakDialog, aggregateDialog,
    computeDialog, highlightDialog, groupByDialog, pivotDialog, saveDialog,
} from "./ir-dialogs.js";

// …/api/reports/ui/ir.js → …/api/reports
const BASE_DEFAULT = new URL("..", import.meta.url).pathname.replace(/\/$/, "");

const OPS_BY_TYPE = {
    text: ["contains", "ncontains", "starts", "ends", "eq", "ne", "in", "nin", "blank", "nblank"],
    number: ["eq", "ne", "lt", "le", "gt", "ge", "between", "in", "nin", "blank", "nblank"],
    date: ["eq", "ne", "lt", "le", "gt", "ge", "between", "blank", "nblank"],
    bool: ["eq", "ne", "blank", "nblank"],
    other: ["eq", "ne", "blank", "nblank"],
};
const FNS_BY_TYPE = {
    number: ["sum", "avg", "min", "max", "count", "countDistinct"],
    text: ["min", "max", "count", "countDistinct"],
    date: ["min", "max", "count", "countDistinct"],
    bool: ["count", "countDistinct"],
    other: ["count", "countDistinct"],
};
// Client-side guess at a computed column's type (drives which operators the filter
// dialogs offer); the server's binder is the real authority. "THEN '…'" catches
// CASE expressions that yield text.
const TEXTY_EXPR = /UPPER|LOWER|TRIM|CONCAT|SUBSTR|\|\||THEN\s*'/i;

class InteractiveReportElement extends HTMLElement {
    static observedAttributes = ["report", "base"];

    constructor() {
        super();
        this._seq = 0;
        this._initialized = false;
    }

    get base() { return this.getAttribute("base") ?? BASE_DEFAULT; }
    get reportName() { return this.getAttribute("report"); }

    connectedCallback() { this.scheduleInit(); }
    attributeChangedCallback(_name, oldValue, newValue) {
        if (this._initialized && oldValue !== newValue) this.scheduleInit();
    }

    scheduleInit() {
        if (this._initQueued) return;
        this._initQueued = true;
        queueMicrotask(() => { this._initQueued = false; if (this.isConnected) this.init(); });
    }

    // --- lifecycle -----------------------------------------------------------

    async init() {
        const seq = ++this._seq;
        this._initialized = true;
        this._abort?.abort();
        ensureCss(this.base);
        this.classList.add("ir-root");

        this.schema = null;
        this.doc = null;
        this.lastResult = null;
        this.whoami = null;
        this.savedList = [];
        this.currentSaved = null;
        this.searchScopeCol = null;
        this.viewMemory = {};
        this.buildSkeleton();

        const name = this.reportName;
        if (!name) {
            this.showError(new Error("<interactive-report> needs a report=\"…\" attribute."));
            return;
        }

        try {
            const [schema, whoami, saved] = await Promise.all([
                api(`${this.base}/${encodeURIComponent(name)}/schema`),
                api(`${this.base}/whoami`).catch(() => null),
                api(`${this.base}/${encodeURIComponent(name)}/saved`).catch(() => []),
            ]);
            if (seq !== this._seq) return;
            this.schema = schema;
            this.whoami = whoami;
            this.savedList = saved;
            this.doc = this.normalize(schema.defaultState);
            this.els.search.value = this.doc.search ?? "";
            this.refreshSavedSelect();
            await this.runQuery();
        } catch (err) {
            if (err.name !== "AbortError" && seq === this._seq) this.showError(err);
        }
    }

    buildSkeleton() {
        const scopeBtn = el("button", {
            type: "button", class: "ir-btn ir-search-scope",
            title: "Choose search column", "aria-label": "Choose search column",
            onclick: () => this.openSearchScopeMenu(scopeBtn),
        }, icon("search"), icon("caret"));
        const search = el("input", {
            class: "ir-search-input", type: "search", placeholder: "Search",
            onkeydown: e => { if (e.key === "Enter") this.doSearch(); },
        });
        const go = el("button", { type: "button", class: "ir-btn ir-go", onclick: () => this.doSearch() }, "Go");

        const viewBtn = (mode, iconName, label) => el("button", {
            type: "button", class: "ir-btn ir-viewbtn", dataset: { mode },
            title: label, "aria-label": label,
            onclick: () => this.switchView(mode),
        }, icon(iconName));
        const views = el("div", { class: "ir-viewbtns", role: "group", "aria-label": "View" },
            viewBtn("grid", "grid", "Grid"),
            viewBtn("groupBy", "group", "Group By"),
            viewBtn("pivot", "pivot", "Pivot"));

        const actionsBtn = el("button", {
            type: "button", class: "ir-btn ir-actionsbtn",
            onclick: () => this.openActionsMenu(actionsBtn),
        }, "Actions", icon("caret"));

        const savedSel = el("select", {
            class: "ir-select ir-saved-select",
            onchange: () => savedSel.value ? this.loadSavedById(savedSel.value) : this.resetToPrimary(),
        });
        const savedWrap = el("label", { class: "ir-saved", hidden: true },
            el("span", { class: "ir-saved-label" }, "Saved Report"), savedSel);

        this.els = {
            search, views, savedSel, savedWrap,
            errorSlot: el("div", {}),
            transientSlot: el("div", {}),
            ignoredSlot: el("div", {}),
            chips: el("div", { class: "ir-chips", hidden: true }),
            table: el("table", { class: "ir-table" }),
            pager: el("div", { class: "ir-pager" }),
        };

        this.replaceChildren(
            el("div", { class: "ir-toolbar" },
                el("div", { class: "ir-search" }, scopeBtn, search, go),
                views, actionsBtn,
                el("span", { class: "ir-spacer" }),
                savedWrap),
            el("div", { class: "ir-busybar" }),
            el("div", { class: "ir-notices" }, this.els.errorSlot, this.els.transientSlot, this.els.ignoredSlot),
            this.els.chips,
            el("div", { class: "ir-tablewrap" }, this.els.table),
            this.els.pager);
    }

    // --- schema lookups ------------------------------------------------------

    pickable() {
        const computed = (this.doc?.computed ?? []).map(c => ({
            name: c.id,
            label: c.label ?? c.id,
            type: TEXTY_EXPR.test(c.expr ?? "") ? "text" : "number",
            computed: true,
        }));
        return [...(this.schema?.columns ?? []), ...computed];
    }

    typeOf(name) { return this.pickable().find(c => c.name === name)?.type ?? "other"; }
    labelOf(name) { return this.pickable().find(c => c.name === name)?.label ?? name; }
    opsFor(type) { return OPS_BY_TYPE[type] ?? OPS_BY_TYPE.other; }
    fnsFor(type) { return FNS_BY_TYPE[type] ?? FNS_BY_TYPE.other; }

    visibleColumnNames() {
        if (this.doc?.columns?.length) return [...this.doc.columns];
        return this.pickable().map(c => c.name);
    }

    isBreakOff(name) { return (this.doc?._offBreaks ?? []).includes(name); }

    // --- state doc plumbing --------------------------------------------------

    normalize(raw) {
        const d = raw ? structuredClone(raw) : {};
        d.filters ??= [];
        d.sorts ??= [];
        d.page = { index: 1, size: d.page?.size ?? this.schema?.limits?.defaultPageSize ?? 50 };
        return d;
    }

    /// The canonical state document: client-only keys (_*) and disabled items removed.
    serialize() {
        const walk = v => {
            if (Array.isArray(v)) return v.filter(x => !(x && typeof x === "object" && x._off)).map(walk);
            if (v && typeof v === "object") {
                const out = {};
                for (const [k, value] of Object.entries(v)) {
                    if (k.startsWith("_") || value === undefined) continue;
                    out[k] = walk(value);
                }
                return out;
            }
            return v;
        };
        const doc = walk(this.doc);
        if (doc.breaks) doc.breaks = doc.breaks.filter(b => !this.isBreakOff(b));
        for (const k of ["filters", "sorts", "breaks", "aggregates", "computed", "highlights", "columns"])
            if (Array.isArray(doc[k]) && doc[k].length === 0) delete doc[k];
        if (doc.search === "") delete doc.search;
        doc.v = 1;
        return doc;
    }

    async runQuery(opts = {}) {
        this._abort?.abort();
        const ctrl = this._abort = new AbortController();
        this.classList.add("ir-busy");
        try {
            const result = await api(`${this.base}/${encodeURIComponent(this.reportName)}/query`, {
                method: "POST", body: this.serialize(), signal: ctrl.signal,
            });
            if (ctrl !== this._abort) return;
            this.lastResult = result;
            this.clearError();
            if (this.doc.view?.mode && this.doc.view.mode !== "grid")
                this.viewMemory[this.doc.view.mode] = this.doc.view;
            renderChips(this, this.els.chips);
            renderGrid(this, this.els.table);
            renderPager(this, this.els.pager);
            this.renderIgnored(result.ignored);
            this.refreshViewButtons();
            return result;
        } catch (err) {
            if (err.name === "AbortError") return;
            renderChips(this, this.els.chips);
            if (!opts.quiet) this.showError(err);
            throw err;
        } finally {
            if (ctrl === this._abort) this.classList.remove("ir-busy");
        }
    }

    /// Optimistic apply: mutate the doc, re-query, roll back on failure. Throws so
    /// dialogs can show the (precise) validation problem and stay open.
    async apply(mutate, { resetPage = true } = {}) {
        const prev = structuredClone(this.doc);
        mutate(this.doc);
        if (resetPage && this.doc.page) this.doc.page.index = 1;
        try {
            await this.runQuery({ quiet: true });
        } catch (err) {
            this.doc = prev;
            renderChips(this, this.els.chips);
            throw err;
        }
    }

    applyOrBanner(mutate, opts) {
        return this.apply(mutate, opts).catch(err => this.showError(err));
    }

    // --- notices -------------------------------------------------------------

    showError(err) {
        const text = err?.status === 401 ? "Sign in to use this report."
            : err?.status === 404 ? "Report not found — or you don't have access."
            : (err?.message || String(err));
        const suffix = err?.traceId ? ` (ref ${err.traceId})` : "";
        this.els.errorSlot.replaceChildren(banner("error", text + suffix, () => this.clearError()));
    }

    clearError() { this.els.errorSlot.replaceChildren(); }

    notify(text, kind = "ok") {
        const node = banner(kind, text);
        this.els.transientSlot.append(node);
        setTimeout(() => node.remove(), 4000);
    }

    renderIgnored(ignored) {
        if (!ignored?.length) { this.els.ignoredSlot.replaceChildren(); return; }
        const text = "Some settings were ignored: " + ignored.map(i => `${i.kind} (${i.detail})`).join("; ");
        this.els.ignoredSlot.replaceChildren(banner("warn", text, () => this.els.ignoredSlot.replaceChildren()));
    }

    // --- toolbar: search -----------------------------------------------------

    doSearch() {
        const raw = this.els.search.value.trim();
        if (!this.searchScopeCol) {
            this.applyOrBanner(d => { d.search = raw || undefined; });
            return;
        }
        if (!raw) return;
        const col = this.searchScopeCol;
        const type = this.typeOf(col);
        let value = raw;
        if (type === "number") {
            const n = Number(raw);
            if (Number.isNaN(n)) { this.showError(new Error(`'${raw}' is not a number`)); return; }
            value = n;
        }
        const op = type === "text" ? "contains" : "eq";
        this.els.search.value = "";
        this.applyOrBanner(d => { (d.filters ??= []).push({ col, op, value }); });
    }

    openSearchScopeMenu(anchor) {
        const textCols = this.pickable().filter(c => c.type === "text");
        popupMenu(anchor, [
            { label: "All Text Columns", checked: !this.searchScopeCol, onPick: () => this.setSearchScope(null) },
            "-",
            ...textCols.map(c => ({ label: c.label, checked: this.searchScopeCol === c.name, onPick: () => this.setSearchScope(c.name) })),
        ]);
    }

    setSearchScope(col) {
        this.searchScopeCol = col;
        this.els.search.placeholder = col ? `Search: ${this.labelOf(col)}` : "Search";
        this.els.search.focus();
    }

    // --- toolbar: views ------------------------------------------------------

    refreshViewButtons() {
        const mode = this.doc?.view?.mode ?? "grid";
        for (const btn of this.els.views.children)
            btn.classList.toggle("ir-active", btn.dataset.mode === mode);
    }

    switchView(mode) {
        const current = this.doc.view?.mode ?? "grid";
        if (mode === current) return;
        if (mode === "grid") { this.applyOrBanner(d => { d.view = undefined; }); return; }
        const memory = this.viewMemory[mode];
        if (memory) this.applyOrBanner(d => { d.view = memory; });
        else mode === "groupBy" ? groupByDialog(this) : pivotDialog(this);
    }

    // --- toolbar: actions menu ----------------------------------------------

    openActionsMenu(anchor) {
        const canSave = this.canManageCurrentSaved();
        popupMenu(anchor, [
            { label: "Columns…", onPick: () => columnsDialog(this) },
            { label: "Filter…", onPick: () => filterDialog(this, {}) },
            { label: "Sort…", onPick: () => sortDialog(this) },
            "-",
            { label: "Control Break…", onPick: () => breakDialog(this) },
            { label: "Highlight…", onPick: () => highlightDialog(this) },
            { label: "Aggregate…", onPick: () => aggregateDialog(this) },
            { label: "Compute…", onPick: () => computeDialog(this) },
            "-",
            { label: "Group By…", onPick: () => groupByDialog(this) },
            { label: "Pivot…", onPick: () => pivotDialog(this) },
            { heading: "Report" },
            ...(canSave ? [{ label: "Save", onPick: () => saveDialog(this, { asNew: false }) }] : []),
            { label: "Save As…", onPick: () => saveDialog(this, { asNew: true }) },
            ...(canSave ? [{ label: "Delete…", onPick: () => this.deleteCurrentSaved() }] : []),
            { label: "Reset", onPick: () => this.resetWorkingCopy() },
            { heading: "Download" },
            { label: "CSV", onPick: () => this.exportCsv() },
        ]);
    }

    // --- header menu ---------------------------------------------------------

    openHeaderMenu(col, anchor) {
        const mode = this.doc.view?.mode ?? "grid";
        const sortItems = [
            { label: "Sort Ascending", onPick: () => this.applyOrBanner(d => { d.sorts = [{ col, dir: "asc" }]; }) },
            { label: "Sort Descending", onPick: () => this.applyOrBanner(d => { d.sorts = [{ col, dir: "desc" }]; }) },
        ];
        if (mode !== "grid") { popupMenu(anchor, sortItems); return; }

        const visible = this.visibleColumnNames();
        const breaking = (this.doc.breaks ?? []).includes(col);
        popupMenu(anchor, [
            ...sortItems,
            "-",
            {
                label: "Hide Column",
                disabled: visible.length <= 1,
                onPick: () => this.applyOrBanner(d => { d.columns = visible.filter(n => n !== col); }),
            },
            {
                label: breaking ? "Remove Control Break" : "Control Break",
                checked: breaking,
                onPick: () => this.applyOrBanner(d => {
                    d.breaks = breaking ? (d.breaks ?? []).filter(b => b !== col) : [...(d.breaks ?? []), col];
                    d._offBreaks = (d._offBreaks ?? []).filter(b => d.breaks.includes(b));
                }),
            },
            "-",
            { label: "Filter…", onPick: () => filterDialog(this, { col }) },
        ]);
    }

    // --- chips ---------------------------------------------------------------

    chipArray(d, kind) {
        return { filter: d.filters, aggregate: d.aggregates, computed: d.computed, highlight: d.highlights }[kind];
    }

    chipToggle(kind, index, on) {
        this.applyOrBanner(d => {
            if (kind === "break") {
                const name = (d.breaks ?? [])[index];
                d._offBreaks = (d._offBreaks ?? []).filter(b => b !== name);
                if (!on) (d._offBreaks ??= []).push(name);
            } else {
                const item = this.chipArray(d, kind)?.[index];
                if (item) on ? delete item._off : item._off = true;
            }
        });
    }

    chipRemove(kind, index) {
        this.applyOrBanner(d => {
            switch (kind) {
                case "search": d.search = undefined; this.els.search.value = ""; break;
                case "break": {
                    const name = (d.breaks ?? [])[index];
                    d.breaks = (d.breaks ?? []).filter((_, i) => i !== index);
                    d._offBreaks = (d._offBreaks ?? []).filter(b => b !== name);
                    break;
                }
                case "view": d.view = undefined; break;
                default: this.chipArray(d, kind)?.splice(index, 1);
            }
        });
    }

    chipEdit(kind, index) {
        switch (kind) {
            case "search": this.els.search.focus(); this.els.search.select(); break;
            case "filter": filterDialog(this, { editIndex: index }); break;
            case "break": breakDialog(this); break;
            case "aggregate": aggregateDialog(this); break;
            case "computed": computeDialog(this, index); break;
            case "highlight": highlightDialog(this, index); break;
            case "view": this.doc.view?.mode === "pivot" ? pivotDialog(this) : groupByDialog(this); break;
        }
    }

    // --- paging --------------------------------------------------------------

    gotoPage(index) { this.applyOrBanner(d => { d.page.index = index; }, { resetPage: false }); }
    setPageSize(size) { this.applyOrBanner(d => { d.page.size = size; }); }

    // --- saved reports -------------------------------------------------------

    canManageCurrentSaved() {
        const s = this.currentSaved;
        if (!s) return false;
        return this.whoami?.isAdministrator || (s.mine && !s.isGlobal);
    }

    refreshSavedSelect() {
        const { savedSel, savedWrap } = this.els;
        savedSel.replaceChildren(new Option("Primary Report", ""));
        const group = (label, items) => {
            if (!items.length) return;
            const g = el("optgroup", { label });
            for (const s of items) g.append(new Option(s.title + (s.mine || s.isGlobal ? "" : ` (${s.owner})`), s.id));
            savedSel.append(g);
        };
        group("Global", this.savedList.filter(s => s.isGlobal));
        group("Private", this.savedList.filter(s => !s.isGlobal));
        savedSel.value = this.currentSaved?.id ?? "";
        savedWrap.hidden = this.savedList.length === 0;
    }

    async loadSavedList() {
        this.savedList = await api(`${this.base}/${encodeURIComponent(this.reportName)}/saved`).catch(() => []);
    }

    async loadSavedById(id) {
        try {
            const docResponse = await api(`${this.base}/saved/${encodeURIComponent(id)}`);
            this.currentSaved = docResponse.summary;
            this.doc = this.normalize(docResponse.state);
            this.els.search.value = this.doc.search ?? "";
            this.refreshSavedSelect();
            await this.runQuery();
        } catch (err) {
            if (err.name !== "AbortError") this.showError(err);
        }
    }

    resetToPrimary() {
        this.currentSaved = null;
        this.doc = this.normalize(this.schema?.defaultState);
        this.els.search.value = this.doc.search ?? "";
        this.refreshSavedSelect();
        this.runQuery().catch(() => {});
    }

    async resetWorkingCopy() {
        const target = this.currentSaved ? `"${this.currentSaved.title}"` : "its default settings";
        if (!await confirmDialog("Reset", `Restore this report to ${target}? Unsaved changes are lost.`, "Reset")) return;
        if (this.currentSaved) await this.loadSavedById(this.currentSaved.id);
        else this.resetToPrimary();
    }

    async saveReport({ title, isGlobal, asNew }) {
        const state = this.serialize();
        if (asNew) {
            this.currentSaved = await api(`${this.base}/${encodeURIComponent(this.reportName)}/saved`, {
                method: "POST", body: { title, state, isGlobal },
            });
        } else {
            const body = { title, state };
            if (this.whoami?.isAdministrator) body.isGlobal = isGlobal;
            this.currentSaved = await api(`${this.base}/saved/${encodeURIComponent(this.currentSaved.id)}`, {
                method: "PUT", body,
            });
        }
        await this.loadSavedList();
        this.refreshSavedSelect();
        this.notify("Report saved.");
    }

    async deleteCurrentSaved() {
        const s = this.currentSaved;
        if (!s) return;
        if (!await confirmDialog("Delete Saved Report", `Delete "${s.title}"? This cannot be undone.`)) return;
        try {
            await api(`${this.base}/saved/${encodeURIComponent(s.id)}`, { method: "DELETE" });
            this.currentSaved = null;
            await this.loadSavedList();
            this.resetToPrimary();
            this.notify("Saved report deleted.");
        } catch (err) {
            this.showError(err);
        }
    }

    // --- export --------------------------------------------------------------

    async exportCsv() {
        try {
            const { blob, filename, truncated } = await download(
                `${this.base}/${encodeURIComponent(this.reportName)}/export?format=csv`, this.serialize());
            saveBlob(blob, filename ?? `${this.reportName}.csv`);
            if (truncated) this.notify("Export truncated at the report's row cap.", "warn");
        } catch (err) {
            this.showError(err);
        }
    }
}

if (!customElements.get("interactive-report"))
    customElements.define("interactive-report", InteractiveReportElement);
