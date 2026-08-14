// The <interactive-report-admin> element: a thin shell around an embedded
// <interactive-report> pointed at the built-in "__saved-reports" listing. The
// listing IS a report — search, sort, pagination, column tools, and CSV export
// all come from the report widget; this wrapper contributes only what a report
// cannot: the admin actions its ir-action events request (publish/unpublish,
// reassign, view state, download, delete), the Upload JSON… dialog, and the
// identity line. The server enforces the authorization matrix; the embedded
// report simply has no data (404) for non-administrators.

import { api, apiUrl, downloadFile, saveBlob } from "../core/api.js";
import { el, banner, labeled, transientBanner } from "../core/dom.js";
import { openDialog, confirmDialog } from "../core/dialog.js";
import { WidgetElement } from "../core/widget.js";

const LISTING_REPORT = "__saved-reports";

export class InteractiveReportAdminElement extends WidgetElement {
    static observedAttributes = ["api-base", "base"];

    connectedCallback() { this._connected = true; this.init(); }
    disconnectedCallback() {
        this._connected = false;
        super.disconnectedCallback();
    }
    attributeChangedCallback(_name, oldValue, newValue) {
        if (this._connected && oldValue !== newValue) this.init();
    }

    async init() {
        const seq = ++this._seq;
        this.whoami = null;

        const report = el("interactive-report", {
            report: LISTING_REPORT,
            "api-base": this.apiBase,
        });
        report.addEventListener("ir-action", event => { void this.onAction(event.detail); });
        this.els = {
            report,
            identity: el("span", { class: "ir-admin-count" }),
            errorSlot: el("div", { role: "alert", "aria-atomic": "true" }),
            transientSlot: el("div", { role: "status", "aria-live": "polite", "aria-atomic": "true" }),
        };
        this._mount.replaceChildren(
            el("div", { class: "ir-toolbar ir-admin-bar", part: "toolbar" },
                el("button", { type: "button", class: "ir-btn", onclick: () => this.refresh() }, "Refresh"),
                el("button", { type: "button", class: "ir-btn", onclick: () => this.uploadDocument() }, "Upload JSON…"),
                el("span", { class: "ir-spacer" }),
                this.els.identity),
            el("div", { class: "ir-notices", part: "notices" }, this.els.errorSlot, this.els.transientSlot),
            report);

        this.whoami = await api(`${this.base}/whoami`).catch(() => null);
        if (seq !== this._seq || !this.isConnected) return;
        if (this.whoami?.identity)
            this.els.identity.textContent = `Signed in as ${this.whoami.identity}`;
        // The embedded report answers 404 for non-administrators; when whoami is
        // available, replace that generic denial with precise guidance.
        if (this.whoami && !this.whoami.isAdministrator) {
            this.els.errorSlot.replaceChildren(banner("error",
                "Administrator access required. Add your identity to InteractiveReport:Administrators."));
        }
    }

    /// Refresh the embedded listing in place — after admin mutations and for the
    /// toolbar button. runQuery is the report element's public query surface.
    refresh() {
        this.els.report.runQuery?.().catch(() => {});
    }

    notify(text) {
        transientBanner(this.els.transientSlot, "ok", text);
    }

    fail(err) {
        this.els.errorSlot.replaceChildren(
            banner("error", err.message, () => this.els.errorSlot.replaceChildren()));
    }

    /// The listing's action cells dispatch { command, row }: the row carries the
    /// hidden ID key plus the displayed columns (TITLE, OWNER, SCOPE, …) the
    /// dialogs and confirmations need.
    async onAction({ command, row }) {
        const id = row?.ID;
        if (!id) return;
        try {
            switch (command) {
                case "toggleGlobal": await this.toggleGlobal(id, row); break;
                case "togglePrimary": await this.togglePrimary(id, row); break;
                case "reassign": this.reassign(id, row); break;
                case "openState": await this.viewState(id, row); break;
                case "download": await this.downloadDocument(id, row); break;
                case "delete": await this.remove(id, row); break;
            }
        } catch (err) {
            this.fail(err);
        }
    }

