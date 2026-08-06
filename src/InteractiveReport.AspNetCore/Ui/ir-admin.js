// <interactive-report-admin></interactive-report-admin>
//
// Saved-report administration: every saved report in the system, with the
// admin-only powers from the authorization matrix — publish/unpublish globals,
// reassign owner, inspect state, delete. The server enforces the matrix; this
// widget simply loses its data (404) for non-administrators.
//
// Attributes:
//   base — API prefix; defaults to the prefix this script was served from

import { api } from "./ir-api.js";
import { el, banner, ensureCss, labeled, openDialog, confirmDialog } from "./ir-ui.js";

const BASE_DEFAULT = new URL("..", import.meta.url).pathname.replace(/\/$/, "");

class InteractiveReportAdminElement extends HTMLElement {
    get base() { return this.getAttribute("base") ?? BASE_DEFAULT; }

    connectedCallback() { this.init(); }

    async init() {
        ensureCss(this.base);
        this.classList.add("ir-root", "ir-admin");
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
            body: el("div", { class: "ir-tablewrap" }),
        };
        this.replaceChildren(
            el("div", { class: "ir-admin-bar" },
                filter,
                el("button", { type: "button", class: "ir-btn", onclick: () => this.reload() }, "Refresh"),
                this.els.count,
                el("span", { class: "ir-spacer" }),
                this.els.identity),
            el("div", { class: "ir-notices" }, this.els.errorSlot, this.els.transientSlot),
            this.els.body);

        this.whoami = await api(`${this.base}/whoami`).catch(() => null);
        if (this.whoami?.identity)
            this.els.identity.textContent = `Signed in as ${this.whoami.identity}`;
        await this.reload();
    }

    async reload() {
        this.els.errorSlot.replaceChildren();
        try {
            this.rows = await api(`${this.base}/admin/saved`);
            this.renderTable();
        } catch (err) {
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

        const trs = rows.map(r => el("tr", { class: "ir-row" },
            el("td", {}, r.reportName),
            el("td", {}, r.title),
            el("td", {}, r.owner),
            el("td", {}, el("span", { class: "ir-badge " + (r.isGlobal ? "ir-badge-global" : "ir-badge-private") },
                r.isGlobal ? "Global" : "Private")),
            el("td", { class: "ir-date" }, formatUtc(r.modifiedUtc)),
            el("td", { class: "ir-actions-cell" },
                linkBtn(r.isGlobal ? "Unpublish" : "Publish", () => this.setGlobal(r, !r.isGlobal)),
                " · ", linkBtn("Reassign…", () => this.reassign(r)),
                " · ", linkBtn("State", () => this.viewState(r)),
                " · ", linkBtn("Delete…", () => this.remove(r), true))));

        if (!trs.length)
            trs.push(el("tr", { class: "ir-empty" }, el("td", { colSpan: 6 }, "No saved reports.")));

        this.els.body.replaceChildren(el("table", { class: "ir-table" },
            el("thead", {}, el("tr", {},
                ...["Report", "Title", "Owner", "Scope", "Modified", ""].map(h => el("th", { scope: "col" }, h)))),
            el("tbody", {}, ...trs)));
    }

    async setGlobal(r, isGlobal) {
        try {
            await api(`${this.base}/saved/${encodeURIComponent(r.id)}`, { method: "PUT", body: { isGlobal } });
            this.notify(isGlobal ? `"${r.title}" is now global.` : `"${r.title}" is now private to ${r.owner}.`);
            await this.reload();
        } catch (err) { this.fail(err); }
    }

    reassign(r) {
        const ownerInp = el("input", { class: "ir-input", type: "text", value: r.owner });
        openDialog({
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
                await api(`${this.base}/saved/${encodeURIComponent(r.id)}`, { method: "PUT", body: { owner } });
                this.notify(`"${r.title}" reassigned to ${owner}.`);
                await this.reload();
            },
        });
    }

    async viewState(r) {
        try {
            const doc = await api(`${this.base}/saved/${encodeURIComponent(r.id)}`);
            openDialog({
                title: `${r.title} — state document`,
                width: "36rem",
                build: body => body.append(
                    el("pre", { class: "ir-state-pre" }, JSON.stringify(doc.state, null, 2))),
            });
        } catch (err) { this.fail(err); }
    }

    async remove(r) {
        const scope = r.isGlobal ? "the GLOBAL report" : `${r.owner}'s report`;
        if (!await confirmDialog("Delete Saved Report", `Delete ${scope} "${r.title}"? This cannot be undone.`)) return;
        try {
            await api(`${this.base}/saved/${encodeURIComponent(r.id)}`, { method: "DELETE" });
            this.notify(`"${r.title}" deleted.`);
            await this.reload();
        } catch (err) { this.fail(err); }
    }
}

function formatUtc(iso) {
    const date = new Date(iso);
    return Number.isNaN(date.valueOf()) ? (iso ?? "") : date.toLocaleString();
}

if (!customElements.get("interactive-report-admin"))
    customElements.define("interactive-report-admin", InteractiveReportAdminElement);
