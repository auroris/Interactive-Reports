// Protocol contract: the <interactive-report-admin> element: a thin shell around an embedded
// <interactive-report> pointed at the built-in "__saved-reports" listing. The listing is itself a
// report: search, sort, pagination, column tools, and CSV export all come from the report
// widget; this wrapper contributes only what a report cannot: the admin actions its ir-action
// events request (publish/unpublish, reassign, view state, download, delete), the Upload JSON…
// dialog, the administrator list editor, and the identity line. The server enforces the
// authorization matrix; the embedded report simply has no data (404) for non-administrators.

import { api, apiUrl, downloadFile, saveBlob } from "../core/api.js";
import { el, banner, labeled, sel } from "../core/dom.js";
import { openDialog, confirmDialog } from "../core/dialog.js";
import { loadWhoami } from "../core/identity.js";
import { WidgetElement } from "../core/widget.js";
import { userPicker } from "./users.js";

const LISTING_REPORT = "__saved-reports";

/**
 * Releases a picker's pending lookup when its dialog closes, whichever control closes it.
 *
 * @param {object} dlg - The dialog controller returned by openDialog.
 * @param {() => object|null} picker - Returns the picker to dispose, or null when none was built.
 * @returns {void} No value.
 *
 * Side effects: wraps the dialog's close method.
 */
function disposePickerOnClose(dlg, picker) {
    const close = dlg.close.bind(dlg);
    dlg.close = () => {
        picker()?.dispose();
        close();
    };
}

export class InteractiveReportAdminElement extends WidgetElement {
    static observedAttributes = ["api-base", "lang"];

    /**
     * Marks the custom element connected and starts administration initialization.
     *
     * @returns {void} No value.
     *
     * Side effects: sets the connection flag, rebuilds the UI, and starts identity loading.
     */
    connectedCallback() { this._connected = true; this.init(); }
    /**
     * Marks the custom element disconnected and releases inherited widget resources.
     *
     * @returns {void} No value.
     *
     * Side effects: clears the connection flag and advances the inherited lifecycle sequence.
     */
    disconnectedCallback() {
        this._connected = false;
        super.disconnectedCallback();
    }
    /**
     * Reinitializes component state when a watched host attribute changes.
     *
     * @param {string} _name - The changed observed attribute name; its identity is not otherwise needed.
     * @param {string|null} oldValue - The attribute's previous serialized value.
     * @param {string|null} newValue - The attribute's new serialized value.
     * @returns {void} No value.
     *
     * Side effects: reinitializes a connected element when the serialized value changes.
     */
    attributeChangedCallback(_name, oldValue, newValue) {
        if (this._connected && oldValue !== newValue) this.init();
    }

