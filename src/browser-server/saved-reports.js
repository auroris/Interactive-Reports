// Ephemeral in-memory saved-report store for the browser. It emulates the C# server's
// saved-report contract closely enough for a demo: summaries carry the fields the client
// reads (owner, mine, isReadOnly, modifiedUtc), updates honour visibility, default selection,
// and owner changes, titles are unique within their visibility scope, and the seeded default
// cannot be unset. Everything a visitor saves belongs to the single demo user.

export const DEMO_USER = "demo-user";

/** Thrown when a title is already taken in the scope it would compete in (IR-1309). */
export class SavedReportTitleConflictError extends Error {
    constructor(title) {
        super(`A saved report titled '${title}' already exists.`);
        this.name = "SavedReportTitleConflictError";
    }
}

/** Thrown when an update or delete would leave the family without a default (IR-1312). */
export class DefaultReportRequiredError extends Error {
    constructor() {
        super("Select another default report instead of unsetting the current default.");
        this.name = "DefaultReportRequiredError";
    }
}

const clone = value => (value ? JSON.parse(JSON.stringify(value)) : {});
const sameName = (a, b) => String(a).toLowerCase() === String(b).toLowerCase();

/** Projects a stored record to the summary shape the C# server returns. */
function summaryOf(report) {
    return {
        id: report.id,
        reportName: report.reportName,
        title: report.title,
        isGlobal: Boolean(report.isGlobal),
        isDefault: Boolean(report.isDefault),
        owner: report.owner ?? null,
        mine: report.owner === DEMO_USER,
        isReadOnly: false,
        modifiedUtc: report.modifiedUtc,
    };
}

export class EphemeralSavedReportStore {
    constructor() {
        this.nextId = 1;
        /** @type {Map<number, object>} */
        this.reports = new Map();
        /** @type {Map<string, number>} */
        this.defaultReportIds = new Map();
    }

    /**
     * Explicitly seeds a persisted default for a demo or administration setup.
     *
     * @param {string} reportName
     * @param {object} [defaultState={}]
     */
    ensureDefault(reportName, defaultState = {}) {
        const key = String(reportName || "").toLowerCase();
        if (this.defaultReportIds.has(key)) return;

        const id = this.nextId++;
        this.defaultReportIds.set(key, id);
        this.reports.set(id, {
            id,
            reportName,
            title: "Default",
            isDefault: true,
            isGlobal: true,
            owner: null,
            modifiedUtc: new Date().toISOString(),
            state: clone(defaultState),
        });
    }

    /**
     * Lists saved reports visible for the specified report definition, default first, then
     * public documents, then by title, as the C# listing orders them.
     *
     * @param {string} reportName
     * @returns {Array<object>} Array of saved report summaries
     */
    list(reportName) {
        return [...this.reports.values()]
            .filter(report => sameName(report.reportName, reportName))
            .map(summaryOf)
            .sort((a, b) => {
                if (a.isDefault !== b.isDefault) return a.isDefault ? -1 : 1;
                if (a.isGlobal !== b.isGlobal) return a.isGlobal ? -1 : 1;
                return a.title.localeCompare(b.title, undefined, { sensitivity: "base" }) || a.id - b.id;
            });
    }

    /**
     * Saves a new private or public report document owned by the demo user.
     *
     * @param {string} reportName
     * @param {object} request
     * @param {string} request.title
     * @param {object} request.state
     * @param {boolean} [request.isGlobal=false]
     * @returns {object} The created summary
     * @throws {SavedReportTitleConflictError} When the title is taken in its visibility scope.
     */
    save(reportName, { title, state, isGlobal = false }) {
        const id = this.nextId;
        const cleanTitle = String(title ?? "").trim() || `Report ${id}`;
        this.assertTitleAvailable(reportName, cleanTitle, Boolean(isGlobal), null);
        this.nextId++;
        const record = {
            id,
            reportName,
            title: cleanTitle,
            isDefault: false,
            isGlobal: Boolean(isGlobal),
            owner: DEMO_USER,
            modifiedUtc: new Date().toISOString(),
            state: clone(state),
        };
        this.reports.set(id, record);
        return summaryOf(record);
    }

    /**
     * Loads a saved report document and summary by report family and ID.
     *
     * @param {string} reportName
     * @param {number|string} id
     * @returns {{ summary: object, state: object }|null}
     */
    load(reportName, id) {
        const report = this.reports.get(Number(id));
        if (!report || !sameName(report.reportName, reportName)) return null;
        return { summary: summaryOf(report), state: clone(report.state) };
    }

    /**
     * Updates a saved report's title, state, visibility, default selection, or owner. Selecting
     * a new default publishes it and demotes the previous default to an ordinary public report.
     *
     * @param {number|string} id
     * @param {object} request
     * @param {string} [request.title]
     * @param {object} [request.state]
     * @param {boolean} [request.isGlobal]
     * @param {boolean} [request.isDefault]
     * @param {string|null} [request.owner]
     * @returns {object|null} Updated summary, or `null` when the id is unknown
     * @throws {DefaultReportRequiredError} When the update would unset the family default.
     * @throws {SavedReportTitleConflictError} When the title is taken in its new scope.
     */
    update(id, { title, state, isGlobal, isDefault, owner } = {}) {
        const report = this.reports.get(Number(id));
        if (!report) return null;

        const next = {
            title: title == null ? report.title : String(title).trim() || report.title,
            isGlobal: isGlobal == null ? report.isGlobal : Boolean(isGlobal),
            isDefault: isDefault == null ? report.isDefault : Boolean(isDefault),
            owner: owner === undefined ? report.owner : owner,
        };
        if (report.isDefault && !next.isDefault) throw new DefaultReportRequiredError();
        if (next.isDefault) next.isGlobal = true;
        this.assertTitleAvailable(report.reportName, next.title, next.isGlobal, report.id);

        const now = new Date().toISOString();
        if (next.isDefault && !report.isDefault) {
            const key = report.reportName.toLowerCase();
            const previous = this.reports.get(this.defaultReportIds.get(key));
            if (previous && previous.id !== report.id)
                Object.assign(previous, { isDefault: false, isGlobal: true, modifiedUtc: now });
            this.defaultReportIds.set(key, report.id);
        }
        Object.assign(report, next, { modifiedUtc: now });
        if (state != null) report.state = clone(state);
        return summaryOf(report);
    }

    /**
     * Deletes a saved report. The seeded default stays: the demo has nowhere to obtain a
     * replacement from.
     *
     * @param {number|string} id
     * @returns {boolean} True when deleted; false when the id is unknown.
     * @throws {DefaultReportRequiredError} When the report is the family default.
     */
    delete(id) {
        const report = this.reports.get(Number(id));
        if (!report) return false;
        if (report.isDefault) throw new DefaultReportRequiredError();
        return this.reports.delete(report.id);
    }

    /**
     * Rejects a title already used in the scope a document competes in: public documents
     * compete with every public document, private ones also with their owner's private ones.
     * The demo has one user, so a private title competes with every document of the family.
     */
    assertTitleAvailable(reportName, title, isGlobal, exceptId) {
        const taken = [...this.reports.values()].some(report =>
            report.id !== exceptId
            && sameName(report.reportName, reportName)
            && report.title.localeCompare(title, undefined, { sensitivity: "base" }) === 0
            && (isGlobal ? report.isGlobal : true));
        if (taken) throw new SavedReportTitleConflictError(title);
    }
}
