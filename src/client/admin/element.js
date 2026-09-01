// Protocol contract: the <interactive-report-admin> element: a thin shell around an embedded
// <interactive-report> pointed at the built-in "__saved-reports" listing. The listing is itself a
// report: search, sort, pagination, column tools, and CSV export all come from the report
// widget; this wrapper contributes only what a report cannot: the admin actions its ir-action
// events request (publish/unpublish, reassign, view state, download, delete), the Upload JSON…
// dialog, and the identity line. The server enforces the authorization matrix; the embedded
// report simply has no data (404) for non-administrators.

import { api, apiUrl, downloadFile, saveBlob } from "../core/api.js";
import { el, banner, labeled, sel } from "../core/dom.js";
import { openDialog, confirmDialog } from "../core/dialog.js";
import { loadWhoami } from "../core/identity.js";
import { WidgetElement } from "../core/widget.js";

const LISTING_REPORT = "__saved-reports";

export class InteractiveReportAdminElement extends WidgetElement {
    static observedAttributes = ["api-base", "base", "lang"];

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

        let availableReports;
        try {
            const families = await api(this.base);
            const reportsByFamily = await Promise.all(families.map(family =>
                api(apiUrl(this.base, family.name))));
            availableReports = reportsByFamily.flat();
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
        this._mount.replaceChildren(
            el("div", { class: "ir-toolbar ir-admin-bar", part: "toolbar" },
                el("button", { type: "button", class: "ir-btn", onclick: () => this.refresh() }, this.t("admin.refresh")),
                el("button", { type: "button", class: "ir-btn", onclick: () => this.uploadDocument() }, this.t("admin.uploadJson")),
                el("button", {
                    type: "button", class: "ir-btn",
                    onclick: () => { void this.authorizationDialog().catch(err => this.showError(err)); },
                }, this.t("admin.authorization")),
                el("span", { class: "ir-spacer" }),
                this.els.identity),
            el("div", { class: "ir-notices", part: "notices" }, this.els.errorSlot, this.els.transientSlot),
            report);

        const identity = await loadWhoami(this.base);
        if (seq !== this._seq || !this.isConnected) return;
        this.whoami = identity.whoami;
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
        } else if (this.whoami.administratorListConfigured && !this.whoami.isAdministrator) {
            this.els.errorSlot.replaceChildren(banner(
                "error", this.t("admin.accessRequired"), null, this));
        } else if (!this.whoami.isAdministrator
            && !this.whoami.applicationAuthorizationConfigured) {
            this.els.errorSlot.replaceChildren(banner(
                "error", this.t("admin.accessConfigurationRequired"), null, this));
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
        await api(apiUrl(this.base, id), { method: "PUT", body: { isDefault: true } });
        this.notify(this.t("admin.nowDefault", {
            title: row.TITLE,
        }));
        this.refresh();
    }

    /**
     * Opens an owner picker and reassigns a saved report to the selected user.
     *
     * @param {string} id - The saved-report identifier to reassign.
     * @param {object} row - Listing row containing the current owner and report labels.
     * @returns {Promise<void>} Resolves after owner choices load and the reassignment dialog opens.
     *
     * Side effects: fetches the user directory and opens a dialog whose apply handler updates the owner, notifies the user, and refreshes the listing.
     * @throws {Error} When loading the user directory fails for a reason other than an unsupported endpoint.
     */
    async reassign(id, row) {
        const users = await this.loadUserDirectory();

        let ownerInp;
        let note;
        if (users.length) {
            const current = users.find(user =>
                String(user.value).toLocaleLowerCase() === String(row.OWNER ?? "").toLocaleLowerCase());
            const options = users.map(user => ({ label: user.display, value: user.value }));
            if (!current) options.unshift({ label: this.t("admin.selectUser"), value: "" });
            ownerInp = sel(options, current?.value ?? "");
            ownerInp.required = true;
            note = this.t("admin.directoryOwnerNote");
        } else {
            ownerInp = el("input", {
                class: "ir-input", type: "text", value: row.OWNER ?? "", required: true,
            });
            note = this.t("admin.identityOwnerNote");
        }

        openDialog({
            owner: this,
            title: this.t("admin.reassignOwner"),
            width: "26rem",
            applyLabel: this.t("admin.reassign"),
            build: body => body.append(
                el("p", { class: "ir-confirm-text" }, `"${row.TITLE}" (${row.REPORT_NAME})`),
                labeled(this.t("admin.newOwner"), ownerInp),
                el("p", { class: "ir-dialog-note" }, note)),
            onApply: async () => {
                const owner = ownerInp.value.trim();
                if (!owner) throw new Error(this.t("admin.enterIdentity"));
                await api(apiUrl(this.base, id), { method: "PUT", body: { owner } });
                this.notify(this.t("admin.reassigned", { title: row.TITLE, owner }));
                this.refresh();
            },
        });
    }

