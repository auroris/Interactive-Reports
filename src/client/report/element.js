// Protocol contract: the <interactive-report> element: the state-document lifecycle. The report
// state doc is the single source of truth; the element builds it, POSTs it, and routes the
// response to the renderers. The skeleton, menus, search, dialogs, and saved-report features
// live in modules that operate on the element through this class's surface: doc,
// els, apply/applyOrBanner, runQuery, state transitions, restoreLastGood, normalize/serialize,
// reportUrl, and the notice slots.

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

// Chart.js glue ships as its own bundle beside ir.js and loads the first time any report on the
// page enters chart view. The URL is computed at runtime so the bundler leaves the import
// dynamic instead of inlining the chunk here.
let chartModulePromise;
/**
 * Loads the optional chart renderer on demand and caches the resulting module.
 *
 * @returns {Promise<object>} A promise resolving to the cached chart renderer module.
 */
const loadChartModule = () =>
    chartModulePromise ??= import(new URL("./ir-chart.js", import.meta.url).href)
        .catch(err => { chartModulePromise = undefined; throw err; });

export class InteractiveReportElement extends WidgetElement {
    static observedAttributes = ["report", "saved-report", "api-base", "base", "lang"];

    /**
     * Initializes the component state required by the browser report controller.
     */
    constructor() {
        super();
        this._busyTokens = new Set();
        this._initialized = false;
        this._stateRevision = 0;
    }
    /**
     * Returns the report name requested by the host attribute.
     *
     * @returns {string|null} The requested report name.
     */
    get requestedReportName() { return this.getAttribute("report"); }
    /**
     * Returns the saved-report title requested by the host attribute.
     *
     * @returns {string|null} The requested saved report name.
     */
    get requestedSavedReportName() { return this.getAttribute("saved-report"); }
    /**
     * Returns the canonical name of the active report.
     *
     * @returns {string|null} The report name.
     */
    get reportName() { return this._activeReportName ?? this.requestedReportName; }

    /**
     * Schedules initialization when the custom element is attached to a document.
     *
     * @returns {void} No value.
     *
     * Side effects: queues a microtask that initializes the element if it remains connected.
     */
    connectedCallback() { this.scheduleInit(); }
    /**
     * Releases request and chart resources when the custom element leaves the document.
     *
     * @returns {void} No value.
     *
     * Side effects: advances the inherited lifecycle sequence, aborts the active request, and destroys the chart instance.
     */
    disconnectedCallback() {
        super.disconnectedCallback();
        this._abort?.abort();
        this._abort = null;
        this.destroyChart();
    }
    /**
     * Reinitializes component state when a watched host attribute changes.
     *
     * @param {string} _name - The changed observed attribute name; its identity is not otherwise needed.
     * @param {string|null} oldValue - The attribute's previous serialized value.
     * @param {string|null} newValue - The attribute's new serialized value.
     * @returns {void} No value.
     *
     * Side effects: schedules reinitialization when an initialized element receives a different serialized value.
     */
    attributeChangedCallback(_name, oldValue, newValue) {
        if (this._initialized && oldValue !== newValue) this.scheduleInit();
    }

    /**
     * Queues one component reinitialization after the current microtask.
     *
     * @returns {void} No value.
     *
     * Side effects: coalesces calls into one microtask and may start asynchronous initialization.
     */
    scheduleInit() {
        if (this._initQueued) return;
        this._initQueued = true;
        queueMicrotask(() => { this._initQueued = false; if (this.isConnected) this.init(); });
    }

    // Lifecycle sequencing makes every asynchronous activation conditional on the latest element
    // generation. Busy tokens are independent so overlapping operations cannot clear aria-busy early.

    /**
     * Marks the widget busy and returns a callback that releases the busy token.
     *
     * @returns {() => void} An idempotency-agnostic callback that releases this operation's token.
     *
     * Side effects: adds a busy token and sets `aria-busy`; the returned callback removes that token and clears the attribute when none remain.
     */
    beginBusy() {
        const token = Symbol("report operation");
        this._busyTokens.add(token);
        this._mount.setAttribute("aria-busy", "true");
        return () => {
            this._busyTokens.delete(token);
            if (!this._busyTokens.size) this._mount.setAttribute("aria-busy", "false");
        };
    }

    /**
     * Clears report-specific state before another report is activated.
     *
     * @returns {void} No value.
     *
     * Side effects: invalidates state transitions, clears report data and saved/search selections, and removes the custom stylesheet.
     */
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

