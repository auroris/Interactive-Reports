// The Save / Save As dialog for saved reports.

import { el, labeled } from "../../core/dom.js";
import { openDialog } from "../../core/dialog.js";
import { canManageCurrentSaved, saveReport } from "../saved.js";

export function saveDialog(w, { asNew }) {
    const updating = !asNew && canManageCurrentSaved(w);
    const titleInp = el("input", {
        class: "ir-input", type: "text", maxLength: 200,
        value: updating ? w.currentSaved.title : "",
        placeholder: "Saved report name",
    });
    const globalChk = el("input", {
        type: "checkbox",
        checked: updating ? !!w.currentSaved.isGlobal : false,
    });

    openDialog({
        owner: w,
        title: updating ? "Save Report" : "Save Report As",
        width: "26rem",
        applyLabel: "Save",
        build: body => {
            body.append(labeled("Name", titleInp));
            if (w.whoami?.isAdministrator)
                body.append(el("label", { class: "ir-checkline" }, globalChk, "Global — visible to everyone with access to this report"));
        },
        onApply: () => {
            const title = titleInp.value.trim();
            if (!title) throw new Error("Enter a name");
            return saveReport(w, {
                title,
                isGlobal: w.whoami?.isAdministrator ? globalChk.checked : false,
                asNew: !updating,
            });
        },
    });
}
