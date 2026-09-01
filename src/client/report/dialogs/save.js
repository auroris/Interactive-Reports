// Saved-report authoring dialog: handles Save, Save As, collision replacement, and administrative publication fields.

import { el, labeled } from "../../core/dom.js";
import { confirmDialog, openDialog } from "../../core/dialog.js";
import {
    canManageCurrentSaved,
    canManageSaved,
    canRequestAdministration,
    sameTitle,
    saveReport,
} from "../saved.js";

/**
 * Opens Save or Save As and routes create, update, and title-collision replacement through the saved-report controller.
 *
 * @param {object} w - The report controller containing current saved metadata, cached summaries, identity hints, and state.
 * @param {{asNew: boolean}} options - Whether the dialog must create a new report instead of updating a manageable current report.
 * @returns {void} No value.
 *
 * Side effects: opens modeless and modal dialogs and may perform saved-report network writes, update caches, and show errors.
 */
export function saveDialog(w, { asNew }) {
    const updating = !asNew && canManageCurrentSaved(w);
    const titleInp = el("input", {
        class: "ir-input", type: "text", maxLength: 200, required: true,
        value: updating ? w.currentSaved.title : "",
        placeholder: w.t("saved.namePlaceholder"),
    });
    const globalChk = el("input", {
        type: "checkbox",
        checked: updating ? !!w.currentSaved.isGlobal : false,
    });
    const primaryChk = el("input", {
        type: "checkbox",
        checked: updating ? !!w.currentSaved.isPrimary : false,
    });

    openDialog({
        owner: w,
        title: w.t(updating ? "saved.saveTitle" : "saved.saveAsTitle"),
        width: "26rem",
        applyLabel: w.t("menu.save"),
        build: body => {
            body.append(labeled(w.t("common.name"), titleInp));
            if (canRequestAdministration(w)) {
                body.append(
                    el("label", { class: "ir-checkline" }, primaryChk,
                        w.t("saved.primaryHelp")),
                    el("label", { class: "ir-checkline" }, globalChk,
                        w.t("saved.globalHelp")));
            }
        },
        onApply: async () => {
            const title = titleInp.value.trim();
            if (!title) throw new Error(w.t("saved.enterName"));
            if (!updating) {
                const requestedPublic = canRequestAdministration(w)
                    && (globalChk.checked || primaryChk.checked);
                const isPublic = saved => saved.isDefault || saved.isGlobal || saved.isPrimary;
                const matches = (w.savedList ?? [])
                    .filter(saved => sameTitle(saved.title, title))
                    .filter(saved => requestedPublic ? isPublic(saved) : isPublic(saved) || saved.mine);
                if (matches.length > 1)
                    throw new Error(w.t("saved.duplicateNames", { title }));
                if (matches.length === 1) {
                    const target = matches[0];
                    if (isPublic(target) !== requestedPublic || !canManageSaved(w, target))
                        throw new Error(w.t("saved.cannotReplace", { title }));
                    const replace = await confirmDialog(
                        w,
                        w.t("saved.replaceTitle"),
                        w.t("saved.replaceConfirm", { title: target.title }),
                        w.t("saved.replace"));
                    if (!replace) return false;
                    await saveReport(w, {
                        title: target.title,
                        isGlobal: target.isGlobal,
                        isPrimary: target.isPrimary,
                        asNew: false,
                        target,
                    });
                    return;
                }
            }
            return saveReport(w, {
                title,
                isGlobal: canRequestAdministration(w) ? globalChk.checked : false,
                isPrimary: canRequestAdministration(w) ? primaryChk.checked : false,
                asNew: !updating,
            });
        },
    });
}
