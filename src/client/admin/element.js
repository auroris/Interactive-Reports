// The <interactive-report-admin> element: every saved report in the system,
// with the admin-only powers from the authorization matrix — publish/unpublish
// globals, reassign owner, inspect/download state, upload canonical file-backed
// documents for validation and testing, delete. The server enforces the matrix;
// this widget simply loses its data (404) for non-administrators.

import { api, apiUrl, defaultApiBase, downloadFile, saveBlob } from "../core/api.js";
import { el, banner, labeled } from "../core/dom.js";
import { openDialog, confirmDialog } from "../core/dialog.js";
import { createWidgetRoot, disposeWidget } from "../core/widget.js";

const BASE_DEFAULT = defaultApiBase();

export class InteractiveReportAdminElement extends HTMLElement {
    static observedAttributes = ["api-base", "base"];

    constructor() {
        super();
        const { root, mount } = createWidgetRoot(this);
        this._root = root;
        this._mount = mount;
        this._seq = 0;
    }

    get apiBase() { return this.getAttribute("api-base") ?? this.getAttribute("base") ?? BASE_DEFAULT; }
    set apiBase(value) {
        if (value === null || value === undefined) this.removeAttribute("api-base");
        else this.setAttribute("api-base", String(value));
    }
    get base() { return this.apiBase.replace(/\/+$/, ""); }

    connectedCallback() { this._connected = true; this.init(); }
    disconnectedCallback() {
        this._connected = false;
        ++this._seq;
        disposeWidget(this);
    }
    attributeChangedCallback(_name, oldValue, newValue) {
        if (this._connected && oldValue !== newValue) this.init();
    }

    async init() {
        const seq = ++this._seq;
        this.rows = [];
        this.whoami = null;

        const filter = el("input", {
            class: "ir-input", type: "search", placeholder: "Filter by report, title, owner…",
            oninput: () => this.renderTable(),
        });
        this.els = {
            filter,
            count: el("span", { class: "ir-admin-count" }),
            identity: el("span", { class: "ir-admin-count" }),
            errorSlot: el("div", {}),
            transientSlot: el("div", {}),
            body: el("div", { class: "ir-tablewrap", part: "table-container" }),
        };
        this._mount.replaceChildren(
            el("div", { class: "ir-admin-bar", part: "toolbar" },
                filter,
                el("button", { type: "button", class: "ir-btn", onclick: () => this.reload() }, "Refresh"),
                el("button", { type: "button", class: "ir-btn", onclick: () => this.uploadDocument() }, "Upload JSON…"),
                this.els.count,
                el("span", { class: "ir-spacer" }),
                this.els.identity),
            el("div", { class: "ir-notices", part: "notices" }, this.els.errorSlot, this.els.transientSlot),
            this.els.body);

        this.whoami = await api(`${this.base}/whoami`).catch(() => null);
        if (seq !== this._seq || !this.isConnected) return;
        if (this.whoami?.identity)
            this.els.identity.textContent = `Signed in as ${this.whoami.identity}`;
        await this.reload();
    }

    async reload() {
        const seq = this._seq;
        this.els.errorSlot.replaceChildren();
        try {
            const rows = await api(apiUrl(this.base, "admin", "saved"));
            if (seq !== this._seq || !this.isConnected) return;
            this.rows = rows;
            this.renderTable();
        } catch (err) {
            if (seq !== this._seq || !this.isConnected) return;
            const text = err.status === 401 ? "Sign in to administer saved reports."
                : err.status === 404 ? "Administrator access required. Add your identity to InteractiveReport:Administrators."
                : err.message;
            this.els.body.replaceChildren();
            this.els.errorSlot.replaceChildren(banner("error", text));
        }
    }

    notify(text) {
        const node = banner("ok", text);
        this.els.transientSlot.append(node);
        setTimeout(() => node.remove(), 4000);
    }

    fail(err) {
        this.els.errorSlot.replaceChildren(
            banner("error", err.message, () => this.els.errorSlot.replaceChildren()));
    }

    filtered() {
        const q = this.els.filter.value.trim().toLowerCase();
        if (!q) return this.rows;
        return this.rows.filter(r =>
            [r.reportName, r.title, r.owner].some(s => (s ?? "").toLowerCase().includes(q)));
    }

