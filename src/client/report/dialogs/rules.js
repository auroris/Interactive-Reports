// Expression-rule dialogs — the client face of the server's unified expression
// pipeline: filters (predicate over rows), computed columns (value per row),
// and highlights (predicate driving row/cell styling). Every rule edits the
// layer of the table currently being displayed: source, Group By, or Pivot.

import { el, labeled, sel } from "../../core/dom.js";
import { openDialog } from "../../core/dialog.js";
import { stageContext } from "../stage.js";
import { nextFreeId, sourceLayer } from "../state.js";
import { colorPick, expressionColumnToken, expressionEditor, colOptions } from "./parts.js";

export function filterDialog(w, { editIndex, col } = {}) {
    const ctx = stageContext(w);
    const layerOf = ctx.filterLayer ?? (d => sourceLayer(d));
    const existing = editIndex !== undefined ? layerOf(w.doc).filters?.[editIndex] : undefined;
    const condition = expressionEditor(w, {
        initial: existing?.expr ?? (col ? `${expressionColumnToken(col)} = ` : ""),
        placeholder: w.t("expression.filterPlaceholder"),
        result: "predicate",
        columns: ctx.filterColumns ?? ctx.columns,
    });

    openDialog({
        owner: w,
        title: w.t(editIndex !== undefined ? "filter.editTitle" : "filter.addTitle"),
        width: "30rem",
        build: body => body.append(condition),
        onApply: () => {
            const rule = { expr: condition._read(), enabled: existing?.enabled ?? true };
            return w.apply(d => {
                const layer = layerOf(d);
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
    const labelInp = el("input", {
        class: "ir-input", type: "text", value: existing?.label ?? "",
        placeholder: w.t("compute.headingPlaceholder"),
    });
    const grid = ctx.mode === "grid";
    const expression = expressionEditor(w, {
        initial: existing?.expr,
        placeholder: w.t(grid ? "expression.gridValuePlaceholder" : "expression.groupValuePlaceholder"),
        result: "value",
        // The current stage's input columns (computed cannot reference computed),
        // with display labels on the buttons; inserted tokens are always the real
        // names — base columns in grid, dims/__count/metric ids under a group.
        columns: ctx.computeTokens,
    });

    openDialog({
        owner: w,
        title: w.t(editIndex !== undefined ? "compute.editTitle" : "compute.title"),
        width: "36rem",
        build: body => body.append(
            labeled(w.t("columns.heading"), labelInp),
            expression,
            grid ? null : el("p", { class: "ir-dialog-note" },
                w.t("compute.viewNote"))),
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
        class: "ir-input", type: "text",
        value: existing?.name ?? existing?.id ?? w.t("highlight.defaultName", { number: freshId.slice(1) }),
        placeholder: w.t("highlight.namePlaceholder"), required: true,
    });
    const sequenceInp = el("input", {
        class: "ir-input", type: "number", min: 1, step: 1, required: true,
        value: existing?.sequence ?? (editIndex !== undefined ? (editIndex + 1) * 10 : nextSequence),
    });

    const scopeSel = sel([
        { value: "row", label: w.t("highlight.row") },
        { value: "cell", label: w.t("highlight.cell") },
    ], existing?.scope ?? "row");
    scopeSel.classList.add("ir-highlight-scope");
    const targetSel = sel(colOptions(w, { columns: ctx.columns }), existing?.col);
    const targetField = labeled(w.t("highlight.column"), targetSel);
    targetField.classList.add("ir-cell-only");
    const condition = expressionEditor(w, {
        initial: existing?.expr,
        placeholder: w.t(ctx.mode === "grid"
            ? "expression.gridHighlightPlaceholder"
            : "expression.groupHighlightPlaceholder"),
        result: "predicate",
        columns: ctx.columns,
    });

    const bgPick = colorPick(
        w.t("common.background"),
        existing ? (existing.style?.bg ?? null) : "#fff3cd",
        "#fff3cd",
        w);
    const fgPick = colorPick(w.t("columns.textColor"), existing?.style?.fg ?? null, "#9f1239", w);

    openDialog({
        owner: w,
        title: w.t(editIndex !== undefined ? "highlight.editTitle" : "highlight.title"),
        width: "30rem",
        build: body => body.append(
            labeled(w.t("common.name"), nameInp),
            labeled(w.t("common.sequence"), sequenceInp),
            labeled(w.t("highlight.applyTo"), scopeSel),
            targetField,
            el("div", { class: "ir-field-label ir-condition-head" }, w.t("highlight.when")),
            condition,
            el("div", { class: "ir-colors" },
                bgPick.node,
                fgPick.node)),
        onApply: () => {
            const expr = condition._read();
            const name = nameInp.value.trim();
            if (!name) throw new Error(w.t("highlight.enterName"));
            const sequence = Number(sequenceInp.value);
            const bg = bgPick.read();
            const fg = fgPick.read();
            if (!bg && !fg) throw new Error(w.t("highlight.pickColor"));
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
