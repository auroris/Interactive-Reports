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
    savedSel.replaceChildren(new Option("Default", defaultSaved?.id ?? ""));
    const group = (label, items) => {
        if (!items.length) return;
        const g = el("optgroup", { label });
        for (const s of items) g.append(new Option(s.title + (s.mine || s.isGlobal || s.isPrimary ? "" : ` (${s.owner})`), s.id));
        savedSel.append(g);
    };
    group("Primary", w.savedList.filter(s => s.isPrimary && s !== defaultSaved));
    group("Global", w.savedList.filter(s => !s.isPrimary && s.isGlobal));
    group("Private", w.savedList.filter(s => !s.isPrimary && !s.isGlobal));
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
        w.notify(`The saved-report list could not be refreshed (${err.message}).`, "warn");
    }
}

export async function loadSavedById(w, id) {
    try {
        const docResponse = await api(apiUrl(w.base, "saved", id));
        w.currentSaved = docResponse.summary;
        // Liberal acceptance: the document is adopted as-is and the server
        // judges it on query — a rejection lands in the catch and rolls back.
        w.adoptState(docResponse.state);
        refreshSavedSelect(w);
        await w.runQuery();
    } catch (err) {
        if (err.name === "AbortError") return;
        // Nothing validated: put doc, selection, and search back on the last
        // validated state so Save/Delete cannot target the wrong report while
        // the previous grid is still on screen.
        w.restoreLastGood();
        if (err.status === 404) {
            w.showError(new Error("That saved report is no longer available — it may have been deleted."));
            await loadSavedList(w);
            refreshSavedSelect(w);
        } else {
            w.showError(err);
        }
    }
}

export async function resetToPrimary(w) {
    w.currentSaved = (w.savedList ?? []).find(s => s.isPrimary && sameTitle(s.title, "Default")) ?? null;
    w.adoptState(w.schema?.defaultState);
    refreshSavedSelect(w);
    try {
        await w.runQuery();
    } catch (err) {
        if (err.name !== "AbortError") w.restoreLastGood();
    }
}

export async function resetWorkingCopy(w) {
    const target = w.currentSaved ? `"${w.currentSaved.title}"` : "its default settings";
    if (!await confirmDialog(w, "Reset", `Restore this report to ${target}? Unsaved changes are lost.`, "Reset")) return;
    if (w.currentSaved) await loadSavedById(w, w.currentSaved.id);
    else await resetToPrimary(w);
}

export async function saveReport(w, { title, isGlobal, isPrimary, asNew, target = null }) {
    const state = w.serialize();
    if (asNew) {
        w.currentSaved = await api(w.reportUrl("saved"), {
            method: "POST", body: { title, state, isGlobal, isPrimary },
        });
    } else {
        const saved = target ?? w.currentSaved;
        if (!saved) throw new Error("Select a saved report to replace");
        const body = { title, state };
        if (canRequestAdministration(w)) {
            body.isGlobal = isGlobal;
            body.isPrimary = isPrimary;
        }
        w.currentSaved = await api(apiUrl(w.base, "saved", saved.id), {
            method: "PUT", body,
        });
    }
    // The server validated the state on save; a later rollback should land here,
    // on the newly saved report, not on whatever preceded it.
    w.commitLastGood();
    await loadSavedList(w);
    refreshSavedSelect(w);
    w.notify("Report saved.");
}

export async function deleteCurrentSaved(w) {
    const s = w.currentSaved;
    if (!s) return;
    if (!await confirmDialog(w, "Delete Saved Report", `Delete "${s.title}"? This cannot be undone.`)) return;
    try {
        await api(apiUrl(w.base, "saved", s.id), { method: "DELETE" });
        w.currentSaved = null;
        w.forgetSaved(s.id);
        await loadSavedList(w);
        await resetToPrimary(w);
        w.notify("Saved report deleted.");
    } catch (err) {
        w.showError(err);
    }
}
