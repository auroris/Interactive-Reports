// The <interactive-report-admin> element: a thin shell around an embedded
// <interactive-report> pointed at the built-in "__saved-reports" listing. The
// listing IS a report — search, sort, pagination, column tools, and CSV export
// all come from the report widget; this wrapper contributes only what a report
// cannot: the admin actions its ir-action events request (publish/unpublish,
// reassign, view state, download, delete), the Upload JSON… dialog, and the
// identity line. The server enforces the authorization matrix; the embedded
// report simply has no data (404) for non-administrators.

import { api, apiUrl, downloadFile, saveBlob } from "../core/api.js";
import { el, banner, labeled, sel } from "../core/dom.js";
import { openDialog, confirmDialog } from "../core/dialog.js";
import { loadWhoami } from "../core/identity.js";
import { WidgetElement } from "../core/widget.js";

const LISTING_REPORT = "__saved-reports";

export class InteractiveReportAdminElement extends WidgetElement {
    static observedAttributes = ["api-base", "base", "lang"];

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
        // The embedded report answers 404 for non-administrators; when whoami is
        // available, replace that generic denial with precise guidance. When it is
        // not (WhoamiEnabled is off — the default), say what this page needs instead
        // of leaving a bare listing error.
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

    /// Refresh the embedded listing in place — after admin mutations and for the
    /// toolbar button. runQuery is the report element's public query surface.
    refresh() {
        this.els.report.runQuery?.().catch(() => {});
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
                case "reassign": await this.reassign(id, row); break;
                case "openState": await this.viewState(id, row); break;
                case "download": await this.downloadDocument(id, row); break;
                case "delete": await this.deleteSavedReport(id, row); break;
            }
        } catch (err) {
            this.showError(err);
        }
    }

    async toggleGlobal(id, row) {
        const makeGlobal = row.SCOPE !== "Global";
        await api(apiUrl(this.base, "saved", id), { method: "PUT", body: { isGlobal: makeGlobal } });
        this.notify(this.t(makeGlobal ? "admin.nowGlobal" : "admin.nowPrivate", {
            title: row.TITLE,
            owner: row.OWNER,
        }));
        this.refresh();
    }

    async togglePrimary(id, row) {
        const makePrimary = row.PRIMARY_STATUS !== "Yes";
        await api(apiUrl(this.base, "saved", id), { method: "PUT", body: { isPrimary: makePrimary } });
        this.notify(this.t(makePrimary ? "admin.nowPrimary" : "admin.noLongerPrimary", {
            title: row.TITLE,
        }));
        this.refresh();
    }

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
                await api(apiUrl(this.base, "saved", id), { method: "PUT", body: { owner } });
                this.notify(this.t("admin.reassigned", { title: row.TITLE, owner }));
                this.refresh();
            },
        });
    }

    async loadUserDirectory() {
        try {
            const supplied = await api(apiUrl(this.base, "admin", "users"));
            return Array.isArray(supplied) ? supplied : [];
        } catch (err) {
            // A separately hosted older API has no lookup route. Its behavior is the
            // same as an application that did not register a provider.
            if (err?.status === 404) return [];
            throw err;
        }
    }

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

    async viewState(id, row) {
        const doc = await api(apiUrl(this.base, "saved", id));
        openDialog({
            owner: this,
            title: this.t("admin.stateDocumentTitle", { title: row.TITLE }),
            width: "36rem",
            build: body => body.append(
                el("pre", { class: "ir-state-pre" }, JSON.stringify(doc.state, null, 2))),
        });
    }

    async downloadDocument(id, row) {
        const file = await downloadFile(apiUrl(this.base, "admin", "saved", id, "document"));
        saveBlob(file.blob, file.filename ?? `${row.REPORT_NAME}.report.json`);
    }

    // Not named `remove`: that would shadow Element.remove() and turn a host's
    // ordinary element removal into a phantom delete action (found by a test that
    // unmounted the element).
    async deleteSavedReport(id, row) {
        const scope = row.SCOPE === "Global"
            ? this.t("admin.globalReport")
            : this.t("admin.ownerReport", { owner: row.OWNER });
        if (!await confirmDialog(
            this,
            this.t("saved.deleteTitle"),
            this.t("admin.deleteConfirm", { scope, title: row.TITLE }))) return;
        await api(apiUrl(this.base, "saved", id), { method: "DELETE" });
        this.notify(this.t("admin.deleted", { title: row.TITLE }));
        this.refresh();
    }

    uploadDocument() {
        const reportInp = el("input", {
            class: "ir-input", type: "text", placeholder: this.t("admin.reportNamePlaceholder"),
            autocomplete: "off", required: true,
        });
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
                const reportName = reportInp.value.trim();
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
                this.notify(this.t(imported.isPrimary
                    ? "admin.uploadedPrimary"
                    : "admin.uploadedPrivate", { title: imported.title }));
                this.refresh();
            },
        });
    }
}
