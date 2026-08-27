// Expression-rule dialogs — the client face of the server's unified expression
// pipeline: filters (predicate over rows), computed columns (value per row),
// and highlights (predicate driving row/cell styling). Filters always edit the
// source stage; Compute and Highlight edit whichever layer the current stage
// context routes to — the source table in grid, the group stage's table under
// a group or spread tail (where computed metrics derive from dims, __count,
// and metrics, and spread them into cells).

import { el, labeled, sel } from "../../core/dom.js";
import { openDialog } from "../../core/dialog.js";
import { filterableColumns } from "../schema.js";
import { stageContext } from "../stage.js";
import { nextFreeId, sourceLayer } from "../state.js";
import { colorPick, expressionEditor, colOptions } from "./parts.js";

export function filterDialog(w, { editIndex, col } = {}) {
    const existing = editIndex !== undefined ? sourceLayer(w.doc).filters?.[editIndex] : undefined;
    const condition = expressionEditor(w, {
        initial: existing?.expr ?? (col ? `${col} = ` : ""),
        placeholder: "e.g. AMOUNT > 1000 AND STATUS <> 'CANCELLED'",
        result: "predicate",
        // Token buttons omit definition-restricted columns; a typed reference
        // still reaches the server, which strips the rule into ignored[].
        columns: filterableColumns(w),
    });

    openDialog({
        owner: w,
        title: editIndex !== undefined ? "Edit Filter" : "Add Filter",
        width: "30rem",
        build: body => body.append(condition),
        onApply: () => {
            const rule = { expr: condition._read(), enabled: existing?.enabled ?? true };
            return w.apply(d => {
                const layer = sourceLayer(d);
                layer.filters ??= [];
                if (editIndex !== undefined) layer.filters[editIndex] = rule;
                else layer.filters.push(rule);
            });
        },
    });
}

/// The next computed id, unique across every layer of the document — a group
/// stage's schema contains source computed columns as dims, so ids can never
/// be reused between stages.
function nextComputedId(doc) {
    const ids = new Set();
    for (const stage of doc.pipeline ?? [])
        for (const rule of stage.layer?.computed ?? []) ids.add(rule.id.toLowerCase());
    for (const stages of Object.values(doc.shelf ?? {}))
        for (const stage of stages ?? [])
            for (const rule of stage.layer?.computed ?? []) ids.add(rule.id.toLowerCase());
    return nextFreeId(ids, "c");
}

export function computeDialog(w, editIndex) {
    const ctx = stageContext(w);
    const layerOf = ctx.computeLayer;
    const existing = editIndex !== undefined ? layerOf(w.doc).computed?.[editIndex] : undefined;
    const labelInp = el("input", { class: "ir-input", type: "text", value: existing?.label ?? "", placeholder: "Column heading" });
    const grid = ctx.mode === "grid";
    const expression = expressionEditor(w, {
        initial: existing?.expr,
        placeholder: grid ? "e.g. ROUND(AMOUNT * 1.0825, 2)" : "e.g. ROUND(m1 / __count, 2)",
        result: "value",
        // The current stage's input columns (computed cannot reference computed),
        // with display labels on the buttons; inserted tokens are always the real
        // names — base columns in grid, dims/__count/metric ids under a group.
        columns: ctx.computeTokens,
    });

    openDialog({
        owner: w,
        title: editIndex !== undefined ? "Edit Computed Column" : "Compute Column",
        width: "36rem",
        build: body => body.append(
            labeled("Column Heading", labelInp),
            expression,
            grid ? null : el("p", { class: "ir-dialog-note" },
                "Computed here, the column derives from this view's table — group values and counts.")),
        onApply: () => {
            const expr = expression._read();
            const id = existing?.id ?? nextComputedId(w.doc);
            const rule = {
                id,
                label: labelInp.value.trim() || id,
                expr,
                enabled: existing?.enabled ?? true,
            };
            return w.apply(d => {
                const layer = layerOf(d);
                layer.computed ??= [];
                if (editIndex !== undefined) layer.computed[editIndex] = rule;
                else layer.computed.push(rule);
            });
        },
    });
}

export function highlightDialog(w, editIndex) {
    const ctx = stageContext(w);
    const layerOf = ctx.highlightLayer ?? (d => sourceLayer(d));
    const existing = editIndex !== undefined ? layerOf(w.doc).highlights?.[editIndex] : undefined;
    const rules = layerOf(w.doc).highlights ?? [];
    const ids = new Set(rules.map(h => (h.id ?? "").toLowerCase()));
    const freshId = nextFreeId(ids, "h");
    const id = existing?.id ?? freshId;
    const nextSequence = Math.max(0, ...rules.map((h, i) => h.sequence ?? ((i + 1) * 10))) + 10;
    const nameInp = el("input", {
        class: "ir-input", type: "text", value: existing?.name ?? existing?.id ?? `Highlight ${freshId.slice(1)}`,
        placeholder: "Highlight name", required: true,
    });
    const sequenceInp = el("input", {
        class: "ir-input", type: "number", min: 1, step: 1, required: true,
        value: existing?.sequence ?? (editIndex !== undefined ? (editIndex + 1) * 10 : nextSequence),
    });

    const scopeSel = sel([{ value: "row", label: "Row" }, { value: "cell", label: "Cell" }], existing?.scope ?? "row");
    scopeSel.classList.add("ir-highlight-scope");
    const targetSel = sel(colOptions(w, { columns: ctx.columns }), existing?.col);
    const targetField = labeled("Highlight Column", targetSel);
    targetField.classList.add("ir-cell-only");
    const condition = expressionEditor(w, {
        initial: existing?.expr,
        placeholder: ctx.mode === "grid"
            ? "e.g. ROUND(AMOUNT, 2) > 1000 OR NOTES IS NULL"
            : "e.g. m1 > 10000",
        result: "predicate",
        columns: ctx.columns,
    });

    const bgPick = colorPick(
        "Background",
        existing ? (existing.style?.bg ?? null) : "#fff3cd",
        "#fff3cd");
    const fgPick = colorPick("Text", existing?.style?.fg ?? null, "#9f1239");

    openDialog({
        owner: w,
        title: editIndex !== undefined ? "Edit Highlight" : "Highlight",
        width: "30rem",
        build: body => body.append(
            labeled("Name", nameInp),
            labeled("Sequence", sequenceInp),
            labeled("Apply To", scopeSel),
            targetField,
            el("div", { class: "ir-field-label ir-condition-head" }, "When"),
            condition,
            el("div", { class: "ir-colors" },
                bgPick.node,
                fgPick.node)),
        onApply: () => {
            const expr = condition._read();
            const name = nameInp.value.trim();
            if (!name) throw new Error("Enter a highlight name");
            const sequence = Number(sequenceInp.value);
            const bg = bgPick.read();
            const fg = fgPick.read();
            if (!bg && !fg) throw new Error("Pick a background or text color");
            const rule = {
                id,
                name,
                sequence,
                enabled: existing?.enabled ?? true,
                scope: scopeSel.value,
                expr,
            };
            if (scopeSel.value === "cell") rule.col = targetSel.value;
            rule.style = {};
            if (bg) rule.style.bg = bg;
            if (fg) rule.style.fg = fg;
            return w.apply(d => {
                const layer = layerOf(d);
                layer.highlights ??= [];
                if (editIndex !== undefined) layer.highlights[editIndex] = rule;
                else layer.highlights.push(rule);
            });
        },
    });
}