    /**
     * Rebuilds the administration shell, embeds the saved-report listing, and resolves identity guidance.
     *
     * @returns {Promise<void>} Resolves after identity guidance is rendered or the initialization is superseded.
     *
     * Side effects: replaces the rendered DOM, registers action listeners, starts the embedded report, fetches identity, and may render access guidance.
     */
    async init() {
        const seq = ++this._seq;
        this.whoami = null;
        this._displayNames ??= new Map();

        let availableReports;
        try {
            availableReports = await api(this.base);
        } catch (error) {
            if (seq !== this._seq || !this.isConnected) return;
            this._mount.replaceChildren(banner("error", error.message, null, this));
            return;
        }
        if (seq !== this._seq || !this.isConnected) return;
        this.availableReports = availableReports;
        const report = el("interactive-report", {
            report: LISTING_REPORT,
            "api-base": this.apiBase,
            lang: this.locale,
        });
        report.addEventListener("ir-action", event => { void this.onAction(event.detail); });
        this.els = {
            report,
            identity: el("span", { class: "ir-admin-count" }),
            errorSlot: el("div", { role: "alert", "aria-atomic": "true" }),
            transientSlot: el("div", { role: "status", "aria-live": "polite", "aria-atomic": "true" }),
        };
        const administratorsButton = el("button", {
            type: "button", class: "ir-btn",
            onclick: () => { void this.administratorsDialog().catch(err => this.showError(err)); },
        }, this.t("admin.administrators"));
        this._mount.replaceChildren(
            el("div", { class: "ir-toolbar ir-admin-bar", part: "toolbar" },
                el("button", { type: "button", class: "ir-btn", onclick: () => this.refresh() }, this.t("admin.refresh")),
                el("button", { type: "button", class: "ir-btn", onclick: () => this.uploadDocument() }, this.t("admin.uploadJson")),
                administratorsButton,
                el("span", { class: "ir-spacer" }),
                this.els.identity),
            el("div", { class: "ir-notices", part: "notices" }, this.els.errorSlot, this.els.transientSlot),
            report);

        const [identity, administrators] = await Promise.all([
            loadWhoami(this.base),
            // Who decides administrators is the server's to say. A refusal here (the caller is
            // not an administrator) simply leaves the button in place; the dialog reports precisely.
            api(apiUrl(this.base, "admin", "administrators")).catch(() => null),
        ]);
        if (seq !== this._seq || !this.isConnected) return;
        this.whoami = identity.whoami;
        administratorsButton.hidden = administrators?.managedByApplication === true;
        if (this.whoami?.identity)
            this.els.identity.textContent = this.t("admin.signedInAs", { identity: this.whoami.identity });
        // The embedded report answers 404 for non-administrators; when whoami is available,
        // replace that generic denial with precise guidance. When it is not (WhoamiEnabled is
        // off by default), say what this page needs instead of leaving a bare listing error.
        if (identity.error) {
            this.showError(identity.error);
        } else if (this.whoami === null) {
            this.els.errorSlot.replaceChildren(banner(
                "warn", this.t("admin.whoamiDisabled"), null, this));
        } else if (!this.whoami.isAdministrator) {
            this.els.errorSlot.replaceChildren(banner(
                "error",
                this.t(this.whoami.administratorsManagedByApplication
                    ? "admin.accessDecidedByApplication"
                    : "admin.accessRequired"),
                null,
                this));
        }
    }

    /**
     * Reloads and rerenders the saved-report administration listing.
     *
     * @returns {void} No value.
     *
     * Side effects: starts a query on the embedded listing; its own renderer handles errors.
     */
    refresh() {
        try {
            const document = this.els.report.getReportDocument();
            this.els.report.submitReportDocument(document).catch(() => {});
        } catch {
            // The embedded report owns its initial load and presents any load failure itself.
        }
    }

    /**
     * The listing's action cells dispatch { command, row }: the row carries the hidden ID key plus the
     * displayed columns (TITLE, OWNER, SCOPE, …) the dialogs and confirmations need.
     *
     * @param {{command: string, row: object}} options - Action command and listing row emitted by the embedded report.
     * @returns {Promise<void>} Resolves after the selected action finishes or its error is displayed.
     *
     * Side effects: dispatches the requested administrative action and may render an error.
     */
    async onAction({ command, row }) {
        const id = row?.ID;
        if (!id) return;
        try {
            switch (command) {
                case "toggleGlobal": await this.toggleGlobal(id, row); break;
                case "makeDefault": await this.makeDefault(id, row); break;
                case "reassign": await this.reassign(id, row); break;
                case "openState": await this.viewState(id, row); break;
                case "download": await this.downloadDocument(id, row); break;
                case "delete": await this.deleteSavedReport(id, row); break;
            }
        } catch (err) {
            this.showError(err);
        }
    }

    /**
     * Updates whether a saved report is visible to all users.
     *
     * @param {string} id - The saved-report identifier to update.
     * @param {object} row - Listing row containing the current scope, title, and owner.
     * @returns {Promise<void>} Resolves after the visibility update and listing refresh are started.
     *
     * Side effects: sends a saved-report update, displays a confirmation notice, and refreshes the listing.
     */
    async toggleGlobal(id, row) {
        const makeGlobal = row.SCOPE !== "Global";
        await api(apiUrl(this.base, id), { method: "PUT", body: { isGlobal: makeGlobal } });
        this.notify(this.t(makeGlobal ? "admin.nowGlobal" : "admin.nowPrivate", {
            title: row.TITLE,
            owner: row.OWNER,
        }));
        this.refresh();
    }