    /**
     * Clears rendered report content while retaining the widget shell.
     *
     * @returns {void} No value.
     *
     * Side effects: clears search, chart, table, pager, chip, and error UI while restoring the table layout.
     */
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

    /**
     * Rebuilds the widget shell, resolves the current identity, and activates the requested report.
     *
     * @returns {Promise<void>} Resolves after activation finishes, the request is superseded, or an initialization error is displayed.
     *
     * Side effects: invalidates older lifecycle work, aborts the active request, rebuilds DOM, may perform network I/O, and displays identity or activation errors.
     */
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

    /**
     * Loads a report's schema, saved state, and initial query result as one sequenced activation.
     *
     * @param {string} name - The report definition name to activate.
     * @param {number} [seq=++this._seq] - The lifecycle sequence used to reject stale asynchronous work.
     * @param {{quiet?: boolean}} [options={}] - Set `quiet` to suppress query errors during activation.
     * @returns {Promise<boolean|undefined>} True after a current successful query, false for failure or stale work detected at most checkpoints, and undefined at the saved-list checkpoint.
     *
     * Side effects: aborts prior work, fetches schema and saved reports, adopts initial state, runs a query, and updates styles, controls, notices, and saved selection.
     */
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
            // Schema is the loadability gate. Do not issue saved-state or query requests for
            // this report until its definition is accessible and valid.
            const schema = await api(apiUrl(this.base, name, "schema"));
            if (seq !== this._seq) return false;
            this.schema = schema;
            setCustomStyleSheet(this, schema.styleSheet);
            applyFeatureChrome(this);
            // Invariant: a missing saved endpoint means the feature is off; any other failure
            // must not masquerade as "no saved reports exist".
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

    // State-document plumbing keeps the server protocol boundary explicit: normalize data on
    // entry, serialize only protocol fields on exit, and address every request under the active report.

    /**
     * Normalizes an untrusted report-state value against the active schema defaults.
     *
     * @param {object} raw - The untrusted state value to normalize.
     * @returns {object} A detached, structurally valid working document using the schema's defaults and page-size limit.
     */
    normalize(raw) {
        return normalizeReportState(
            raw,
            this.schema?.limits?.defaultPageSize ?? 50,
            this.schema?.defaultState);
    }

    // Protocol contract: adopt a state document as the working copy. Server-delivered documents
    // are authoritative, and saved reports are accepted liberally: normalization guarantees
    // shape, the server judges the content on query — hard problems come back as a validation
    // response (and the failed operation rolls back), with soft drift returned as `ignored` notices.
    /**
     * Normalizes a server-owned report document and installs it as the working state.
     *
     * @param {object} rawState - The server-returned report state to normalize and adopt.
     * @returns {void} No value.
     *
     * Side effects: replaces `this.doc` and synchronizes the search input.
     */
    adoptState(rawState) {
        this.doc = this.normalize(rawState);
        this.els.search.value = this.doc.search ?? "";
    }

    // Protocol contract: canonical state: explicit empty values survive so they can clear
    // report defaults; undefined values and underscore-prefixed working data do not cross the
    // protocol.
    /**
     * Serializes the working report state into its transport-safe representation.
     *
     * @returns {object} A detached transport document without client-only fields.
     */
    serialize() {
        return serializeReportState(this.doc);
    }

    /**
     * Builds a report-relative API URL for the active report.
     *
     * @param {string} resource - The report-relative API resource path.
     * @returns {string} The API URL for the resource under the active report.
     */
    reportUrl(resource) {
        return apiUrl(this.base, this.reportName, resource);
    }

    /**
     * Requests the current report in a generated format without starting a browser download.
     *
     * @param {string} [format="csv"] - The server-supported export format, normally `csv` or `xlsx`.
     * @param {{signal?: AbortSignal}} [options={}] - Optional cancellation for the export request.
     * @returns {Promise<{blob: Blob, filename: string, contentType: string, truncated: boolean}>} The generated file and response metadata.
     *
     * Side effects: performs the export request; download behavior is controlled by `options`.
     */
    getExport(format = "csv", options = {}) {
        return retrieveExport(this, format, options);
    }

    /**
     * Submits the working report state, adopts the validated result, and refreshes the view.
     *
     * @param {{quiet?: boolean}} [opts={}] - Set `quiet` to suppress banner rendering when the request fails.
     * @returns {Promise<object|undefined>} The accepted query response, or undefined when superseded or aborted.
     *
     * Side effects: aborts the previous query, posts serialized state, adopts the validated document, commits rollback state, and rerenders results, controls, and notices.
     */
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
            // Protocol contract: the returned document is the submitted working copy with null
            // schema caches replaced by the server. A superseding operation aborts this request
            // before this point, so it cannot overwrite newer edits.
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