    /**
     * Loads and caches the users eligible to own saved reports.
     *
     * @returns {Promise<Array<object>>} Directory entries, or an empty array when the provider route is unavailable or returns a non-array value.
     *
     * Side effects: may perform network I/O.
     */
    async loadUserDirectory() {
        try {
            const supplied = await api(apiUrl(this.base, "admin", "users"));
            return Array.isArray(supplied) ? supplied : [];
        } catch (err) {
            // Provider constraint: a separately hosted older API has no lookup route. Its
            // behavior is the same as an application that did not register a provider.
            if (err?.status === 404) return [];
            throw err;
        }
    }

    /**
     * Loads authorization and directory data, then opens the administrator and report-access editor.
     *
     * @returns {Promise<void>} Resolves after authorization data and users load and the editor opens.
     *
     * Side effects: fetches authorization data and users, then opens a dialog whose controls perform and reload authorization mutations.
     */
    async authorizationDialog() {
        let [authorization, users] = await Promise.all([
            api(apiUrl(this.base, "admin", "authorization")),
            this.loadUserDirectory(),
        ]);
        let selectedReport = authorization.reports?.[0]?.name ?? null;
        const directory = new Map(users.map(user => [
            String(user.value).toLocaleLowerCase(), user.display,
        ]));
        const displayIdentity = value => {
            const display = directory.get(String(value).toLocaleLowerCase());
            return display && display !== value ? `${display} (${value})` : value;
        };

        openDialog({
            owner: this,
            title: this.t("admin.authorizationTitle"),
            width: "48rem",
            build: (body, dlg) => {
                const reload = async () => {
                    authorization = await api(apiUrl(this.base, "admin", "authorization"));
                    if (!authorization.reports?.some(report => report.name === selectedReport))
                        selectedReport = authorization.reports?.[0]?.name ?? null;
                    render();
                };
                const mutate = async operation => {
                    dlg.setError(null);
                    try {
                        await operation();
                        await reload();
                    } catch (err) {
                        dlg.setError(err);
                    }
                };
                const identityControl = () => {
                    if (users.length) {
                        const control = sel([
                            { label: this.t("admin.selectUser"), value: "" },
                            ...users.map(user => ({ label: user.display, value: user.value })),
                        ], "");
                        control.required = true;
                        return control;
                    }
                    return el("input", {
                        class: "ir-input", type: "text", required: true,
                        placeholder: this.t("admin.identityPlaceholder"), autocomplete: "off",
                    });
                };
                const identityRows = (configured, database, remove) => {
                    const rows = [];
                    for (const identity of configured ?? []) {
                        rows.push(el("div", { class: "ir-auth-row" },
                            el("span", {}, displayIdentity(identity)),
                            el("span", { class: "ir-auth-source" }, this.t("admin.sourceConfiguration"))));
                    }
                    for (const identity of database ?? []) {
                        rows.push(el("div", { class: "ir-auth-row" },
                            el("span", {}, displayIdentity(identity)),
                            el("span", { class: "ir-auth-source" }, this.t("admin.sourceCenter")),
                            el("button", {
                                type: "button", class: "ir-btn ir-row-x",
                                "aria-label": this.t("common.removeNamed", { name: identity }),
                                onclick: () => { void mutate(() => remove(identity)); },
                            }, this.t("common.remove"))));
                    }
                    return rows.length
                        ? el("div", { class: "ir-auth-list" }, rows)
                        : el("p", { class: "ir-dialog-note" }, this.t("admin.noExplicitIdentities"));
                };
                const addIdentity = (label, operation) => {
                    const control = identityControl();
                    const button = el("button", {
                        type: "button", class: "ir-btn",
                        onclick: () => {
                            const identity = control.value.trim();
                            if (!identity) { dlg.setError(this.t("admin.selectOrEnterIdentity")); return; }
                            void mutate(() => operation(identity));
                        },
                    }, this.t("common.add"));
                    return el("div", { class: "ir-auth-add" }, labeled(label, control), button);
                };
                const render = () => {
                    const administratorSection = el("section", { class: "ir-auth-section" },
                        el("h3", {}, this.t("admin.administrationAccess")),
                        el("p", { class: "ir-dialog-note" },
                            this.t("admin.administrationAccessNote")),
                        identityRows(
                            authorization.configuredAdministrators,
                            authorization.databaseAdministrators,
                            identity => api(apiUrl(this.base, "admin", "authorization", "administrators"), {
                                method: "DELETE", body: { identity },
                            })),
                        addIdentity(this.t("admin.administrator"), identity => api(
                            apiUrl(this.base, "admin", "authorization", "administrators"),
                            { method: "POST", body: { identity } })));

                    const reports = authorization.reports ?? [];
                    const reportSelect = sel(reports.map(report => ({
                        label: report.title, value: report.name,
                    })), selectedReport);
                    reportSelect.setAttribute("aria-label", this.t("admin.report"));
                    reportSelect.onchange = () => {
                        selectedReport = reportSelect.value;
                        render();
                    };
                    const report = reports.find(item => item.name === selectedReport);
                    let reportBody;
                    if (!report) {
                        reportBody = el("p", { class: "ir-dialog-note" }, this.t("admin.noReports"));
                    } else if (!report.canRestrict) {
                        reportBody = el("p", { class: "ir-dialog-note" },
                            this.t("admin.reportCannotRestrict"));
                    } else {
                        const restricted = el("input", {
                            type: "checkbox",
                            checked: report.restricted,
                            disabled: report.configuredRestricted,
                            onchange: () => { void mutate(() => api(
                                apiUrl(this.base, "admin", "authorization", "reports", report.name),
                                { method: "PUT", body: { restricted: restricted.checked } })); },
                        });
                        reportBody = el("div", { class: "ir-auth-report" },
                            el("label", { class: "ir-checkline" }, restricted,
                                el("span", {}, this.t("admin.restrictReport"))),
                            report.configuredRestricted
                                ? el("p", { class: "ir-dialog-note" },
                                    this.t("admin.configuredRestriction"))
                                : null,
                            el("h4", {}, this.t("admin.grantedUsers")),
                            identityRows(
                                report.configuredUsers,
                                report.databaseUsers,
                                identity => api(apiUrl(
                                    this.base, "admin", "authorization", "reports", report.name, "users"), {
                                    method: "DELETE", body: { identity },
                                })),
                            addIdentity(this.t("admin.reportUser"), identity => api(apiUrl(
                                this.base, "admin", "authorization", "reports", report.name, "users"), {
                                method: "POST", body: { identity },
                            })),
                            !report.restricted
                                ? el("p", { class: "ir-dialog-note" },
                                    this.t("admin.inactiveGrants"))
                                : null);
                    }

                    const reportSection = el("section", { class: "ir-auth-section" },
                        el("h3", {}, this.t("admin.reportAccess")),
                        reports.length ? labeled(this.t("admin.report"), reportSelect) : null,
                        reportBody);
                    body.replaceChildren(administratorSection, reportSection);
                };
                render();
            },
        });
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
        const doc = await api(apiUrl(this.base, row.REPORT_NAME, id));
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
            .filter(report => report.isDefault && report.reportName !== LISTING_REPORT)
            .map(report => ({ value: report.id, label: report.title })));
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
                const reportId = reportInp.value;
                if (!reportId) throw new Error(this.t("admin.enterReportName"));
                const file = fileInp.files?.[0];
                if (!file) throw new Error(this.t("admin.chooseJson"));

                let document;
                try {
                    document = JSON.parse(await file.text());
                } catch {
                    throw new Error(this.t("admin.invalidJson"));
                }

                const imported = await api(apiUrl(this.base, "admin", reportId, "documents"), {
                    method: "POST",
                    body: document,
                });
                this.notify(this.t("admin.uploadedPrivate", { title: imported.title }));
                this.refresh();
            },
        });
    }
}