    /**
     * Selects a saved report as its report family's default.
     *
     * @param {string} id - The saved-report identifier to update.
     * @param {object} row - Listing row containing the report title.
     * @returns {Promise<void>} Resolves after the default replacement and listing refresh are started.
     *
     * Side effects: sends a saved-report update, displays a confirmation notice, and refreshes the listing.
     */
    async makeDefault(id, row) {
        // Every user's default for the family changes and the previous default cannot simply
        // be restored, so a single stray click must not be enough.
        if (!await confirmDialog(
            this,
            this.t("admin.makeDefaultTitle"),
            this.t("admin.makeDefaultConfirm", { title: row.TITLE }))) return;
        await api(apiUrl(this.base, id), { method: "PUT", body: { isDefault: true } });
        this.notify(this.t("admin.nowDefault", {
            title: row.TITLE,
        }));
        this.refresh();
    }

    /**
     * Opens a searchable owner picker and reassigns a saved report to the chosen or typed identity.
     *
     * @param {string} id - The saved-report identifier to reassign.
     * @param {object} row - Listing row containing the current owner and report labels.
     * @returns {Promise<void>} Resolves once the reassignment dialog is open.
     *
     * Side effects: opens a dialog whose picker looks up accounts and whose apply handler updates the owner, notifies the user, and refreshes the listing.
     */
    async reassign(id, row) {
        let picker = null;
        const dlg = openDialog({
            owner: this,
            title: this.t("admin.reassignOwner"),
            width: "28rem",
            applyLabel: this.t("admin.reassign"),
            build: (body, dialog) => {
                const currentOwner = el("p", { class: "ir-dialog-note" });
                const describeOwner = () => {
                    currentOwner.textContent = this.t("admin.currentOwner", {
                        owner: this.displayIdentity(row.OWNER ?? ""),
                    });
                };
                describeOwner();
                picker = userPicker(this, {
                    // Picking fills the identity field; Reassign then applies whatever it holds,
                    // so a typed value that the directory does not list works the same way.
                    onPick: user => { picker.value = user.value; dialog.setError(null); },
                    onError: error => dialog.setError(error),
                    onResults: items => { this.rememberDisplayNames(items); describeOwner(); },
                });
                body.append(
                    el("p", { class: "ir-confirm-text" }, `"${row.TITLE}" (${row.REPORT_NAME})`),
                    currentOwner,
                    labeled(this.t("admin.newOwner"), picker.input),
                    picker.results,
                    el("p", { class: "ir-dialog-note" }, this.t("admin.ownerNote")));
            },
            onApply: async () => {
                const owner = picker.value;
                if (!owner) throw new Error(this.t("admin.enterIdentity"));
                await api(apiUrl(this.base, id), { method: "PUT", body: { owner } });
                this.notify(this.t("admin.reassigned", { title: row.TITLE, owner }));
                this.refresh();
            },
        });
        disposePickerOnClose(dlg, () => picker);
    }

    /**
     * Records the display names a lookup returned so grant lists can show them beside identity values.
     *
     * @param {Array<{display: string, value: string}>} items - One lookup's account choices.
     * @returns {void} No value.
     */
    rememberDisplayNames(items) {
        for (const user of items ?? []) {
            if (user?.display && user.value && user.display !== user.value)
                this._displayNames.set(String(user.value), String(user.display));
        }
    }

    /**
     * Formats an identity value with its display name when a lookup has supplied one.
     *
     * @param {string} value - The canonical identity value.
     * @returns {string} `Display (value)`, or the bare value.
     */
    displayIdentity(value) {
        const display = this._displayNames.get(String(value));
        return display ? `${display} (${value})` : String(value);
    }

