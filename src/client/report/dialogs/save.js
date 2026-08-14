// The Save / Save As dialog for saved reports.

import { el, labeled } from "../../core/dom.js";
import { confirmDialog, openDialog } from "../../core/dialog.js";
import {
    canManageCurrentSaved,
    canManageSaved,
    canRequestAdministration,
    sameTitle,
    saveReport,
} from "../saved.js";

export function saveDialog(w, { asNew }) {
    const updating = !asNew && canManageCurrentSaved(w);
    const titleInp = el("input", {
        class: "ir-input", type: "text", maxLength: 200, required: true,
        value: updating ? w.currentSaved.title : "",
        placeholder: "Saved report name",
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
        title: updating ? "Save Report" : "Save Report As",
        width: "26rem",
        applyLabel: "Save",
        build: body => {
            body.append(labeled("Name", titleInp));
            if (canRequestAdministration(w)) {
                body.append(
                    el("label", { class: "ir-checkline" }, primaryChk,
                        "Primary — visible to everyone with access to this report"),
                    el("label", { class: "ir-checkline" }, globalChk,
                        "Global — visible to everyone with access to this report"));
            }
        },
        onApply: async () => {
            const title = titleInp.value.trim();
            if (!title) throw new Error("Enter a name");
            if (!updating) {
                const matches = (w.savedList ?? []).filter(saved => sameTitle(saved.title, title));
                if (matches.length > 1)
                    throw new Error(`Several saved reports are named "${title}". Delete the duplicate before replacing one.`);
                if (matches.length === 1) {
                    const target = matches[0];
                    if (!canManageSaved(w, target))
                        throw new Error(`"${title}" already exists and cannot be replaced. Choose another name.`);
                    const replace = await confirmDialog(
                        w,
                        "Replace Saved Report",
                        `Replace "${target.title}"? Its saved settings will be overwritten.`,
                        "Replace");
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
