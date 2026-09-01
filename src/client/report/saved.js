// Saved-report controller for selector data, working-copy loads and resets, and create/update/delete
// operations against the saved-report endpoints. The server
// enforces the authorization matrix; canManageCurrentSaved only decides which controls are
// worth offering.

import { api, apiUrl } from "../core/api.js";
import { el } from "../core/dom.js";
import { confirmDialog } from "../core/dialog.js";
import { featureEnabled } from "./schema.js";

/**
 * Compares saved-report titles without case differences while retaining accent distinctions.
 *
 * @param {unknown} left - The first candidate title.
 * @param {unknown} right - The second candidate title.
 * @returns {boolean} Whether the compared values have the same title.
 */
export const sameTitle = (left, right) => typeof left === "string" && typeof right === "string"
    && left.localeCompare(right, undefined, { sensitivity: "accent" }) === 0;

/**
 * Determines whether the current saved report may be overwritten or deleted.
 *
 * @param {object} w - The report controller containing identity and current saved-report metadata.
 * @returns {boolean} Whether the current saved report is writable and either owned or administratively manageable.
 */
export function canManageCurrentSaved(w) {
    return canManageSaved(w, w.currentSaved);
}

/**
 * Computes the client-side hint for whether the current identity may request report administration.
 *
 * @param {object} w - The report controller containing identity and schema authorization hints.
 * @returns {boolean} Whether any explicit or bootstrap administration path may be available; the server remains authoritative.
 */
export function canRequestAdministration(w) {
    return !!w.whoami?.isAdministrator
        || !!w.schema?.authorization?.mayRequestAdministration
        || (!!w.whoami?.authenticated
            && !w.whoami?.administratorListConfigured
            && !!w.whoami?.applicationAuthorizationConfigured);
}

/**
 * Determines whether the current identity may manage the supplied saved report.
 *
 * @param {object} w - The report controller containing current identity hints.
 * @param {object|null} s - The saved-report summary to evaluate.
 * @returns {boolean} Whether the summary is writable and either owned by the caller or eligible for an administration request.
 */
export function canManageSaved(w, s) {
    if (!s) return false;
    return !s.isReadOnly && (canRequestAdministration(w) || s.mine);
}

/**
 * Rebuilds the saved-report selector while preserving the current selection when possible.
 *
 * @param {object} w - The report controller containing cached summaries, current selection, localization, and selector elements.
 * @returns {void} No value.
 *
 * Side effects: replaces selector options, restores the best available selection, and updates wrapper visibility.
 */
export function refreshSavedSelect(w) {
    const { savedSel, savedWrap } = w.els;
    savedSel.replaceChildren();
    const group = (label, items) => {
        const g = el("optgroup", { label });
        for (const s of items) g.append(new Option(s.title, s.id));
        savedSel.append(g);
    };
    const isPublic = saved => saved.isDefault || saved.isGlobal || saved.isPrimary;
    group(w.t("saved.public"), w.savedList.filter(isPublic));
    group(w.t("saved.private"), w.savedList.filter(saved => !isPublic(saved)));
    savedSel.value = w.currentSaved?.id ?? "";
    savedWrap.hidden = !featureEnabled(w, "savedReports") || w.savedList.length === 0;
}

/**
 * Loads visible saved-report summaries when the initiating report context is still current.
 *
 * @param {object} w - The report controller whose report id, request sequence, and summary cache are used.
 * @returns {Promise<void>} Resolves after the cache is updated, the optional feature is marked absent, or a failure notification is shown.
 *
 * Side effects: performs a network request, may replace `savedList`, and may show a warning. It does not rebuild the selector.
 */
