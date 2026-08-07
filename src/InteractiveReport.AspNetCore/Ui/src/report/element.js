// The <interactive-report> element: the state-document lifecycle. The report
// state doc is the single source of truth; the element builds it, POSTs it, and
// routes the response to the renderers. Everything else — skeleton, menus,
// search, dialogs, saved reports — lives in feature modules that operate on the
// element through this class's surface: doc, els, apply/applyOrBanner, runQuery,
// normalize/serialize, reportUrl, and the notice slots.

import { api, apiUrl, defaultApiBase } from "../core/api.js";
import { banner } from "../core/dom.js";
import { createWidgetRoot, disposeWidget } from "../core/widget.js";
import { buildSkeleton } from "./skeleton.js";
import { normalizeReportState, serializeReportState } from "./state.js";
import { refreshSavedSelect } from "./saved.js";
import { renderChips } from "./render/chips.js";
import { renderGrid } from "./render/grid.js";
import { renderChartView } from "./render/chart-view.js";
import { renderPager } from "./render/pager.js";
import { openViewDialog } from "./dialogs/view.js";

const BASE_DEFAULT = defaultApiBase();
const sameName = (left, right) => typeof left === "string" && typeof right === "string"
    && left.toUpperCase() === right.toUpperCase();

// Chart.js glue ships as its own bundle beside ir.js and loads the first time
// any report on the page enters chart view. The URL is computed at runtime so
// the bundler leaves the import dynamic instead of inlining the chunk here.
let chartModulePromise;
const loadChartModule = () =>
    chartModulePromise ??= import(new URL("./ir-chart.js", import.meta.url).href)
        .catch(err => { chartModulePromise = undefined; throw err; });

export class InteractiveReportElement extends HTMLElement {
    static observedAttributes = ["report", "api-base", "base"];

    constructor() {
        super();
        const { root, mount } = createWidgetRoot(this);
        this._root = root;
        this._mount = mount;
        this._seq = 0;
        this._initialized = false;
    }

    get apiBase() { return this.getAttribute("api-base") ?? this.getAttribute("base") ?? BASE_DEFAULT; }
    set apiBase(value) {
        if (value === null || value === undefined) this.removeAttribute("api-base");
        else this.setAttribute("api-base", String(value));
    }
    get base() { return this.apiBase.replace(/\/+$/, ""); }
    get requestedReportName() { return this.getAttribute("report"); }
    get reportName() { return this._activeReportName ?? this.requestedReportName; }

    connectedCallback() { this.scheduleInit(); }
    disconnectedCallback() {
        ++this._seq;
        this._abort?.abort();
        this._abort = null;
        this.destroyChart();
        disposeWidget(this);
    }
    attributeChangedCallback(_name, oldValue, newValue) {
        if (this._initialized && oldValue !== newValue) this.scheduleInit();
    }

    scheduleInit() {
        if (this._initQueued) return;
        this._initQueued = true;
        queueMicrotask(() => { this._initQueued = false; if (this.isConnected) this.init(); });
    }

    // --- lifecycle -----------------------------------------------------------

    resetReportContext() {
        this.schema = null;
        this.doc = null;
        this.lastResult = null;
        this.savedList = [];
        this.currentSaved = null;
        this.searchScopeCol = null;
        this.viewMemory = {};
    }

    clearReportView() {
        this.els.search.value = "";
        this.destroyChart();
        this.els.chartWrap.replaceChildren();
        this.els.chartWrap.hidden = true;
        this.els.tablewrap.hidden = false;
        this.els.table.replaceChildren();
        this.els.pager.replaceChildren();
        this.els.chips.replaceChildren();
        this.els.chips.hidden = true;
        this.clearError();
    }

    async init() {
        const seq = ++this._seq;
        this._initialized = true;
        this._abort?.abort();
        this._abort = null;
        this.destroyChart();

        this.resetReportContext();
        this.availableReports = [];
        this._activeReportName = null;
        this.whoami = null;
        buildSkeleton(this);

        try {
            const [reports, whoami] = await Promise.all([
                api(this.base),
                api(`${this.base}/whoami`).catch(() => null),
            ]);
            if (seq !== this._seq) return;
            this.availableReports = reports;
            this.whoami = whoami;
            this.refreshReportSelect();

            const requested = this.requestedReportName;
            const preferred = this.availableReports.find(r => sameName(r.name, requested));
            const candidates = preferred
                ? [preferred, ...this.availableReports.filter(r => r !== preferred)]
                : this.availableReports;
            if (!candidates.length) {
                this.showError(new Error("No reports are available for the current user."));
                return;
            }
            for (const candidate of candidates) {
                if (await this.activateReport(candidate.name, seq, { quiet: true })) return;
                if (seq !== this._seq) return;
            }
            this.showError(new Error("None of the reports available to the current user could be loaded."));
        } catch (err) {
            if (err.name !== "AbortError" && seq === this._seq) this.showError(err);
        }
    }