    /**
     * Renders configured (read-only) and database (removable) identity rows.
     *
     * @param {Array<string>} configured - Identities from appsettings.json.
     * @param {Array<string>} database - Identities the administration center may remove.
     * @param {(identity: string) => void} remove - Invoked with a database identity to remove.
     * @returns {HTMLElement} The list, or a note when neither source has entries.
     */
    identityRows(configured, database, remove) {
        const rows = [];
        for (const identity of configured ?? []) {
            rows.push(el("div", { class: "ir-auth-row" },
                el("span", {}, this.displayIdentity(identity)),
                el("span", { class: "ir-auth-source" }, this.t("admin.sourceConfiguration"))));
        }
        for (const identity of database ?? []) {
            rows.push(el("div", { class: "ir-auth-row" },
                el("span", {}, this.displayIdentity(identity)),
                el("span", { class: "ir-auth-source" }, this.t("admin.sourceCenter")),
                el("button", {
                    type: "button", class: "ir-btn ir-row-x",
                    "aria-label": this.t("common.removeNamed", { name: identity }),
                    onclick: () => remove(identity),
                }, this.t("common.remove"))));
        }
        return rows.length
            ? el("div", { class: "ir-auth-list" }, rows)
            : el("p", { class: "ir-dialog-note" }, this.t("admin.noExplicitIdentities"));
    }

    /**
     * Loads the administrator lists and opens the editor that sets the database list as a whole.
     *
     * @returns {Promise<void>} Resolves once the editor is open.
     *
     * Side effects: fetches the administrator lists and opens a dialog whose Save replaces the database list in one request.
     */
    async administratorsDialog() {
        const current = await api(apiUrl(this.base, "admin", "administrators"));
        if (current?.managedByApplication) {
            this.notify(this.t("admin.accessDecidedByApplication"), "warn");
            return;
        }
        const configured = current?.configured ?? [];
        const saved = current?.database ?? [];
        const pending = [...saved];
        let picker = null;
        const dlg = openDialog({
            owner: this,
            title: this.t("admin.administratorsTitle"),
            width: "34rem",
            applyLabel: this.t("admin.saveList"),
            build: (body, dialog) => {
                const listSlot = el("div");
                const render = () => listSlot.replaceChildren(this.identityRows(configured, pending, identity => {
                    pending.splice(pending.indexOf(identity), 1);
                    render();
                }));
                const add = identity => {
                    const value = String(identity ?? "").trim();
                    if (!value) { dialog.setError(this.t("admin.enterIdentity")); return; }
                    dialog.setError(null);
                    if (!pending.includes(value) && !configured.includes(value)) pending.push(value);
                    picker.value = "";
                    render();
                };
                picker = userPicker(this, {
                    onPick: user => add(user.value),
                    onError: error => dialog.setError(error),
                    onResults: items => { this.rememberDisplayNames(items); render(); },
                });
                body.append(
                    el("p", { class: "ir-dialog-note" }, this.t("admin.administratorsNote")),
                    listSlot,
                    el("div", { class: "ir-auth-add" },
                        labeled(this.t("admin.addAdministrator"), picker.input),
                        el("button", {
                            type: "button", class: "ir-btn",
                            onclick: () => add(picker.value),
                        }, this.t("common.add"))),
                    picker.results,
                    el("p", { class: "ir-dialog-note" }, this.t("admin.identityNote")));
                render();
            },
            onApply: async () => {
                // Saving a list that drops the caller's own database grant locks them out unless
                // configuration still names them; ask first when the identity is known.
                const me = this.whoami?.identity;
                if (me && saved.includes(me) && !pending.includes(me) && !configured.includes(me)
                    && !await confirmDialog(
                        this,
                        this.t("admin.removeSelfTitle"),
                        this.t("admin.removeSelfConfirm"),
                        this.t("common.remove"))) return false;
                await api(apiUrl(this.base, "admin", "administrators"), {
                    method: "PUT",
                    body: { identities: pending },
                });
                this.notify(this.t("admin.administratorsSaved"));
            },
        });
        disposePickerOnClose(dlg, () => picker);
    }