export async function loadSavedList(w) {
    const sequence = w._seq;
    const reportId = w.reportId;
    const stillCurrent = () => sequence === w._seq && reportId === w.reportId;
    try {
        const saved = await api(w.documentFamilyUrl("saved"));
        if (stillCurrent()) w.savedList = saved;
    } catch (err) {
        // Protocol contract: a save/delete refresh can finish after the element has switched
        // reports. Its response and errors belong to the old report context.
        if (!stillCurrent()) return;
        // Protocol contract: 404 means the feature is off. A real failure keeps the list we have;
        // wiping it would present a server problem as "no saved reports".
        if (err.status === 404) { w.savedList = []; return; }
        w.notify(w.t("saved.listRefreshFailed", { message: err.message }), "warn");
    }
}

/**
 * Adds or replaces a saved-report summary in the controller's cached list.
 *
 * @param {object} w - The report controller whose `savedList` cache will be updated immutably.
 * @param {object} summary - The saved-report metadata returned by a successful write.
 * @returns {void} No value.
 *
 * Side effects: replaces `w.savedList` with an inserted or updated summary array.
 */
function upsertSavedSummary(w, summary) {
    const index = w.savedList.findIndex(saved => saved.id === summary.id);
    if (index < 0) {
        w.savedList = [...w.savedList, summary];
        return;
    }
    w.savedList = w.savedList.map((saved, i) => i === index ? summary : saved);
}

/**
 * Removes a saved-report summary from the controller's cached list.
 *
 * @param {object} w - The report controller whose summary cache will be filtered.
 * @param {string} id - The saved-report identifier to remove.
 * @returns {void} No value.
 *
 * Side effects: replaces `w.savedList` with a filtered array.
 */
function removeSavedSummary(w, id) {
    w.savedList = w.savedList.filter(saved => saved.id !== id);
}

/**
 * Loads one saved report and adopts its document as the working state.
 *
 * @param {object} w - The report controller that will adopt, execute, or roll back the loaded state.
 * @param {string} id - The visible saved-report identifier to load.
 * @returns {Promise<void>} Resolves after the state query succeeds, a stale response is ignored, or failure recovery completes.
 *
 * Side effects: performs network requests, changes saved selection and working state, runs a query, and may restore state or display an error.
 */
export async function loadSavedById(w, id) {
    const transition = w.beginStateTransition();
    try {
        const docResponse = await api(apiUrl(w.base, id));
        if (!w.isCurrentStateTransition(transition)) return;
        if (docResponse.summary?.reportName !== w.definitionName)
            throw new Error("The selected report document belongs to a different report definition.");
        w.currentSaved = docResponse.summary;
        // Protocol contract: liberal acceptance: the document is adopted as-is and the server
        // judges it on query. A rejection lands in the catch and rolls back.
        w.adoptState(docResponse.state);
        refreshSavedSelect(w);
        const result = await w.runQuery({ quiet: true, source: "saved-report" });
        if (!result && w.isCurrentStateTransition(transition)) w.restoreLastGood();
    } catch (err) {
        if (!w.isCurrentStateTransition(transition)) return;
        // Invariant: nothing validated: put doc, selection, and search back on the last
        // validated state so Save/Delete cannot target the wrong report while the previous grid
        // is still on screen.
        w.restoreLastGood();
        if (err.status === 404) {
            w.showError(new Error(w.t("saved.unavailable")));
            await loadSavedList(w);
            if (w.isCurrentStateTransition(transition)) refreshSavedSelect(w);
        } else {
            w.showError(err);
        }
    }
}

/**
 * Restores the report definition's default document.
 *
 * @param {object} w - The report controller whose default summary will be adopted.
 * @returns {Promise<void>} Resolves after the default query succeeds or the previous validated state is restored.
 *
 * Side effects: changes saved selection and working state, refreshes the selector, runs a query, and may roll back.
 */
export async function resetToPrimary(w) {
    const defaultReport = (w.savedList ?? []).find(report => report.isDefault);
    if (defaultReport) await loadSavedById(w, defaultReport.id);
}