    renderTable() {
        const rows = this.filtered();
        this.els.count.textContent =
            rows.length === this.rows.length ? `${this.rows.length} saved`
                : `${rows.length} of ${this.rows.length} saved`;

        const linkBtn = (label, onclick, danger) => el("button", {
            type: "button", class: "ir-linkbtn" + (danger ? " ir-linkbtn-danger" : ""), onclick,
        }, label);

        const trs = rows.map(r => {
            const actions = r.isReadOnly
                ? [
                    linkBtn("State", () => this.viewState(r)),
                    " · ", linkBtn("Download", () => this.downloadDocument(r)),
                ]
                : [
                    linkBtn(r.isGlobal ? "Unpublish" : "Publish", () => this.setGlobal(r, !r.isGlobal)),
                    " · ", linkBtn("Reassign…", () => this.reassign(r)),
                    " · ", linkBtn("State", () => this.viewState(r)),
                    " · ", linkBtn("Download", () => this.downloadDocument(r)),
                    " · ", linkBtn("Delete…", () => this.remove(r), true),
                ];
            const scope = r.isReadOnly ? "Read only" : r.isGlobal ? "Global" : "Private";
            return el("tr", { class: "ir-row" },
                el("td", {}, r.reportName),
                el("td", {}, r.title),
                el("td", {}, r.owner),
                el("td", {}, el("span", {
                    class: "ir-badge " + (r.isGlobal ? "ir-badge-global" : "ir-badge-private"),
                }, scope)),
                el("td", { class: "ir-date" }, formatUtc(r.modifiedUtc)),
                el("td", { class: "ir-actions-cell" }, ...actions));
        });

        if (!trs.length)
            trs.push(el("tr", { class: "ir-empty" }, el("td", { colSpan: 6 }, "No saved reports.")));

        this.els.body.replaceChildren(el("table", { class: "ir-table", part: "table" },
            el("thead", {}, el("tr", {},
                ...["Report", "Title", "Owner", "Scope", "Modified", ""].map(h => el("th", { scope: "col" }, h)))),
            el("tbody", {}, ...trs)));
    }

    async setGlobal(r, isGlobal) {
        try {
            await api(apiUrl(this.base, "saved", r.id), { method: "PUT", body: { isGlobal } });
            this.notify(isGlobal ? `"${r.title}" is now global.` : `"${r.title}" is now private to ${r.owner}.`);
            await this.reload();
        } catch (err) { this.fail(err); }
    }

    reassign(r) {
        const ownerInp = el("input", { class: "ir-input", type: "text", value: r.owner });
        openDialog({
            owner: this,
            title: "Reassign Owner",
            width: "26rem",
            applyLabel: "Reassign",
            build: body => body.append(
                el("p", { class: "ir-confirm-text" }, `"${r.title}" (${r.reportName})`),
                labeled("New owner (identity value)", ownerInp),
                el("p", { class: "ir-dialog-note" }, "The exact identity value — what GET …/whoami reports for that user.")),
            onApply: async () => {
                const owner = ownerInp.value.trim();
                if (!owner) throw new Error("Enter an identity value");
                await api(apiUrl(this.base, "saved", r.id), { method: "PUT", body: { owner } });
                this.notify(`"${r.title}" reassigned to ${owner}.`);
                await this.reload();
            },
        });
    }

    async viewState(r) {
        try {
            const doc = await api(apiUrl(this.base, "saved", r.id));
            openDialog({
                owner: this,
                title: `${r.title} — state document`,
                width: "36rem",
                build: body => body.append(
                    el("pre", { class: "ir-state-pre" }, JSON.stringify(doc.state, null, 2))),
            });
        } catch (err) { this.fail(err); }
    }

    async downloadDocument(r) {
        try {
            const file = await downloadFile(apiUrl(this.base, "admin", "saved", r.id, "document"));
            saveBlob(file.blob, file.filename ?? `${r.reportName}.report.json`);
        } catch (err) { this.fail(err); }
    }

    uploadDocument() {
        const reportInp = el("input", {
            class: "ir-input", type: "text", placeholder: "Configured report name",
            autocomplete: "off",
        });
        const fileInp = el("input", {
            class: "ir-input", type: "file", accept: ".json,application/json",
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
                    "The title and state are imported as your private saved report after schema validation. " +
                    "The primary flag applies only when the file is configured by the host.")),
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
                this.notify(`"${imported.title}" uploaded as a private saved report.`);
                await this.reload();
            },
        });
    }

    async remove(r) {
        const scope = r.isGlobal ? "the GLOBAL report" : `${r.owner}'s report`;
        if (!await confirmDialog(this, "Delete Saved Report", `Delete ${scope} "${r.title}"? This cannot be undone.`)) return;
        try {
            await api(apiUrl(this.base, "saved", r.id), { method: "DELETE" });
            this.notify(`"${r.title}" deleted.`);
            await this.reload();
        } catch (err) { this.fail(err); }
    }
}

function formatUtc(iso) {
    const date = new Date(iso);
    return Number.isNaN(date.valueOf()) ? (iso ?? "") : date.toLocaleString();
}
