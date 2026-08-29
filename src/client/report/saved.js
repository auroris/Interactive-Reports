// Saved-report management: the saved-report select, loading and resetting the
// working copy, and create/update/delete against the saved-report endpoints.
// The server enforces the authorization matrix; canManageCurrentSaved only
// decides which controls are worth offering.

import { api, apiUrl } from "../core/api.js";
import { el } from "../core/dom.js";
import { confirmDialog } from "../core/dialog.js";
import { featureEnabled } from "./schema.js";

export const sameTitle = (left, right) => typeof left === "string" && typeof right === "string"
    && left.localeCompare(right, undefined, { sensitivity: "accent" }) === 0;

export function canManageCurrentSaved(w) {
    return canManageSaved(w, w.currentSaved);
}

export function canRequestAdministration(w) {
    return !!w.whoami?.isAdministrator
        || !!w.schema?.authorization?.mayRequestAdministration
        || (!!w.whoami?.authenticated
            && !w.whoami?.administratorListConfigured
            && !!w.whoami?.applicationAuthorizationConfigured);
}

export function canManageSaved(w, s) {
    if (!s) return false;
    return !s.isReadOnly && (canRequestAdministration(w) || s.mine);
}

export function refreshSavedSelect(w) {
    const { savedSel, savedWrap } = w.els;
    const defaultSaved = (w.savedList ?? []).find(s => s.isPrimary && sameTitle(s.title, "Default"));
    savedSel.replaceChildren(new Option(w.t("saved.default"), defaultSaved?.id ?? ""));
    const group = (label, items) => {
        if (!items.length) return;
        const g = el("optgroup", { label });
        for (const s of items) g.append(new Option(s.title, s.id));
        savedSel.append(g);
    };
    group(w.t("saved.primary"), w.savedList.filter(s => s.isPrimary && s !== defaultSaved));
    group(w.t("saved.global"), w.savedList.filter(s => !s.isPrimary && s.isGlobal));
    group(w.t("saved.private"), w.savedList.filter(s => !s.isPrimary && !s.isGlobal));
    savedSel.value = w.currentSaved?.id ?? defaultSaved?.id ?? "";
    savedWrap.hidden = w.savedList.length === 0 || !featureEnabled(w, "savedReports");
}

async function loadSavedList(w) {
    try {
        w.savedList = await api(w.reportUrl("saved"));
    } catch (err) {
        // 404 = the feature is off. A real failure keeps the list we have —
        // wiping it would present a server problem as "no saved reports".
        if (err.status === 404) { w.savedList = []; return; }
        w.notify(w.t("saved.listRefreshFailed", { message: err.message }), "warn");
    }
}

function upsertSavedSummary(w, summary) {
    const index = w.savedList.findIndex(saved => saved.id === summary.id);
    if (index < 0) {
        w.savedList = [...w.savedList, summary];
        return;
    }
    w.savedList = w.savedList.map((saved, i) => i === index ? summary : saved);
}

function removeSavedSummary(w, id) {
    w.savedList = w.savedList.filter(saved => saved.id !== id);
}

export async function loadSavedById(w, id) {
    const transition = w.beginStateTransition();
    try {
        const docResponse = await api(apiUrl(w.base, "saved", id));
        if (!w.isCurrentStateTransition(transition)) return;
        w.currentSaved = docResponse.summary;
        // Liberal acceptance: the document is adopted as-is and the server
        // judges it on query — a rejection lands in the catch and rolls back.
        w.adoptState(docResponse.state);
        refreshSavedSelect(w);
        await w.runQuery({ quiet: true });
    } catch (err) {
        if (!w.isCurrentStateTransition(transition)) return;
        // Nothing validated: put doc, selection, and search back on the last
        // validated state so Save/Delete cannot target the wrong report while
        // the previous grid is still on screen.
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

export async function resetToPrimary(w) {
    const transition = w.beginStateTransition();
    w.currentSaved = (w.savedList ?? []).find(s => s.isPrimary && sameTitle(s.title, "Default")) ?? null;
    w.adoptState(w.schema?.defaultState);
    refreshSavedSelect(w);
    try {
        await w.runQuery();
    } catch {
        if (w.isCurrentStateTransition(transition)) w.restoreLastGood();
    }
}

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

export async function saveReport(w, { title, isGlobal, isPrimary, asNew, target = null }) {
    const state = w.serialize();
    const revision = w.stateRevision;
    let savedSummary;
    if (asNew) {
        savedSummary = await api(w.reportUrl("saved"), {
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
        savedSummary = await api(apiUrl(w.base, "saved", saved.id), {
            method: "PUT", body,
        });
    }
    // Keep the known successful mutation in the local cache even if the
    // following list refresh fails. Saving does not validate a rendered query,
    // so it may update the saved association but never the rollback document.
    upsertSavedSummary(w, savedSummary);
    w.recordSaved(savedSummary, revision);
    await loadSavedList(w);
    refreshSavedSelect(w);
    w.notify(w.t("saved.saved"));
}

export async function deleteCurrentSaved(w) {
    const s = w.currentSaved;
    if (!s) return;
    if (!await confirmDialog(w, w.t("saved.deleteTitle"), w.t("saved.deleteConfirm", { title: s.title }))) return;
    const revision = w.stateRevision;
    try {
        await api(apiUrl(w.base, "saved", s.id), { method: "DELETE" });
        const deletedCurrent = w.currentSaved?.id === s.id;
        if (deletedCurrent) w.currentSaved = null;
        w.forgetSaved(s.id);
        removeSavedSummary(w, s.id);
        await loadSavedList(w);
        // Do not replace a document changed after the delete began. The deleted
        // association is cleared either way; only a still-current deletion
        // resets the working document to Default.
        if (deletedCurrent && w.isCurrentStateTransition(revision)) await resetToPrimary(w);
        else refreshSavedSelect(w);
        w.notify(w.t("saved.deleted"));
    } catch (err) {
        w.showError(err);
    }
}