/**
 * Restores the last validated saved or default document as the working copy.
 *
 * @param {object} w - The report controller whose current saved or primary state will be restored.
 * @returns {Promise<void>} Resolves after cancellation or after the chosen state has been loaded and queried.
 *
 * Side effects: opens a confirmation dialog and may perform the load/reset state transition.
 */
export async function resetWorkingCopy(w) {
    const target = w.currentSaved ? `“${w.currentSaved.title}”` : w.t("saved.resetTarget");
    if (!await confirmDialog(
        w,
        w.t("saved.resetTitle"),
        w.t("saved.resetConfirm", { target }),
        w.t("menu.reset"))) return;
    if (w.currentSaved) await loadSavedById(w, w.currentSaved.id);
    else await resetToPrimary(w);
}

/**
 * Creates or updates a saved report and adopts the server-returned summary.
 *
 * @param {object} w - The report controller providing serialized state, identity hints, caches, and notifications.
 * @param {{title: string, isGlobal: boolean, isPrimary: boolean, asNew: boolean, target?: object|null}} options - Publication fields, create/update mode, and optional replacement summary.
 * @returns {Promise<void>} Resolves after the write, best-effort list refresh, selector rebuild, and success notification.
 *
 * Side effects: performs network requests, updates saved-report caches and association state, rebuilds the selector, and notifies the user.
 * @throws {Error} When update mode has no replacement target or a network request fails.
 */
export async function saveReport(w, { title, isGlobal, isPrimary, asNew, target = null }) {
    const state = w.serialize();
    const revision = w.stateRevision;
    let savedSummary;
    if (asNew) {
        savedSummary = await api(w.documentFamilyUrl("saved"), {
            method: "POST", body: { title, state, isGlobal, isPrimary },
        });
    } else {
        const saved = target ?? w.currentSaved;
        if (!saved) throw new Error(w.t("saved.selectReplacement"));
        const body = { title, state };
        if (canRequestAdministration(w)) {
            body.isGlobal = isGlobal;
            body.isPrimary = isPrimary;
        }
        savedSummary = await api(apiUrl(w.base, saved.id), {
            method: "PUT", body,
        });
    }
    // Cache policy: keep the known successful mutation in the local cache even if the following
    // list refresh fails. Saving does not validate a rendered query, so it may update the saved
    // association but never the rollback document.
    upsertSavedSummary(w, savedSummary);
    w.recordSaved(savedSummary, revision);
    await loadSavedList(w);
    refreshSavedSelect(w);
    w.notify(w.t("saved.saved"));
}

/**
 * Confirms and deletes the current saved report, then reconciles cached and working state.
 *
 * @param {object} w - The report controller containing the current summary, state revision, caches, and UI services.
 * @returns {Promise<void>} Resolves after no-op, cancellation, successful reconciliation, or displayed failure.
 *
 * Side effects: opens a confirmation dialog, performs delete/list requests, updates caches and selection, may reset to primary state, and shows a notification or error.
 */
export async function deleteCurrentSaved(w) {
    const s = w.currentSaved;
    if (!s) return;
    if (!await confirmDialog(w, w.t("saved.deleteTitle"), w.t("saved.deleteConfirm", { title: s.title }))) return;
    const revision = w.stateRevision;
    try {
        await api(apiUrl(w.base, s.id), { method: "DELETE" });
        const deletedCurrent = w.currentSaved?.id === s.id;
        if (deletedCurrent) w.currentSaved = null;
        w.forgetSaved(s.id);
        removeSavedSummary(w, s.id);
        await loadSavedList(w);
        // Invariant: do not replace a document changed after the delete began. The deleted
        // association is cleared either way; only a still-current deletion resets the working
        // document to Default.
        if (deletedCurrent && w.isCurrentStateTransition(revision)) await resetToPrimary(w);
        else refreshSavedSelect(w);
        w.notify(w.t("saved.deleted"));
    } catch (err) {
        w.showError(err);
    }
}
