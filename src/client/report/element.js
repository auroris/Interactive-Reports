// The <interactive-report> element: the state-document lifecycle. The report
// state doc is the single source of truth; the element builds it, POSTs it, and
// routes the response to the renderers. Everything else — skeleton, menus,
// search, dialogs, saved reports — lives in feature modules that operate on the
// element through this class's surface: doc, els, apply/applyOrBanner, runQuery,
// normalize/serialize, reportUrl, and the notice slots.

import { api, apiUrl, defaultApiBase } from "../core/api.js";
import { banner, transientBanner } from "../core/dom.js";
import { createWidgetRoot, disposeWidget, setCustomStyleSheet } from "../core/widget.js";
import { applyFeatureChrome, buildSkeleton } from "./skeleton.js";
import { featureEnabled } from "./schema.js";
import {
    activateTail,
    configuredTail,
    modeOf,
    normalizeReportState,
    schemaMismatch,
    serializeReportState,
} from "./state.js";
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
    static observedAttributes = ["report", "saved-report", "api-base", "base"];

    constructor() {
        super();
        const { root, mount } = createWidgetRoot(this);
        this._root = root;
        this._mount = mount;
        this._busyTokens = new Set();
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
    get requestedSavedReportName() { return this.getAttribute("saved-report"); }
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

    beginBusy() {
        const token = Symbol("report operation");
        this._busyTokens.add(token);
        this._mount.setAttribute("aria-busy", "true");
        return () => {
            this._busyTokens.delete(token);
            if (!this._busyTokens.size) this._mount.setAttribute("aria-busy", "false");
        };
    }

    resetReportContext() {
        setCustomStyleSheet(this, null);
        this.schema = null;
        this.doc = null;
        this.lastResult = null;
        this.savedList = [];
        this.currentSaved = null;
        this.searchScopeCol = null;
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
        this._activeReportName = null;
        this.whoami = null;
        buildSkeleton(this);

        const requested = this.requestedReportName?.trim();
        if (!requested) {
            this.showError(new Error("The interactive-report element requires a non-empty report attribute."));
            return;
        }

        try {
            const whoami = await api(`${this.base}/whoami`).catch(() => null);
            if (seq !== this._seq) return;
            this.whoami = whoami;
            await this.activateReport(requested, seq);
        } catch (err) {
            if (err.name !== "AbortError" && seq === this._seq) this.showError(err);
        }
    }

    async activateReport(name, seq = ++this._seq, { quiet = false } = {}) {
        name = name?.trim();
        if (!name || seq !== this._seq) return false;

        this._abort?.abort();
        this._abort = null;
        this.resetReportContext();
        this._activeReportName = name;
        this.clearReportView();
        refreshSavedSelect(this);
        const finishBusy = this.beginBusy();

        try {
            // Schema is the loadability gate. Do not issue saved-state or query
            // requests for this report until its definition is accessible and valid.
            const schema = await api(apiUrl(this.base, name, "schema"));
            if (seq !== this._seq) return false;
            this.schema = schema;
            setCustomStyleSheet(this, schema.styleSheet);
            applyFeatureChrome(this);
            const saved = featureEnabled(this, "savedReports")
                ? await api(apiUrl(this.base, name, "saved")).catch(() => [])
                : [];
            if (seq !== this._seq) return;
            this.savedList = saved;

            const requestedSaved = this.requestedSavedReportName?.trim();
            const savedMatches = requestedSaved
                ? saved.filter(candidate => sameName(candidate.title, requestedSaved))
                : [];
            let savedWarning;
            if (savedMatches.length === 1) {
                const docResponse = await api(apiUrl(this.base, "saved", savedMatches[0].id));
                if (seq !== this._seq) return false;
                this.currentSaved = docResponse.summary;
                this.adoptState(docResponse.state, `Saved report "${docResponse.summary?.title}"`);
            } else {
                this.adoptState(schema.defaultState, "The default report");
                if (requestedSaved) {
                    savedWarning = savedMatches.length === 0
                        ? `Saved report "${requestedSaved}" is not available; loaded Primary Report.`
                        : `Saved report name "${requestedSaved}" is ambiguous; loaded Primary Report.`;
                }
            }
            this.els.search.value = this.doc.search ?? "";
            refreshSavedSelect(this);
            await this.runQuery({ quiet });
            if (savedWarning) this.notify(savedWarning, "warn");
            return seq === this._seq && this.lastResult !== null;
        } catch (err) {
            if (!quiet && err.name !== "AbortError" && seq === this._seq) this.showError(err);
            return false;
        } finally {
            finishBusy();
        }
    }

    // --- state doc plumbing --------------------------------------------------

    normalize(raw) {
        return normalizeReportState(
            raw,
            this.schema?.limits?.defaultPageSize ?? 50,
            this.schema?.defaultState);
    }

    /// The T0 consistency gate: a document whose recorded schema snapshot no
    /// longer matches the live schema is refused — not run — and the working
    /// copy resets down the drift-proof chain: default report, then the
    /// synthetic empty state (which depends on nothing). Stored rows are never
    /// touched; only the working copy resets. Absent snapshots skip the check.
    adoptState(rawState, description) {
        const live = this.schema?.columns ?? [];
        let mismatch = schemaMismatch(rawState?.schema, live);
        if (!mismatch) {
            this.doc = this.normalize(rawState);
            return true;
        }

        const detail = mismatch.join("; ");
        let fallback = "the default report";
        const defaultState = this.schema?.defaultState;
        if (schemaMismatch(defaultState?.schema, live)) {
            // A configured default can itself predate the schema change; the
            // synthetic empty terminus cannot.
            this.doc = normalizeReportState(null, this.schema?.limits?.defaultPageSize ?? 50);
            fallback = "an empty report";
        } else {
            this.doc = this.normalize(defaultState);
        }
        this.showError(new Error(
            `${description ?? "This report"} was built against a schema that has changed (${detail}). Loaded ${fallback} instead.`));
        return false;
    }

    /// Canonical state: explicit empty values survive so they can clear report defaults;
    /// undefined values and underscore-prefixed working data do not cross the protocol.
    serialize() {
        return serializeReportState(this.doc, this.schema?.stateVersion ?? 3);
    }

    reportUrl(resource) {
        return apiUrl(this.base, this.reportName, resource);
    }

    async runQuery(opts = {}) {
        this._abort?.abort();
        const ctrl = this._abort = new AbortController();
        const finishBusy = this.beginBusy();
        try {
            const result = await api(this.reportUrl("query"), {
                method: "POST", body: this.serialize(), signal: ctrl.signal,
            });
            if (ctrl !== this._abort) return;
            this.lastResult = result;
            this.clearError();
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
            finishBusy();
        }
    }

    /// Route the result to the table or the chart. Only one is ever visible; the
    /// other is emptied so stale content cannot flash back on the next switch.
    renderView() {
        const chartMode = modeOf(this.doc) === "chart";
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
            if (this.lastResult !== result || modeOf(this.doc) !== "chart" || !this.isConnected) return;
            this._chart = renderChartView(this, this.els.chartWrap, module);
        } catch {
            // A failed chunk load may settle after the user has already switched
            // views, reports, or disconnected the element. Do not leak that stale
            // failure into the current view.
            if (this.lastResult !== result || modeOf(this.doc) !== "chart" || !this.isConnected) return;
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
        transientBanner(this.els.transientSlot, kind, text);
    }

    renderIgnored(ignored) {
        if (!ignored?.length) { this.els.ignoredSlot.replaceChildren(); return; }
        const text = "Some settings were ignored: " + ignored.map(i => `${i.kind} (${i.detail})`).join("; ");
        this.els.ignoredSlot.replaceChildren(banner("warn", text, () => this.els.ignoredSlot.replaceChildren()));
    }

    // --- view switching ------------------------------------------------------

    refreshViewButtons() {
        const mode = this.doc ? modeOf(this.doc) : "grid";
        for (const btn of this.els.views.children)
            btn.setAttribute("aria-pressed", String(btn.dataset.mode === mode));
    }

    switchView(mode) {
        const current = modeOf(this.doc);
        if (mode === current) return;
        if (mode !== "grid" && !featureEnabled(this, mode)) return;
        if (mode === "grid") { this.applyOrBanner(d => activateTail(d, "grid")); return; }
        if (configuredTail(this.doc, mode)) this.applyOrBanner(d => activateTail(d, mode));
        else openViewDialog(this, mode);
    }
}