    refreshReportSelect() {
        const { reportSel, reportWrap } = this.els;
        reportSel.replaceChildren(...this.availableReports.map(r => new Option(r.title, r.name)));
        reportSel.value = this._activeReportName ?? "";
        reportWrap.hidden = this.availableReports.length <= 1;
    }

    async activateReport(name, seq = ++this._seq, { quiet = false } = {}) {
        const selected = this.availableReports.find(r => sameName(r.name, name));
        if (!selected || seq !== this._seq) return false;

        this._abort?.abort();
        this._abort = null;
        this.resetReportContext();
        this._activeReportName = selected.name;
        this.clearReportView();
        this.refreshReportSelect();
        refreshSavedSelect(this);
        this._mount.classList.add("ir-busy");

        try {
            // Schema is the loadability gate. Do not issue saved-state or query
            // requests for this report until its definition is accessible and valid.
            const schema = await api(apiUrl(this.base, selected.name, "schema"));
            if (seq !== this._seq) return false;
            const saved = await api(apiUrl(this.base, selected.name, "saved")).catch(() => []);
            if (seq !== this._seq) return;
            this.schema = schema;
            this.savedList = saved;
            this.doc = this.normalize(schema.defaultState);
            this.els.search.value = this.doc.search ?? "";
            refreshSavedSelect(this);
            await this.runQuery({ quiet });
            return seq === this._seq && this.lastResult !== null;
        } catch (err) {
            if (!quiet && err.name !== "AbortError" && seq === this._seq) this.showError(err);
            return false;
        } finally {
            if (seq === this._seq) this._mount.classList.remove("ir-busy");
        }
    }

    // --- state doc plumbing --------------------------------------------------

    normalize(raw) {
        return normalizeReportState(
            raw,
            this.schema?.limits?.defaultPageSize ?? 50,
            this.schema?.defaultState);
    }

    /// Canonical state: explicit empty values survive so they can clear report defaults;
    /// undefined values and underscore-prefixed working data do not cross the protocol.
    serialize() {
        return serializeReportState(this.doc, this.schema?.stateVersion ?? 2);
    }

    reportUrl(resource) {
        return apiUrl(this.base, this.reportName, resource);
    }

    async runQuery(opts = {}) {
        this._abort?.abort();
        const ctrl = this._abort = new AbortController();
        this._mount.classList.add("ir-busy");
        try {
            const result = await api(this.reportUrl("query"), {
                method: "POST", body: this.serialize(), signal: ctrl.signal,
            });
            if (ctrl !== this._abort) return;
            this.lastResult = result;
            this.clearError();
            if (this.doc.view?.mode && this.doc.view.mode !== "grid")
                this.viewMemory[this.doc.view.mode] = this.doc.view;
            renderChips(this, this.els.chips);
            this.renderView();
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
            if (ctrl === this._abort) this._mount.classList.remove("ir-busy");
        }
    }

    /// Route the result to the table or the chart. Only one is ever visible; the
    /// other is emptied so stale content cannot flash back on the next switch.
    renderView() {
        const chartMode = (this.doc.view?.mode ?? "grid") === "chart";
        this.els.tablewrap.hidden = chartMode;
        this.els.chartWrap.hidden = !chartMode;
        if (!chartMode) {
            this.destroyChart();
            this.els.chartWrap.replaceChildren();
            renderGrid(this, this.els.table);
            return;
        }
        this.els.table.replaceChildren();
        this.renderChart();
    }

    async renderChart() {
        const result = this.lastResult;
        this.destroyChart();
        try {
            const module = await loadChartModule();
            // The module load is async: bail if the widget moved on meanwhile.
            if (this.lastResult !== result || (this.doc.view?.mode ?? "grid") !== "chart" || !this.isConnected) return;
            this._chart = renderChartView(this, this.els.chartWrap, module);
        } catch {
            // A failed chunk load may settle after the user has already switched
            // views, reports, or disconnected the element. Do not leak that stale
            // failure into the current view.
            if (this.lastResult !== result || (this.doc.view?.mode ?? "grid") !== "chart" || !this.isConnected) return;
            this.els.chartWrap.replaceChildren();
            this.showError(new Error("The charting module failed to load. Reload the page and try again."));
        }
    }

    destroyChart() {
        this._chart?.destroy();
        this._chart = null;
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

    // --- view switching ------------------------------------------------------

    refreshViewButtons() {
        const mode = this.doc?.view?.mode ?? "grid";
        for (const btn of this.els.views.children)
            btn.classList.toggle("ir-active", btn.dataset.mode === mode);
    }

    switchView(mode) {
        const current = this.doc.view?.mode ?? "grid";
        if (mode === current) return;
        if (mode === "grid") { this.applyOrBanner(d => { d.view = { mode: "grid" }; }); return; }
        const memory = this.viewMemory[mode];
        if (memory) this.applyOrBanner(d => { d.view = memory; });
        else openViewDialog(this, mode);
    }
}