    // Invariant: route the result to the table or the chart. Only one is ever visible; the
    // other is emptied so stale content cannot flash back on the next switch.
    /**
     * Routes the current result to either the grid or chart renderer.
     *
     * @returns {void} No value.
     *
     * Side effects: updates the rendered DOM.
     */
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

    /**
     * Loads the chart renderer and draws the current result when the request is still current.
     *
     * @returns {Promise<void>} Resolves after the current chart is rendered, deemed stale, or replaced by a load error.
     *
     * Side effects: destroys any prior chart, dynamically loads chart code, and may render either a chart or a localized load error.
     */
    async renderChart() {
        const result = this.lastResult;
        this.destroyChart();
        try {
            const module = await loadChartModule();
            // The module load is async: bail if the widget moved on meanwhile.
            if (this.lastResult !== result || modeOf(this.doc) !== "chart" || !this.isConnected) return;
            this._chart = renderChartView(this, this.els.chartWrap, module);
        } catch {
            // A failed chunk load may settle after the user has already switched views,
            // reports, or disconnected the element. Do not leak that stale failure into the
            // current view.
            if (this.lastResult !== result || modeOf(this.doc) !== "chart" || !this.isConnected) return;
            this.els.chartWrap.replaceChildren();
            this.showError(new Error(this.t("chart.loadFailed")));
        }
    }

    /**
     * Destroys the active chart instance and releases its retained reference.
     *
     * @returns {void} No value.
     *
     * Side effects: calls the chart instance's `destroy` hook when present and clears its retained reference.
     */
    destroyChart() {
        this._chart?.destroy();
        this._chart = null;
    }

    /**
     * Optimistic apply: mutate a CLONE, install it, re-query, restore the last validated state on
     * failure. Mutating a clone keeps a mutator that throws mid-way (staged multi-column edits) from
     * leaving half its work in the live doc. Throws so dialogs can show the (precise) validation
     * problem and stay open.
     *
     * @param {(doc: object) => void} mutate - Synchronous callback that edits a cloned working document.
     * @param {{resetPage?: boolean}} [options={}] - Controls whether the next query starts at page one.
     * @returns {Promise<void>} Resolves after the edited document is validated and rendered; rejects after restoring validated state when the current query fails.
     *
     * Side effects: installs the edited state, invalidates affected schema caches, performs a query, and restores validated state if the current transition fails.
     */
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

    /**
     * Applies a state mutation and renders any failure in the report banner.
     *
     * @param {(doc: object) => void} mutate - Synchronous callback that edits a cloned working document.
     * @param {{resetPage?: boolean}} [opts] - Options forwarded to `apply`.
     * @returns {Promise<void>} A promise that settles after the mutation succeeds or its error is displayed.
     *
     * Side effects: performs the same state and query work as `apply`, then renders a banner instead of propagating a failure.
     */
    applyOrBanner(mutate, opts) {
        return this.apply(mutate, opts).catch(err => this.showError(err));
    }

    /**
     * Begins a state-replacing operation, invalidating older loads and queries.
     *
     * @returns {number} The newly allocated state revision.
     *
     * Side effects: increments the state revision and aborts the active query.
     */
    beginStateTransition() {
        const revision = ++this._stateRevision;
        this._abort?.abort();
        this._abort = null;
        return revision;
    }

    /**
     * Determines whether a revision still represents the current state transition.
     *
     * @param {number} revision - The state revision used to reject stale asynchronous work.
     * @returns {boolean} Whether the revision still identifies the latest state-replacing operation.
     */
    isCurrentStateTransition(revision) {
        return revision === this._stateRevision;
    }

    /**
     * Returns the revision of the current report-state transition.
     *
     * @returns {number} The state revision.
     */
    get stateRevision() { return this._stateRevision; }

    // Protocol contract: the last server-validated state is the rollback target for any failed
    // operation. Committed only on query success, so an operation whose query was aborted by a
    // newer one never becomes a rollback target: if that newer operation fails, the restore
    // skips past the aborted, never-validated intermediate back to validated ground.
    /**
     * Records the last server-validated report document as the rollback target.
     *
     * @param {object} [doc=this.doc] - The validated document to snapshot.
     * @returns {void} No value.
     *
     * Side effects: replaces the rollback snapshot with a deep copy and the current saved-report association and revision.
     */
    commitLastGood(doc = this.doc) {
        this._lastGood = {
            doc: structuredClone(doc),
            currentSaved: this.currentSaved,
            revision: this._stateRevision,
        };
    }

