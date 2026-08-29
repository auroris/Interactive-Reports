// The <interactive-report> element: the state-document lifecycle. The report
// state doc is the single source of truth; the element builds it, POSTs it, and
// routes the response to the renderers. Everything else — skeleton, menus,
// search, dialogs, saved reports — lives in feature modules that operate on the
// element through this class's surface: doc, els, apply/applyOrBanner, runQuery,
// state transitions, restoreLastGood, normalize/serialize, reportUrl, and the
// notice slots.

import { api, apiUrl, errorText } from "../core/api.js";
import { banner } from "../core/dom.js";
import { loadWhoami } from "../core/identity.js";
import { setCustomStyleSheet, WidgetElement } from "../core/widget.js";
import { applyFeatureChrome, buildSkeleton } from "./skeleton.js";
import { featureEnabled } from "./schema.js";
import {
    invalidateChangedSchemas,
    modeOf,
    normalizeReportState,
    resolveView,
    selectView,
    serializeReportState,
} from "./state.js";
import { refreshSavedSelect, sameTitle } from "./saved.js";
import { renderChips } from "./render/chips.js";
import { renderGrid } from "./render/grid.js";
import { canRenderChart, renderChartView } from "./render/chart-view.js";
import { renderPager } from "./render/pager.js";
import { openViewDialog } from "./dialogs/view.js";
import { retrieveExport } from "./export.js";

// Chart.js glue ships as its own bundle beside ir.js and loads the first time
// any report on the page enters chart view. The URL is computed at runtime so
// the bundler leaves the import dynamic instead of inlining the chunk here.
let chartModulePromise;
const loadChartModule = () =>
    chartModulePromise ??= import(new URL("./ir-chart.js", import.meta.url).href)
        .catch(err => { chartModulePromise = undefined; throw err; });

export class InteractiveReportElement extends WidgetElement {
    static observedAttributes = ["report", "saved-report", "api-base", "base", "lang"];

    constructor() {
        super();
        this._busyTokens = new Set();
        this._initialized = false;
        this._stateRevision = 0;
    }
    get requestedReportName() { return this.getAttribute("report"); }
    get requestedSavedReportName() { return this.getAttribute("saved-report"); }
    get reportName() { return this._activeReportName ?? this.requestedReportName; }

    connectedCallback() { this.scheduleInit(); }
    disconnectedCallback() {
        super.disconnectedCallback();
        this._abort?.abort();
        this._abort = null;
        this.destroyChart();
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
        this._stateRevision++;
        setCustomStyleSheet(this, null);
        this.schema = null;
        this.doc = null;
        this.lastResult = null;
        this.savedList = [];
        this.currentSaved = null;
        this.searchScopeCol = null;
        this._lastGood = null;
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
            this.showError(new Error(this.t("report.attributeRequired")));
            return;
        }

