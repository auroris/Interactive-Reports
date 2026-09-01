// Protocol contract: the private report controller owns the state-document lifecycle. The report
// state doc is the single source of truth; the controller builds it, POSTs it, and routes the
// response to the renderers. The skeleton, menus, search, dialogs, and saved-report features
// live in modules that operate on the controller through this class's internal surface: doc,
// els, apply/applyOrBanner, runQuery, state transitions, restoreLastGood, normalize/serialize,
// reportUrl, and the notice slots.

import { api, apiUrl, defaultApiBase, errorText } from "../core/api.js";
import { banner, transientBanner } from "../core/dom.js";
import { loadWhoami } from "../core/identity.js";
import { resolveLocale, translate } from "../core/localization.js";
import { createWidgetRoot, disposeWidget, setHostStyleSheet } from "../core/widget.js";
import { applyFeatureChrome, buildSkeleton } from "./skeleton.js";
import { canonicalControlName, featureEnabled } from "./schema.js";
import {
    invalidateChangedSchemas,
    modeOf,
    normalizeReportState,
    resolveView,
    selectView,
    serializeReportState,
} from "./state.js";
import { loadSavedList, refreshSavedSelect } from "./saved.js";
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

const USER_QUERY_DEBOUNCE_MS = 200;

const invalidState = message => {
    const error = new Error(message);
    error.name = "InvalidStateError";
    return error;
};

const invalidDocument = cause => new TypeError(
    "The report document must be a JSON-compatible object.",
    cause === undefined ? undefined : { cause });

/**
 * Produces the detached transport form of a caller-supplied report document and verifies that it
 * can cross the JSON protocol boundary.
 *
 * @param {unknown} value - The public API input.
 * @returns {object} A detached JSON-compatible report document.
 */
function copyReportDocument(value) {
    if (!value || typeof value !== "object" || Array.isArray(value)) throw invalidDocument();
    try {
        const copy = serializeReportState(value);
        return JSON.parse(JSON.stringify(copy));
    } catch (error) {
        throw invalidDocument(error);
    }
}

const defaultBase = defaultApiBase();
const controllers = new WeakMap();

const controllerFor = element => {
    const controller = controllers.get(element);
    if (!controller) throw invalidState("The report controller is unavailable.");
    return controller;
};

class ReportController {

    /**
     * Initializes the component state required by the browser report controller.
     */
    constructor(host, root, mount) {
        this.host = host;
        this._root = root;
        this._mount = mount;
        this._seq = 0;
        this._busyTokens = new Set();
        this._initialized = false;
        this._stateRevision = 0;
        this._requestId = 0;
        this._controlOverrides = new Map();
        this._savedListLoaded = false;
        this._savedListPromise = null;
        this._scheduledUserQuery = null;
    }

    get shadowRoot() { return this.host.shadowRoot; }
    get nodeType() { return 1; }
    get parentElement() { return this.host.parentElement; }
    get ownerDocument() { return this.host.ownerDocument; }
    get isConnected() { return this.host.isConnected; }
    getRootNode(options) { return this.host.getRootNode(options); }
    get apiBase() {
        return this.host.getAttribute("api-base")
            ?? this.host.getAttribute("base")
            ?? defaultBase;
    }
    get base() { return this.apiBase.replace(/\/+$/, ""); }
    get downloadBase() {
        const configured = this.host.getAttribute("download-base")?.replace(/\/+$/, "");
        if (configured) return configured;
        return /\/reports$/i.test(this.base)
            ? this.base.replace(/reports$/i, "download")
            : "/api/download";
    }
    get locale() { return resolveLocale(this.host); }
    getAttribute(name) { return this.host.getAttribute(name); }
    hasAttribute(name) { return this.host.hasAttribute(name); }
    dispatchEvent(event) { return this.host.dispatchEvent(event); }
    t(key, values = {}) { return translate(this.host, key, values); }

    clearError() { this.els?.errorSlot?.replaceChildren(); }
    notify(text, kind = "ok") {
        if (this.els?.transientSlot)
            transientBanner(this.els.transientSlot, kind, text, 4000, this.host);
    }

