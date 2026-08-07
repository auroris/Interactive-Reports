// Expression-rule dialogs — the client face of the server's unified expression
// pipeline: filters (predicate over rows), computed columns (value per row),
// and highlights (predicate driving row/cell styling).

import { el, labeled, sel } from "../../core/dom.js";
import { openDialog } from "../../core/dialog.js";
import { pickable } from "../schema.js";
import { expressionEditor, colOptions } from "./parts.js";

export function filterDialog(w, { editIndex, col } = {}) {
    const existing = editIndex !== undefined ? w.doc.filters?.[editIndex] : undefined;
    const condition = expressionEditor(w, {
        initial: existing?.expr ?? (col ? `${col} = ` : ""),
        placeholder: "e.g. AMOUNT > 1000 AND STATUS <> 'CANCELLED'",
        result: "predicate",
    });

    openDialog({
        owner: w,
        title: editIndex !== undefined ? "Edit Filter" : "Add Filter",
        width: "30rem",
        build: body => body.append(condition),
        onApply: () => {
            const rule = { expr: condition._read(), enabled: existing?.enabled ?? true };
            return w.apply(d => {
                d.filters ??= [];
                if (editIndex !== undefined) d.filters[editIndex] = rule;
                else d.filters.push(rule);
            });
        },
    });
}

export function computeDialog(w, editIndex) {
    const existing = editIndex !== undefined ? w.doc.computed?.[editIndex] : undefined;
    const labelInp = el("input", { class: "ir-input", type: "text", value: existing?.label ?? "", placeholder: "Column heading" });
    const expression = expressionEditor(w, {
        initial: existing?.expr,
        placeholder: "e.g. ROUND(AMOUNT * 1.0825, 2)",
        result: "value",
        // Base columns only (computed cannot reference computed), with the report's
        // display labels on the buttons; inserted tokens are always the real names.
        columns: pickable(w).filter(c => !c.computed),
    });

    openDialog({
        owner: w,
        title: editIndex !== undefined ? "Edit Computed Column" : "Compute Column",
        width: "36rem",
        build: body => body.append(
            labeled("Column Heading", labelInp),
            expression),
        onApply: () => {
            const expr = expression._read();
            const ids = (w.doc.computed ?? []).map(c => c.id);
            let n = 1;
            while (ids.includes(`c${n}`)) n++;
            const rule = {
                id: existing?.id ?? `c${n}`,
                label: labelInp.value.trim() || (existing?.id ?? `c${n}`),
                expr,
                enabled: existing?.enabled ?? true,
            };
            return w.apply(d => {
                d.computed ??= [];
                if (editIndex !== undefined) d.computed[editIndex] = rule;
                else d.computed.push(rule);
            });
        },
    });
}

export function highlightDialog(w, editIndex) {
    const existing = editIndex !== undefined ? w.doc.highlights?.[editIndex] : undefined;

    const scopeSel = sel([{ value: "row", label: "Row" }, { value: "cell", label: "Cell" }], existing?.scope ?? "row");
    const targetSel = sel(colOptions(w), existing?.col);
    const targetField = labeled("Highlight Column", targetSel);
    const condition = expressionEditor(w, {
        initial: existing?.expr,
        placeholder: "e.g. ROUND(AMOUNT, 2) > 1000 OR NOTES IS NULL",
        result: "predicate",
    });

    const bgInp = el("input", { type: "color", class: "ir-color", value: existing?.style?.bg ?? "#fff3cd" });
    const bgOn = el("input", { type: "checkbox", checked: existing ? !!existing.style?.bg : true });
    const fgInp = el("input", { type: "color", class: "ir-color", value: existing?.style?.fg ?? "#9f1239" });
    const fgOn = el("input", { type: "checkbox", checked: !!existing?.style?.fg });

    const syncScope = () => { targetField.hidden = scopeSel.value !== "cell"; };
    scopeSel.onchange = syncScope;
    syncScope();

    openDialog({
        owner: w,
        title: editIndex !== undefined ? "Edit Highlight" : "Highlight",
        width: "30rem",
        build: body => body.append(
            labeled("Highlight", scopeSel),
            targetField,
            el("div", { class: "ir-field-label ir-condition-head" }, "When"),
            condition,
            el("div", { class: "ir-colors" },
                el("label", { class: "ir-color-pick" }, bgOn, "Background", bgInp),
                el("label", { class: "ir-color-pick" }, fgOn, "Text", fgInp))),
        onApply: () => {
            const expr = condition._read();
            if (!bgOn.checked && !fgOn.checked) throw new Error("Pick a background or text color");
            const ids = (w.doc.highlights ?? []).map(h => h.id);
            let n = 1;
            while (ids.includes(`h${n}`)) n++;
            const rule = {
                id: existing?.id ?? `h${n}`,
                enabled: existing?.enabled ?? true,
                scope: scopeSel.value,
                expr,
            };
            if (scopeSel.value === "cell") rule.col = targetSel.value;
            rule.style = {};
            if (bgOn.checked) rule.style.bg = bgInp.value;
            if (fgOn.checked) rule.style.fg = fgInp.value;
            return w.apply(d => {
                d.highlights ??= [];
                if (editIndex !== undefined) d.highlights[editIndex] = rule;
                else d.highlights.push(rule);
            });
        },
    });
}