    /**
     * Records successful save metadata without treating the save as a validated query.
     *
     * @param {object} summary - The saved-report metadata returned by a successful write.
     * @param {number} revision - The state revision used to reject stale asynchronous work.
     * @returns {void} No value.
     *
     * Side effects: updates matching current and rollback saved-report metadata without changing the validated document.
     */
    recordSaved(summary, revision) {
        const sameWorkingCopy = this.isCurrentStateTransition(revision);
        const updatesCurrent = this.currentSaved?.id === summary.id;
        if (sameWorkingCopy || updatesCurrent) this.currentSaved = summary;

        const updatesLastGood = this._lastGood?.currentSaved?.id === summary.id;
        if (this._lastGood && (updatesLastGood
            || (sameWorkingCopy && this._lastGood.revision === revision)))
            this._lastGood.currentSaved = summary;
    }

    /**
     * Rationale: put doc, saved-report selection, search box, and chips back on the last validated
     * state (or the caller's fallback when nothing was validated yet). The rendered grid still shows
     * that state's result, so the widget is consistent again as a whole.
     *
     * @param {object|null} [fallbackDoc=null] - The fallback document restored when no validated snapshot exists.
     * @returns {void} No value.
     *
     * Side effects: restores document and saved selection, advances the snapshot revision, and refreshes search, chips, and saved-report controls.
     */
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

    // Invariant: a deleted saved report must not resurrect through a later rollback.
    /**
     * Removes a deleted saved-report association from the rollback snapshot.
     *
     * @param {string} id - The deleted saved-report identifier.
     * @returns {void} No value.
     *
     * Side effects: clears the rollback snapshot's saved-report association when its identifier matches.
     */
    forgetSaved(id) {
        if (this._lastGood?.currentSaved?.id === id) this._lastGood.currentSaved = null;
    }

    // Notice rendering translates transport failures and non-fatal validation drift into the
    // widget's dedicated error and warning regions.

    /**
     * Renders a normalized error in the widget's error region.
     *
     * @param {Error|string|object} err - The error value to normalize for display.
     * @returns {void} No value.
     *
     * Side effects: updates the rendered DOM.
     */
    showError(err) {
        // Protocol contract: friendly text remains as compatibility for older, bodiless
        // servers. A coded server error is more precise than either stock phrase.
        const error = err?.error ?? err?.problem ?? {};
        const hasServerText = error.title || error.description || error.detail;
        const friendly = err?.status === 401 && !hasServerText ? this.t("report.signIn")
            : err?.status === 404 && !hasServerText
                ? this.t("report.notFound")
                : null;
        super.showError(err, friendly);
    }

    /**
     * Renders non-fatal server diagnostics for ignored report-state entries.
     *
     * @param {Array<object>} ignored - The non-fatal server diagnostics to render for the user.
     * @returns {void} No value.
     *
     * Side effects: updates the rendered DOM.
     */
    renderIgnored(ignored) {
        if (!ignored?.length) { this.els.ignoredSlot.replaceChildren(); return; }
        const text = this.t("report.ignored", {
            details: ignored.map(i => `${i.kind} (${i.detail})`).join("; "),
        });
        this.els.ignoredSlot.replaceChildren(banner(
            "warn", text, () => this.els.ignoredSlot.replaceChildren(), this));
    }

    // View switching reuses an existing unambiguous table when possible. Creating a missing shaped
    // view is delegated to its editor so no incomplete shape enters the working document.

    /**
     * Updates each view button to reflect the active report mode.
     *
     * @returns {void} No value.
     *
     * Side effects: updates the rendered DOM.
     */
    refreshViewButtons() {
        const mode = this.doc ? modeOf(this.doc) : "grid";
        for (const btn of this.els.views.children)
            btn.setAttribute("aria-pressed", String(btn.dataset.mode === mode));
    }

    /**
     * Selects or creates the requested report view when the schema permits it.
     *
     * @param {string} mode - The built-in view mode requested by the toolbar.
     * @returns {void} No value.
     *
     * Side effects: may start an asynchronous state apply, show an ambiguity/unavailability error, or open the requested shape dialog.
     */
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