    /**
     * Loads a saved report and opens its raw document in a read-only dialog.
     *
     * @param {string} id - The saved-report identifier to load.
     * @param {object} row - Listing row supplying the saved-report title.
     * @returns {Promise<void>} Resolves after the document is loaded and its dialog opens.
     *
     * Side effects: fetches the saved report and opens a read-only JSON dialog.
     */
    async viewState(id, row) {
        const doc = await api(apiUrl(this.base, "admin", "saved", id, "document"));
        openDialog({
            owner: this,
            title: this.t("admin.stateDocumentTitle", { title: row.TITLE }),
            width: "36rem",
            build: body => body.append(
                el("pre", { class: "ir-state-pre" }, JSON.stringify(doc.state, null, 2))),
        });
    }

    /**
     * Downloads the supplied saved report as a JSON document.
     *
     * @param {string} id - The saved-report identifier to download.
     * @param {object} row - Listing row supplying the fallback report name.
     * @returns {Promise<void>} Resolves after the file is fetched and handed to the browser.
     *
     * Side effects: performs a network request and initiates a browser download.
     */
    async downloadDocument(id, row) {
        const file = await downloadFile(apiUrl(this.base, "admin", "saved", id, "document"));
        saveBlob(file.blob, file.filename ?? `${row.REPORT_NAME}.report.json`);
    }

    /**
     * Confirms and deletes the supplied saved report without shadowing the host element's remove
     * method.
     *
     * @param {string} id - The saved-report identifier to delete.
     * @param {object} row - Listing row supplying scope, owner, and title for confirmation text.
     * @returns {Promise<void>} Resolves after cancellation or after deletion and refresh.
     *
     * Side effects: opens a confirmation dialog and, when confirmed, deletes the saved report, displays a notice, and refreshes the listing.
     */
    async deleteSavedReport(id, row) {
        const scope = row.SCOPE === "Global"
            ? this.t("admin.globalReport")
            : this.t("admin.ownerReport", { owner: row.OWNER });
        if (!await confirmDialog(
            this,
            this.t("saved.deleteTitle"),
            this.t("admin.deleteConfirm", { scope, title: row.TITLE }))) return;
        await api(apiUrl(this.base, id), { method: "DELETE" });
        this.notify(this.t("admin.deleted", { title: row.TITLE }));
        this.refresh();
    }

    /**
     * Prompts for a JSON document and imports it as a saved report.
     *
     * @returns {void} No value.
     *
     * Side effects: opens an upload dialog whose apply handler reads and parses the selected file, imports it, displays a notice, and refreshes the listing.
     */
    uploadDocument() {
        const reportInp = sel((this.availableReports ?? [])
            .filter(report => report.name !== LISTING_REPORT)
            .map(report => ({ value: report.name, label: report.title })));
        reportInp.required = true;
        const fileInp = el("input", {
            class: "ir-input", type: "file", accept: ".json,application/json", required: true,
        });
        openDialog({
            owner: this,
            title: this.t("admin.uploadTitle"),
            width: "30rem",
            applyLabel: this.t("admin.upload"),
            build: body => body.append(
                labeled(this.t("admin.reportName"), reportInp),
                labeled(this.t("admin.reportDocumentJson"), fileInp),
                el("p", { class: "ir-dialog-note" },
                    this.t("admin.uploadNote"))),
            onApply: async () => {
                const reportName = reportInp.value;
                if (!reportName) throw new Error(this.t("admin.enterReportName"));
                const file = fileInp.files?.[0];
                if (!file) throw new Error(this.t("admin.chooseJson"));

                let document;
                try {
                    document = JSON.parse(await file.text());
                } catch {
                    throw new Error(this.t("admin.invalidJson"));
                }

                const imported = await api(apiUrl(this.base, "admin", reportName, "documents"), {
                    method: "POST",
                    body: document,
                });
                this.notify(this.t("admin.uploadedPrivate", { title: imported.title }));
                this.refresh();
            },
        });
    }
}
