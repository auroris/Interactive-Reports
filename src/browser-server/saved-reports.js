// Ephemeral in-memory saved-report store for the browser.
// Supports creating, loading, updating, listing, and deleting saved reports.

export class EphemeralSavedReportStore {
    constructor() {
        this.nextId = 2; // ID 1 reserved for default report
        /** @type {Map<number, object>} */
        this.reports = new Map();
        /** @type {Set<string>} */
        this.initializedReports = new Set();
    }

    /**
     * Ensures the default report is present for the given family.
     *
     * @param {string} reportName
     */
    ensureDefault(reportName) {
        if (this.initializedReports.has(reportName.toLowerCase())) return;
        this.initializedReports.add(reportName.toLowerCase());

        const defaultReport = {
            id: 1,
            reportName,
            title: "Default",
            isDefault: true,
            isGlobal: true,
            createdBy: "system",
            state: {},
        };
        this.reports.set(1, defaultReport);
    }

    /**
     * Lists saved reports visible for the specified report definition.
     *
     * @param {string} reportName
     * @returns {Array<object>} Array of saved report summaries
     */
    list(reportName) {
        this.ensureDefault(reportName);
        const summaries = [];
        for (const report of this.reports.values()) {
            if (report.reportName.toLowerCase() === reportName.toLowerCase()) {
                summaries.push({
                    id: report.id,
                    reportName: report.reportName,
                    title: report.title,
                    isDefault: Boolean(report.isDefault),
                    isGlobal: Boolean(report.isGlobal),
                    createdBy: report.createdBy,
                });
            }
        }
        return summaries.sort((a, b) => {
            if (a.isDefault !== b.isDefault) return a.isDefault ? -1 : 1;
            return a.id - b.id;
        });
    }

    /**
     * Saves a new private or global report document.
     *
     * @param {string} reportName
     * @param {object} param1
     * @param {string} param1.title
     * @param {object} param1.state
     * @param {boolean} [param1.isGlobal=false]
     * @returns {object} The created summary
     */
    save(reportName, { title, state, isGlobal = false }) {
        this.ensureDefault(reportName);
        const id = this.nextId++;
        const record = {
            id,
            reportName,
            title: title || `Report ${id}`,
            isDefault: false,
            isGlobal: Boolean(isGlobal),
            createdBy: "demo-user",
            state: state ? JSON.parse(JSON.stringify(state)) : {},
        };
        this.reports.set(id, record);

        return {
            id: record.id,
            reportName: record.reportName,
            title: record.title,
            isDefault: record.isDefault,
            isGlobal: record.isGlobal,
            createdBy: record.createdBy,
        };
    }

    /**
     * Loads a saved report document and summary by report family and ID.
     *
     * @param {string} reportName
     * @param {number|string} id
     * @returns {{ summary: object, state: object }|null}
     */
    load(reportName, id) {
        this.ensureDefault(reportName);
        const numId = Number(id);
        const report = this.reports.get(numId);
        if (!report || report.reportName.toLowerCase() !== reportName.toLowerCase()) {
            return null;
        }

        return {
            summary: {
                id: report.id,
                reportName: report.reportName,
                title: report.title,
                isDefault: Boolean(report.isDefault),
                isGlobal: Boolean(report.isGlobal),
                createdBy: report.createdBy,
            },
            state: JSON.parse(JSON.stringify(report.state)),
        };
    }

    /**
     * Updates an existing saved report's title and/or state document.
     *
     * @param {number|string} id
     * @param {object} param1
     * @param {string} [param1.title]
     * @param {object} [param1.state]
     * @returns {object|null} Updated summary
     */
    update(id, { title, state }) {
        const numId = Number(id);
        const report = this.reports.get(numId);
        if (!report) return null;

        if (title !== undefined && title !== null) {
            report.title = title;
        }
        if (state !== undefined && state !== null) {
            report.state = JSON.parse(JSON.stringify(state));
        }

        return {
            id: report.id,
            reportName: report.reportName,
            title: report.title,
            isDefault: Boolean(report.isDefault),
            isGlobal: Boolean(report.isGlobal),
            createdBy: report.createdBy,
        };
    }

    /**
     * Deletes an author-owned saved report.
     *
     * @param {number|string} id
     * @returns {boolean} True when deleted; false when not found or protected default.
     */
    delete(id) {
        const numId = Number(id);
        const report = this.reports.get(numId);
        if (!report || report.isDefault) {
            return false;
        }
        return this.reports.delete(numId);
    }
}
