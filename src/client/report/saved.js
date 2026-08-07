// Saved-report management: the saved-report select, loading and resetting the
// working copy, and create/update/delete against the saved-report endpoints.
// The server enforces the authorization matrix; canManageCurrentSaved only
// decides which controls are worth offering.

import { api, apiUrl } from "../core/api.js";
import { el } from "../core/dom.js";
import { confirmDialog } from "../core/dialog.js";
import { featureEnabled } from "./schema.js";

export function canManageCurrentSaved(w) {
    const s = w.currentSaved;
    if (!s) return false;
    return !s.isReadOnly && (w.whoami?.isAdministrator || (s.mine && !s.isGlobal));
}

export function refreshSavedSelect(w) {
    const { savedSel, savedWrap } = w.els;
    savedSel.replaceChildren(new Option("Primary Report", ""));
    const group = (label, items) => {
        if (!items.length) return;
        const g = el("optgroup", { label });
        for (const s of items) g.append(new Option(s.title + (s.mine || s.isGlobal ? "" : ` (${s.owner})`), s.id));
        savedSel.append(g);
    };
    group("Global", w.savedList.filter(s => s.isGlobal));
    group("Private", w.savedList.filter(s => !s.isGlobal));
    savedSel.value = w.currentSaved?.id ?? "";
    savedWrap.hidden = w.savedList.length === 0 || !featureEnabled(w, "savedReports");
}

async function loadSavedList(w) {
    w.savedList = await api(w.reportUrl("saved")).catch(() => []);
}

export async function loadSavedById(w, id) {
    try {
        const docResponse = await api(apiUrl(w.base, "saved", id));
        w.currentSaved = docResponse.summary;
        w.doc = w.normalize(docResponse.state);
        w.els.search.value = w.doc.search ?? "";
        refreshSavedSelect(w);
        await w.runQuery();
    } catch (err) {
        if (err.name !== "AbortError") w.showError(err);
    }
}

export function resetToPrimary(w) {
    w.currentSaved = null;
    w.doc = w.normalize(w.schema?.defaultState);
    w.els.search.value = w.doc.search ?? "";
    refreshSavedSelect(w);
    w.runQuery().catch(() => {});
}

export async function resetWorkingCopy(w) {
    const target = w.currentSaved ? `"${w.currentSaved.title}"` : "its default settings";
    if (!await confirmDialog(w, "Reset", `Restore this report to ${target}? Unsaved changes are lost.`, "Reset")) return;
    if (w.currentSaved) await loadSavedById(w, w.currentSaved.id);
    else resetToPrimary(w);
}

export async function saveReport(w, { title, isGlobal, asNew }) {
    const state = w.serialize();
    if (asNew) {
        w.currentSaved = await api(w.reportUrl("saved"), {
            method: "POST", body: { title, state, isGlobal },
        });
    } else {
        const body = { title, state };
        if (w.whoami?.isAdministrator) body.isGlobal = isGlobal;
        w.currentSaved = await api(apiUrl(w.base, "saved", w.currentSaved.id), {
            method: "PUT", body,
        });
    }
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
        await loadSavedList(w);
        resetToPrimary(w);
        w.notify("Saved report deleted.");
    } catch (err) {
        w.showError(err);
    }
}