        try {
            const identity = await loadWhoami(this.base);
            if (seq !== this._seq) return;
            this.whoami = identity.whoami;
            if (identity.error)
                this.notify(this.t("report.identityUnknown", {
                    message: errorText(identity.error, null, this),
                }), "warn");
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
            // A missing saved endpoint means the feature is off; any other
            // failure must not masquerade as "no saved reports exist".
            let savedError = null;
            const saved = featureEnabled(this, "savedReports")
                ? await api(apiUrl(this.base, name, "saved")).catch(err => {
                    if (err.status !== 404) savedError = err;
                    return [];
                })
                : [];
            if (seq !== this._seq) return;
            this.savedList = saved;

            const requestedSaved = this.requestedSavedReportName?.trim();
            const savedMatches = requestedSaved
                ? saved.filter(candidate => sameTitle(candidate.title, requestedSaved))
                : [];
            let savedWarning = savedError
                ? this.t("saved.loadFailed", { message: savedError.message })
                : undefined;
            if (savedMatches.length === 1) {
                const docResponse = await api(apiUrl(this.base, "saved", savedMatches[0].id));
                if (seq !== this._seq) return false;
                this.currentSaved = docResponse.summary;
                this.adoptState(docResponse.state);
            } else {
                this.adoptState(schema.defaultState);
                this.currentSaved = saved.find(candidate =>
                    candidate.isPrimary && sameTitle(candidate.title, "Default")) ?? null;
                if (requestedSaved && !savedError) {
                    savedWarning = savedMatches.length === 0
                        ? this.t("saved.requestedUnavailable", { title: requestedSaved })
                        : this.t("saved.requestedAmbiguous", { title: requestedSaved });
                }
            }
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

    /// Adopt a state document as the working copy. Server-delivered documents
    /// are authoritative, and saved reports are accepted liberally: normalization
    /// guarantees shape, the server judges the content on query — hard problems
    /// come back as a validation response (and the failed operation rolls back),
    /// soft drift as ignored[] notices.
    adoptState(rawState) {
        this.doc = this.normalize(rawState);
        this.els.search.value = this.doc.search ?? "";
    }

    /// Canonical state: explicit empty values survive so they can clear report defaults;
    /// undefined values and underscore-prefixed working data do not cross the protocol.
    serialize() {
        return serializeReportState(this.doc);
    }

    reportUrl(resource) {
        return apiUrl(this.base, this.reportName, resource);
    }

    /**
     * Public host-integration API. Retrieve the current report in a generated
     * format without presenting it as a browser download. The resolved object
     * contains { blob, filename, contentType, truncated }.
     */
    getExport(format = "csv", options = {}) {
        return retrieveExport(this, format, options);
    }

    async runQuery(opts = {}) {
        this._abort?.abort();
        const ctrl = this._abort = new AbortController();
        const submitted = this.serialize();
        const finishBusy = this.beginBusy();
        try {
            const result = await api(this.reportUrl("query"), {
                method: "POST", body: submitted, signal: ctrl.signal,
            });
            if (ctrl !== this._abort) return;
            const accepted = result.document ?? submitted;
            this.doc = structuredClone(accepted);
            this.lastResult = result;
            // The returned document is the submitted working copy with null schema
            // caches replaced by the server. A superseding operation aborts this
            // request before this point, so it cannot overwrite newer edits.
            this.commitLastGood(accepted);
            this.clearError();
            renderChips(this, this.els.chips);
            this.renderView();
            renderPager(this, this.els.pager);
            this.renderIgnored(result.ignored);
            this.refreshViewButtons();
            return result;
        } catch (err) {
            if (ctrl !== this._abort || err.name === "AbortError") return;
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
        const chartRenderable = chartMode && canRenderChart(this);
        this.els.tablewrap.hidden = chartRenderable;
        this.els.chartWrap.hidden = !chartRenderable;
        if (!chartRenderable) {
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
            this.showError(new Error(this.t("chart.loadFailed")));
        }
    }

    destroyChart() {
        this._chart?.destroy();
        this._chart = null;
    }

    /// Optimistic apply: mutate a CLONE, install it, re-query, restore the last
    /// validated state on failure. Mutating a clone keeps a mutator that throws
    /// mid-way (staged multi-column edits) from leaving half its work in the live
    /// doc. Throws so dialogs can show the (precise) validation problem and stay
    /// open.
    async apply(mutate, { resetPage = true } = {}) {
        const prev = this.doc;
        const next = structuredClone(this.doc);
        mutate(next);
        invalidateChangedSchemas(prev, next);
        if (resetPage && next.page) next.page.index = 1;
        const transition = this.beginStateTransition();
        this.doc = next;
        try {
            await this.runQuery({ quiet: true });
        } catch (err) {
            if (this.isCurrentStateTransition(transition)) this.restoreLastGood(prev);
            throw err;
        }
    }

    applyOrBanner(mutate, opts) {
        return this.apply(mutate, opts).catch(err => this.showError(err));
    }

    /// Begin an operation that may replace the working document. Starting one
    /// invalidates older saved-document loads and aborts any query it supersedes.
    beginStateTransition() {
        const revision = ++this._stateRevision;
        this._abort?.abort();
        this._abort = null;
        return revision;
    }

    isCurrentStateTransition(revision) {
        return revision === this._stateRevision;
    }

    get stateRevision() { return this._stateRevision; }

    /// The last server-validated state — the rollback target for any failed
    /// operation. Committed only on query success, so an operation whose query
    /// was aborted by a newer one never becomes a rollback target: if that newer
    /// operation fails, the restore skips past the aborted, never-validated
    /// intermediate back to validated ground.
    commitLastGood(doc = this.doc) {
        this._lastGood = {
            doc: structuredClone(doc),
            currentSaved: this.currentSaved,
            revision: this._stateRevision,
        };
    }

    /// Record a successful save without treating it as a successful query.
    /// A stable working copy becomes associated with the returned saved report;
    /// a newer state transition keeps its own selection. Existing associations
    /// still receive updated title/scope/row-version metadata.
    recordSaved(summary, revision) {
        const sameWorkingCopy = this.isCurrentStateTransition(revision);
        const updatesCurrent = this.currentSaved?.id === summary.id;
        if (sameWorkingCopy || updatesCurrent) this.currentSaved = summary;

        const updatesLastGood = this._lastGood?.currentSaved?.id === summary.id;
        if (this._lastGood && (updatesLastGood
            || (sameWorkingCopy && this._lastGood.revision === revision)))
            this._lastGood.currentSaved = summary;
    }

    /// Put doc, saved-report selection, search box, and chips back on the last
    /// validated state (or the caller's fallback when nothing was validated yet).
    /// The rendered grid still shows that state's result, so the widget is
    /// consistent again as a whole.
    restoreLastGood(fallbackDoc = null) {
        const good = this._lastGood;
        if (!good && !fallbackDoc) return;
        this.doc = good ? structuredClone(good.doc) : fallbackDoc;
        if (good) {
            this.currentSaved = good.currentSaved;
            // The restored, validated document is now the current generation.
            good.revision = this._stateRevision;
        }
        this.els.search.value = this.doc?.search ?? "";
        renderChips(this, this.els.chips);
        refreshSavedSelect(this);
    }

    /// A deleted saved report must not resurrect through a later rollback.
    forgetSaved(id) {
        if (this._lastGood?.currentSaved?.id === id) this._lastGood.currentSaved = null;
    }

    // --- notices -------------------------------------------------------------

    showError(err) {
        // Friendly text remains as compatibility for older, bodiless servers. A
        // coded server error is more precise than either stock phrase.
        const error = err?.error ?? err?.problem ?? {};
        const hasServerText = error.title || error.description || error.detail;
        const friendly = err?.status === 401 && !hasServerText ? this.t("report.signIn")
            : err?.status === 404 && !hasServerText
                ? this.t("report.notFound")
                : null;
        super.showError(err, friendly);
    }

    renderIgnored(ignored) {
        if (!ignored?.length) { this.els.ignoredSlot.replaceChildren(); return; }
        const text = this.t("report.ignored", {
            details: ignored.map(i => `${i.kind} (${i.detail})`).join("; "),
        });
        this.els.ignoredSlot.replaceChildren(banner(
            "warn", text, () => this.els.ignoredSlot.replaceChildren(), this));
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
        const resolution = resolveView(this.doc, mode);
        if (resolution.candidate) {
            this.applyOrBanner(d => selectView(d, mode, resolution.candidate.tableId));
            return;
        }
        if (resolution.status === "ambiguous") {
            const name = this.t(mode === "groupBy" ? "group.label" : `toolbar.${mode}`);
            this.showError(new Error(this.t("view.ambiguous", {
                mode: name,
                tables: resolution.candidates.map(candidate => candidate.tableId).join(", "),
            })));
            return;
        }
        if (mode === "grid") {
            this.showError(new Error(this.t("view.baseUnavailable")));
            return;
        }
        openViewDialog(this, mode);
    }
}