    disposeTransients() {
        disposeWidget(this);
        disposeWidget(this.host);
    }
    /**
     * Returns the appsettings report configuration requested by the host attribute.
     *
     * @returns {string|null} The requested report configuration name.
     */
    get requestedReportName() { return this.getAttribute("report"); }
    /**
     * Returns the saved report-document id requested by the host attribute.
     *
     * @returns {string|null} The requested saved report-document id.
     */
    get requestedSavedReportId() { return this.getAttribute("saved-report"); }
    /**
     * Returns the numeric id of the active report-family anchor document.
     *
     * @returns {string|null} The report-document id.
     */
    get reportId() { return this._activeReportId ?? null; }
    /** Returns the configured definition key learned from the active anchor document. */
    get definitionName() { return this._activeDefinitionName ?? null; }

    /**
     * Returns whether every package-owned control is temporarily inert.
     *
     * @returns {boolean} Whether the host carries the standard boolean `disabled` attribute.
     */
    get disabled() { return this.host.hasAttribute("disabled"); }

    /**
     * Schedules initialization when the custom element is attached to a document.
     *
     * @returns {void} No value.
     *
     * Side effects: queues a microtask that initializes the element if it remains connected.
     */
    connectedCallback() {
        this.refreshDisabledState();
        this.scheduleInit();
    }
    /**
     * Releases request and chart resources when the custom element leaves the document.
     *
     * @returns {void} No value.
     *
     * Side effects: advances the inherited lifecycle sequence, aborts the active request, and destroys the chart instance.
     */
    disconnectedCallback() {
        ++this._seq;
        this.disposeTransients();
        this.cancelScheduledUserQuery();
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
    attributeChangedCallback(name, oldValue, newValue) {
        if (oldValue === newValue) return;
        if (name === "stylesheet") {
            this.refreshStyleSheet();
            return;
        }
        if (name === "disabled") {
            this.refreshDisabledState();
            return;
        }
        if (this._initialized) this.scheduleInit();
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
     * Replaces any pending user query with one trailing-edge timer.
     *
     * @returns {Promise<boolean>} True when this call owns the elapsed timer; false when superseded.
     */
    scheduleUserQuery() {
        this.cancelScheduledUserQuery();
        return new Promise(resolve => {
            const scheduled = {
                resolve,
                timer: setTimeout(() => {
                    if (this._scheduledUserQuery !== scheduled) return;
                    this._scheduledUserQuery = null;
                    resolve(true);
                }, USER_QUERY_DEBOUNCE_MS),
            };
            this._scheduledUserQuery = scheduled;
        });
    }

    /** Resolves and removes a superseded user query before it reaches the transport. */
    cancelScheduledUserQuery() {
        const scheduled = this._scheduledUserQuery;
        if (!scheduled) return;
        this._scheduledUserQuery = null;
        clearTimeout(scheduled.timer);
        scheduled.resolve(false);
    }

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
     * Side effects: invalidates state transitions and clears report data and saved/search selections.
     */
    resetReportContext() {
        this.cancelScheduledUserQuery();
        this._stateRevision++;
        this.schema = null;
        this.doc = null;
        this.lastResult = null;
        this.savedList = [];
        this.currentSaved = null;
        this._activeDefinitionName = null;
        this.searchScopeCol = null;
        this._lastGood = null;
        this._savedListLoaded = false;
        this._savedListPromise = null;
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
        this._activeReportId = null;
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
     * @param {string} name - The appsettings report configuration name to activate.
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
        this._activeReportId = null;
        this.clearReportView();
        refreshSavedSelect(this);
        const finishBusy = this.beginBusy();

        try {
            const requestedSaved = this.requestedSavedReportId?.trim();
            const saved = await api(apiUrl(this.base, name));
            if (seq !== this._seq) return false;
            const selected = requestedSaved
                ? saved.find(candidate => String(candidate.id) === requestedSaved)
                : saved.find(candidate => candidate.isDefault);
            if (!selected)
                throw new Error(requestedSaved
                    ? this.t("saved.unavailable")
                    : `Report configuration “${name}” has no default document.`);

            const definitionName = selected.reportName?.trim();
            if (!definitionName)
                throw new Error("The report listing did not identify its configured definition.");
            if (definitionName.localeCompare(name, undefined, { sensitivity: "accent" }) !== 0)
                throw new Error("The default report document belongs to a different report definition.");
            this._activeDefinitionName = definitionName;
            this._activeReportId = String(selected.id);

            // Schema and processing are definition operations. Only document discovery and
            // persistence use numeric document ids.
            const [docResponse, schema] = await Promise.all([
                api(apiUrl(this.base, definitionName, selected.id)),
                api(apiUrl(this.base, definitionName, "schema")),
            ]);
            if (seq !== this._seq) return;
            if (docResponse.summary?.reportName !== definitionName)
                throw new Error("The selected report document belongs to a different report definition.");
            this.schema = schema;
            applyFeatureChrome(this);
            this._savedListLoaded = true;
            this.savedList = saved;

            this.currentSaved = docResponse.summary;
            if (!saved.some(candidate => String(candidate.id) === String(docResponse.summary.id)))
                this.savedList = [...saved, docResponse.summary];
            this.adoptState(docResponse.state);
            refreshSavedSelect(this);
            await this.runQuery({ quiet, source: "initial" });
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
     * @param {{resetPageIndex?: boolean}} [options={}] - Whether adoption starts again on page one.
     * @returns {object} A detached, structurally valid working document using the schema's defaults and page-size limit.
     */
    normalize(raw, options = {}) {
        return normalizeReportState(
            raw,
            this.schema?.limits?.defaultPageSize ?? 50,
            this.schema?.defaultState,
            options);
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
     * Returns the current accepted report document as a detached JSON-compatible object.
     *
     * @returns {object} The canonical transport document. Mutating it cannot mutate the widget.
     * @throws {Error} When the initial report query has not completed successfully.
     */
    getReportDocument() {
        if (!this.reportId || !this.definitionName || !this.schema || !this.doc || !this.lastResult)
            throw invalidState("The report must finish loading before its document can be read.");
        return this.serialize();
    }

    /**
     * Requests the bounded distinct values for one column of a submitted current report document.
     *
     * @param {{document?: object, table?: string, column: string, search?: string, signal?: AbortSignal}} options - The document lookup coordinates and optional cancellation.
     * @returns {Promise<{table: string, column: string, type: string, items: Array<unknown>, truncated: boolean}>} The server-bounded LOV result.
     * @throws {Error} When the report is not loaded, the coordinates are missing, or the request fails.
     *
     * Side effects: posts the complete document to the report's query-authorized LOV endpoint.
     */
    async getListOfValues(options = {}) {
        if (!this.reportId || !this.schema || !this.doc || !this.lastResult)
            throw invalidState("The report must finish loading before values can be requested.");
        const {
            document = this.serialize(),
            table = this.doc.activeTable ?? "definition",
            column,
            search = "",
            signal,
        } = options;
        if (typeof table !== "string" || !table.trim()) throw new TypeError("A current table is required.");
        if (typeof column !== "string" || !column.trim()) throw new TypeError("A current-table column is required.");
        if (typeof search !== "string") throw new TypeError("LOV search text must be a string.");

        const result = await api(this.definitionUrl("lov"), {
            method: "POST",
            body: {
                document: copyReportDocument(document),
                table: table.trim(),
                column: column.trim(),
                search,
            },
            signal,
        });
        return structuredClone(result);
    }

    /**
     * Replaces the working document, submits it through the ordinary query pipeline, and adopts the
     * server-enriched response atomically.
     *
     * @param {object} document - A JSON-compatible report document.
     * @returns {Promise<object|undefined>} A detached query result, or `undefined` when canceled or superseded.
     * @throws {TypeError} When `document` is not a JSON-compatible object.
     * @throws {Error} When the report is not loaded or the current submission fails.
     *
     * Side effects: aborts an older query, dispatches query lifecycle events, performs a POST,
     * rerenders on success, and restores the last validated document on current failure or cancellation.
     */
    async submitReportDocument(document) {
        if (!this.reportId || !this.definitionName || !this.schema || !this.doc || !this.lastResult)
            throw invalidState("The report must finish loading before a document can be submitted.");

        const prev = this.doc;
        const next = this.normalize(copyReportDocument(document), { resetPageIndex: false });
        invalidateChangedSchemas(prev, next);
        const transition = this.beginStateTransition();
        this.doc = next;
        this.els.search.value = next.search ?? "";
        try {
            const result = await this.runQuery({ quiet: true, source: "host" });
            if (!result && this.isCurrentStateTransition(transition)) this.restoreLastGood(prev);
            return result ? structuredClone(result) : undefined;
        } catch (error) {
            if (this.isCurrentStateTransition(transition)) this.restoreLastGood(prev);
            throw error;
        }
    }

    /**
     * Overrides one package-owned report control, or restores the server suggestion.
     *
     * @param {string} name - A canonical report feature/control name, matched case-insensitively.
     * @param {boolean|null|undefined} enabled - `true` or `false` overrides the server; nullish restores it.
     * @returns {boolean} The control's effective state after the change.
     */
    setControlEnabled(name, enabled) {
        const canonical = canonicalControlName(name);
        if (!canonical) throw new TypeError(`Unknown report control: ${String(name)}`);
        if (enabled !== null && enabled !== undefined && typeof enabled !== "boolean")
            throw new TypeError("A report control override must be true, false, or null.");

        const had = this._controlOverrides.has(canonical);
        const previous = this._controlOverrides.get(canonical);
        if (enabled === null || enabled === undefined) this._controlOverrides.delete(canonical);
        else this._controlOverrides.set(canonical, enabled);
        if (had !== this._controlOverrides.has(canonical)
            || previous !== this._controlOverrides.get(canonical))
            this.refreshControlSurface();
        return featureEnabled(this, canonical);
    }

    /**
     * Applies several client control overrides as one visual update. Unmentioned controls retain
     * their current override; a nullish value restores the server suggestion for that control.
     *
     * @param {Record<string, boolean|null|undefined>} overrides - Control names and override values.
     * @returns {object} The resulting detached override map in canonical spelling.
     */
    setControlOverrides(overrides) {
        if (!overrides || typeof overrides !== "object" || Array.isArray(overrides))
            throw new TypeError("Control overrides must be an object.");

        const changes = [];
        for (const [name, enabled] of Object.entries(overrides)) {
            const canonical = canonicalControlName(name);
            if (!canonical) throw new TypeError(`Unknown report control: ${String(name)}`);
            if (enabled !== null && enabled !== undefined && typeof enabled !== "boolean")
                throw new TypeError("A report control override must be true, false, or null.");
            changes.push([canonical, enabled]);
        }
        for (const [canonical, enabled] of changes) {
            if (enabled === null || enabled === undefined) this._controlOverrides.delete(canonical);
            else this._controlOverrides.set(canonical, enabled);
        }
        if (changes.length) this.refreshControlSurface();
        return this.getControlOverrides();
    }

    /**
     * Removes every client control override and resumes following the active server suggestions.
     *
     * @returns {void} No value.
     */
    clearControlOverrides() {
        if (!this._controlOverrides.size) return;
        this._controlOverrides.clear();
        this.refreshControlSurface();
    }

    /**
     * Returns whether one report control is effectively available after client override precedence.
     *
     * @param {string} name - A report feature/control name, matched case-insensitively.
     * @returns {boolean} The effective control state.
     */
    isControlEnabled(name) {
        const canonical = canonicalControlName(name);
        if (!canonical) throw new TypeError(`Unknown report control: ${String(name)}`);
        return featureEnabled(this, canonical);
    }

    /**
     * Returns the explicit client control overrides as a detached object.
     *
     * @returns {Record<string, boolean>} Canonically named overrides.
     */
    getControlOverrides() { return Object.fromEntries(this._controlOverrides); }

    /** Synchronizes the application-owned stylesheet attribute into the shadow root. */
    refreshStyleSheet() {
        setHostStyleSheet(this.host, this.host.getAttribute("stylesheet"));
    }

    /**
     * Builds a processing URL for the active configured report definition.
     *
     * @param {string} resource - The report-relative API resource path.
     * @returns {string} The definition-scoped API URL.
     */
    definitionUrl(resource) {
        return apiUrl(this.base, this.definitionName, resource);
    }

    /** Builds a document-family persistence URL using the numeric anchor id. */
    documentFamilyUrl(resource) {
        return apiUrl(this.base, this.reportId, resource);
    }

    /**
     * Requests the current report in a generated format without starting a browser download.
     *
     * @param {string} [format="csv"] - The server-supported export format. The built-in exporter supports `csv`.
     * @param {{signal?: AbortSignal}} [options={}] - Optional cancellation for the export request.
     * @returns {Promise<{blob: Blob, filename: string, contentType: string, truncated: boolean}>} The generated file and response metadata.
     *
     * Side effects: performs the export request; download behavior is controlled by `options`.
     */
    getExport(format = "csv", options = {}) {
        return retrieveExport(this, format, options);
    }

    /**
     * Synchronizes the standard boolean `disabled` state to the isolated report surface.
     *
     * @returns {void} No value.
     *
     * Side effects: makes the surface inert, exposes its disabled state to accessibility APIs,
     * and closes transient UI that otherwise lives beside the inert surface in the shadow root.
     */
    refreshDisabledState() {
        if (!this._mount) return;
        const disabled = this.disabled;
        this._mount.inert = disabled;
        this._mount.toggleAttribute("inert", disabled);
        this._mount.setAttribute("aria-disabled", String(disabled));
        if (disabled) this.disposeTransients();
    }

    /**
     * Rebuilds every control-bearing surface after a client feature override changes.
     *
     * @returns {void} No value.
     *
     * Side effects: closes stale menus/dialogs, refreshes chrome and result controls, and may
     * lazily request saved-report summaries when the client force-enables that control family.
     */
    refreshControlSurface() {
        if (!this.els) return;
        this.disposeTransients();
        if (this.schema) applyFeatureChrome(this);
        if (this.doc) renderChips(this, this.els.chips);
        if (this.lastResult) {
            this.renderView();
            renderPager(this, this.els.pager);
            this.refreshViewButtons();
        }
        if (this.schema && featureEnabled(this, "savedReports") && !this._savedListLoaded)
            this.ensureSavedList();
    }

    /**
     * Loads the saved-report list once for a report whose client controls were enabled after activation.
     *
     * @returns {Promise<void>} The in-flight or completed lazy-load operation.
     */
    ensureSavedList() {
        if (this._savedListLoaded) return Promise.resolve();
        if (this._savedListPromise) return this._savedListPromise;
        const sequence = this._seq;
        const reportId = this.reportId;
        const promise = this._savedListPromise = loadSavedList(this).then(() => {
            if (sequence !== this._seq || reportId !== this.reportId) return;
            this._savedListLoaded = true;
            refreshSavedSelect(this);
            applyFeatureChrome(this);
        }).finally(() => {
            if (this._savedListPromise === promise) this._savedListPromise = null;
        });
        return promise;
    }

    /**
     * Submits the working report state, adopts the validated result, and refreshes the view.
     *
     * @param {{quiet?: boolean, source?: string}} [opts={}] - Controls banner rendering and identifies the query initiator to lifecycle events.
     * @returns {Promise<object|undefined>} The accepted query response, or undefined when superseded or aborted.
     *
     * Side effects: aborts the previous query, posts serialized state, adopts the validated document, commits rollback state, and rerenders results, controls, and notices.
     */
    async runQuery(opts = {}) {
        this._abort?.abort();
        this._abort = null;
        const source = opts.source ?? "refresh";
        if (source === "user") {
            if (!await this.scheduleUserQuery()) return;
        } else {
            this.cancelScheduledUserQuery();
        }
        const ctrl = this._abort = new AbortController();
        const requestId = ++this._requestId;
        const finishBusy = this.beginBusy();
        try {
            const beforeHook = this.serialize();
            const detail = {
                document: structuredClone(beforeHook),
                source,
                requestId,
                signal: ctrl.signal,
            };
            const EventType = this.ownerDocument?.defaultView?.CustomEvent ?? globalThis.CustomEvent;
            const proceed = this.dispatchEvent(new EventType("ir-before-query", {
                bubbles: true,
                composed: true,
                cancelable: true,
                detail,
            }));
            if (!proceed) {
                if (ctrl === this._abort) this._abort = null;
                return;
            }

            const outgoing = copyReportDocument(detail.document);
            invalidateChangedSchemas(beforeHook, outgoing);
            const submitted = serializeReportState(outgoing);
            const result = await api(this.definitionUrl("query"), {
                method: "POST", body: submitted, signal: ctrl.signal,
            });
            if (ctrl !== this._abort) return;
            const accepted = copyReportDocument(result.document ?? submitted);
            const completedResult = {
                ...result,
                document: structuredClone(accepted),
            };
            this.doc = structuredClone(accepted);
            this.els.search.value = this.doc.search ?? "";
            this.lastResult = completedResult;
            // Protocol contract: the returned document is the submitted working copy with null
            // schema caches replaced by the server. A superseding operation aborts this request
            // before this point, so it cannot overwrite newer edits.
            this.commitLastGood(accepted);
            this.clearError();
            renderChips(this, this.els.chips);
            this.renderView();
            renderPager(this, this.els.pager);
            this.renderIgnored(completedResult.ignored);
            this.refreshViewButtons();
            this.dispatchEvent(new EventType("ir-query-complete", {
                bubbles: true,
                composed: true,
                detail: {
                    document: structuredClone(accepted),
                    result: structuredClone(completedResult),
                    submitted: structuredClone(submitted),
                    source,
                    requestId,
                },
            }));
            return completedResult;
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
     * @param {{resetPage?: boolean, source?: string}} [options={}] - Controls paging and identifies the query initiator.
     * @returns {Promise<void>} Resolves after the edited document is validated and rendered; rejects after restoring validated state when the current query fails.
     *
     * Side effects: installs the edited state, invalidates affected schema caches, performs a query, and restores validated state if the current transition fails.
     */
    async apply(mutate, { resetPage = true, source = "user" } = {}) {
        const prev = this.doc;
        const next = structuredClone(this.doc);
        mutate(next);
        invalidateChangedSchemas(prev, next);
        if (resetPage && next.page) next.page.index = 1;
        const transition = this.beginStateTransition();
        this.doc = next;
        try {
            const result = await this.runQuery({ quiet: true, source });
            if (!result && this.isCurrentStateTransition(transition)) this.restoreLastGood(prev);
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
        this.cancelScheduledUserQuery();
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
        const slot = this.els?.errorSlot;
        if (!slot) return;
        slot.replaceChildren(
            banner("error", errorText(err, friendly, this.host), () => this.clearError(), this.host));
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

/**
 * Public browser interface for one interactive report.
 *
 * All mutable report and rendering state is held by a closure-owned controller in `controllers`.
 * The custom element deliberately exposes only host configuration and supported integration APIs.
 *
 * @fires ir-before-query Cancelable query-transform event with a detached document.
 * @fires ir-query-complete Observational event after a current result is adopted and rendered.
 * @fires ir-action Application action event emitted by an action-format cell.
 */
export class InteractiveReportElement extends HTMLElement {
    static observedAttributes = [
        "report", "saved-report", "api-base", "base", "lang", "disabled", "stylesheet",
    ];

    constructor() {
        super();
        const { root, mount } = createWidgetRoot(this);
        const controller = new ReportController(this, root, mount);
        controllers.set(this, controller);
        controller.refreshStyleSheet();
    }

    connectedCallback() { controllerFor(this).connectedCallback(); }
    disconnectedCallback() { controllerFor(this).disconnectedCallback(); }
    attributeChangedCallback(name, oldValue, newValue) {
        controllerFor(this).attributeChangedCallback(name, oldValue, newValue);
    }

    /**
     * The active report-family anchor id. The `report` attribute contains the appsettings
     * configuration name instead, so this remains null until family bootstrap succeeds.
     *
     * @returns {string|null} The active report-document id.
     */
    get reportId() { return controllerFor(this).reportId; }

    /** The configured definition key learned after the anchor document is retrieved. */
    get definitionName() { return controllerFor(this).definitionName; }

    /**
     * The explicit `api-base`, legacy `base`, or bundle-relative default.
     *
     * @returns {string} The normalized API prefix.
     */
    get apiBase() { return controllerFor(this).apiBase; }
    /** @param {string|null|undefined} value - The new `api-base`; nullish removes the attribute. */
    set apiBase(value) {
        if (value === null || value === undefined) this.removeAttribute("api-base");
        else this.setAttribute("api-base", String(value));
    }

    /** The file-client endpoint prefix; defaults beside an <c>api-base</c> ending in <c>/reports</c>. */
    get downloadBase() { return controllerFor(this).downloadBase; }
    /** @param {string|null|undefined} value - The new download prefix; nullish restores the default. */
    set downloadBase(value) {
        if (value === null || value === undefined) this.removeAttribute("download-base");
        else this.setAttribute("download-base", String(value));
    }

    /**
     * The application-owned stylesheet URL injected into this report's shadow root.
     *
     * @returns {string|null} The configured URL, or null when no host stylesheet is present.
     */
    get styleSheet() { return this.getAttribute("stylesheet"); }
    /** @param {string|null|undefined} value - The new URL; nullish removes the stylesheet. */
    set styleSheet(value) {
        if (value === null || value === undefined) this.removeAttribute("stylesheet");
        else this.setAttribute("stylesheet", String(value));
    }

    /**
     * Whether every package-owned interactive control is inert. Control overrides are retained.
     *
     * @returns {boolean} True when the standard boolean `disabled` attribute is present.
     */
    get disabled() { return this.hasAttribute("disabled"); }
    /** @param {boolean} value - Whether to set the standard boolean `disabled` attribute. */
    set disabled(value) { this.toggleAttribute("disabled", Boolean(value)); }

    /**
     * Returns the accepted report document as a detached JSON-compatible object.
     *
     * @returns {object} The canonical transport document.
     * @throws {Error} When the initial query has not completed successfully.
     */
    getReportDocument() { return controllerFor(this).getReportDocument(); }

    /**
     * Returns up to 50 distinct values for one column of the supplied current report document.
     * Defaults use this element's accepted, possibly unsaved document and its active table.
     *
     * @param {{document?: object, table?: string, column: string, search?: string, signal?: AbortSignal}} options - Lookup coordinates, optional document/search, and cancellation.
     * @returns {Promise<{table: string, column: string, type: string, items: Array<unknown>, truncated: boolean}>} The bounded LOV result.
     */
    getListOfValues(options) { return controllerFor(this).getListOfValues(options); }

    /**
     * Replaces the working document and submits it through the ordinary query pipeline.
     * A current failure restores the last accepted document.
     *
     * @param {object} document - A JSON-compatible report document.
     * @returns {Promise<object|undefined>} A detached result, or undefined when canceled or superseded.
     * @throws {TypeError} When the document is not JSON-compatible.
     * @throws {Error} When the report is not loaded or the request fails.
     */
    submitReportDocument(document) {
        return controllerFor(this).submitReportDocument(document);
    }

    /**
     * Retrieves a generated representation of the accepted report without starting a browser download.
     *
     * @param {string} [format="csv"] - The server-supported format token.
     * @param {{signal?: AbortSignal}} [options={}] - Optional request cancellation.
     * @returns {Promise<{blob: Blob, filename: string, contentType: string, truncated: boolean}>} File data and response metadata.
     * @throws {Error} When the report is not loaded or the request fails.
     */
    getExport(format = "csv", options = {}) {
        return controllerFor(this).getExport(format, options);
    }

    /**
     * Overrides one package-owned control, or restores the server suggestion with a nullish value.
     *
     * @param {string} name - A supported control name, matched case-insensitively.
     * @param {boolean|null|undefined} enabled - The override, or nullish to inherit the server suggestion.
     * @returns {boolean} The control's effective state.
     * @throws {TypeError} When the name or value is invalid.
     */
    setControlEnabled(name, enabled) {
        return controllerFor(this).setControlEnabled(name, enabled);
    }

    /**
     * Applies several client control overrides as one visual update.
     *
     * @param {Record<string, boolean|null|undefined>} overrides - Control names and values.
     * @returns {Record<string, boolean>} The detached explicit override map.
     * @throws {TypeError} When any name or value is invalid.
     */
    setControlOverrides(overrides) {
        return controllerFor(this).setControlOverrides(overrides);
    }

    /** Removes every client override and resumes following server suggestions. */
    clearControlOverrides() { return controllerFor(this).clearControlOverrides(); }

    /**
     * Returns one control's effective state after client override precedence.
     *
     * @param {string} name - A supported control name, matched case-insensitively.
     * @returns {boolean} The effective state.
     * @throws {TypeError} When the name is unknown.
     */
    isControlEnabled(name) { return controllerFor(this).isControlEnabled(name); }

    /** @returns {Record<string, boolean>} A detached map of explicit client overrides. */
    getControlOverrides() { return controllerFor(this).getControlOverrides(); }
}