    async toggleGlobal(id, row) {
        const makeGlobal = row.SCOPE !== "Global";
        await api(apiUrl(this.base, "saved", id), { method: "PUT", body: { isGlobal: makeGlobal } });
        this.notify(makeGlobal
            ? `"${row.TITLE}" is now global.`
            : `"${row.TITLE}" is now private to ${row.OWNER}.`);
        this.refresh();
    }

    async togglePrimary(id, row) {
        const makePrimary = row.PRIMARY_STATUS !== "Yes";
        await api(apiUrl(this.base, "saved", id), { method: "PUT", body: { isPrimary: makePrimary } });
        this.notify(makePrimary
            ? `"${row.TITLE}" is now primary.`
            : `"${row.TITLE}" is no longer primary.`);
        this.refresh();
    }

    reassign(id, row) {
        const ownerInp = el("input", { class: "ir-input", type: "text", value: row.OWNER ?? "", required: true });
        openDialog({
            owner: this,
            title: "Reassign Owner",
            width: "26rem",
            applyLabel: "Reassign",
            build: body => body.append(
                el("p", { class: "ir-confirm-text" }, `"${row.TITLE}" (${row.REPORT_NAME})`),
                labeled("New owner (identity value)", ownerInp),
                el("p", { class: "ir-dialog-note" }, "The exact identity value — what GET …/whoami reports for that user.")),
            onApply: async () => {
                const owner = ownerInp.value.trim();
                if (!owner) throw new Error("Enter an identity value");
                await api(apiUrl(this.base, "saved", id), { method: "PUT", body: { owner } });
                this.notify(`"${row.TITLE}" reassigned to ${owner}.`);
                this.refresh();
            },
        });
    }

    async viewState(id, row) {
        const doc = await api(apiUrl(this.base, "saved", id));
        openDialog({
            owner: this,
            title: `${row.TITLE} — state document`,
            width: "36rem",
            build: body => body.append(
                el("pre", { class: "ir-state-pre" }, JSON.stringify(doc.state, null, 2))),
        });
    }

    async downloadDocument(id, row) {
        const file = await downloadFile(apiUrl(this.base, "admin", "saved", id, "document"));
        saveBlob(file.blob, file.filename ?? `${row.REPORT_NAME}.report.json`);
    }

    async remove(id, row) {
        const scope = row.SCOPE === "Global" ? "the GLOBAL report" : `${row.OWNER}'s report`;
        if (!await confirmDialog(this, "Delete Saved Report", `Delete ${scope} "${row.TITLE}"? This cannot be undone.`)) return;
        await api(apiUrl(this.base, "saved", id), { method: "DELETE" });
        this.notify(`"${row.TITLE}" deleted.`);
        this.refresh();
    }

    uploadDocument() {
        const reportInp = el("input", {
            class: "ir-input", type: "text", placeholder: "Configured report name",
            autocomplete: "off", required: true,
        });
        const fileInp = el("input", {
            class: "ir-input", type: "file", accept: ".json,application/json", required: true,
        });
        openDialog({
            owner: this,
            title: "Upload Report Document",
            width: "30rem",
            applyLabel: "Upload",
            build: body => body.append(
                labeled("Report name", reportInp),
                labeled("Report document JSON", fileInp),
                el("p", { class: "ir-dialog-note" },
                    "The title and state are imported after schema validation. " +
                    "A primary flag publishes the report; a primary report named Default replaces the generated Default.")),
            onApply: async () => {
                const reportName = reportInp.value.trim();
                if (!reportName) throw new Error("Enter the configured report name.");
                const file = fileInp.files?.[0];
                if (!file) throw new Error("Choose a report document JSON file.");

                let document;
                try {
                    document = JSON.parse(await file.text());
                } catch {
                    throw new Error("The selected file is not valid JSON.");
                }

                const imported = await api(apiUrl(this.base, "admin", reportName, "documents"), {
                    method: "POST",
                    body: document,
                });
                this.notify(imported.isPrimary
                    ? `"${imported.title}" uploaded as a primary saved report.`
                    : `"${imported.title}" uploaded as a private saved report.`);
                this.refresh();
            },
        });
    }
}
