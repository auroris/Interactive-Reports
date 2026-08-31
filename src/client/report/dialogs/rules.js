// Expression-rule dialogs for the server's composable table algebra: filters (predicates over
// rows), computed columns (values per row), and highlights
// (predicate driving row/cell styling). Every rule edits the exact terminal composable owned by
// the active table.

import { el, labeled, sel } from "../../core/dom.js";
import { openDialog } from "../../core/dialog.js";
import { tableContext } from "../table.js";
import {
    composableLocations,
    nextFreeId,
    nextSyntheticColumnId,
    normalizedHighlightRules,
} from "../state.js";
import { colorPick, expressionColumnToken, expressionEditor, colOptions } from "./parts.js";

/**
 * Returns the normalized kind of a composable operation.
 *
 * @param {object|null|undefined} composable - The composable whose kind token will be normalized.
 * @returns {string} The normalized composable kind.
 */
const kindOf = composable => String(composable?.kind ?? "").trim().toLowerCase();

// Invariant: highlight declarations in every repeated node owned by the active table form one
// priority set. Earlier nodes may be read-only in the packaged editor, but their ids and
// explicit/implicit precedence still reserve authoring space.
/**
 * Returns highlight rules authored by the active table with their mutation locations.
 *
 * @param {object} doc - The report state whose active table ownership will be inspected.
 * @returns {Array<object>} Highlight rule objects across all nodes owned by the active table, in storage order.
 */
const ownedHighlights = doc => composableLocations(doc)
    .filter(location => location.owned && kindOf(location.composable) === "highlight")
    .flatMap(location => location.composable?.highlights ?? []);

/**
 * Opens an add or edit dialog for one active-table filter expression.
 *
 * @param {object} w - The report controller whose active table, expression capabilities, and apply pipeline are used.
 * @param {{editIndex?: number, col?: string}} [options={}] - Existing rule index or an optional column used to seed a new expression.
 * @returns {void} No value.
 *
 * Side effects: opens a dialog; applying it inserts or replaces a filter and runs the report.
 */
export function filterDialog(w, { editIndex, col } = {}) {
    const ctx = tableContext(w);
    const existing = editIndex !== undefined ? ctx.node(w.doc, "filter")?.filters?.[editIndex] : undefined;
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
                ctx.edit(d, "filter", node => {
                    node.filters ??= [];
                    if (editIndex !== undefined) node.filters[editIndex] = rule;
                    else node.filters.push(rule);
                });
            });
        },
    });
}

/**
 * Opens an add or edit dialog for one computed column and allocates a stable synthetic id for new rules.
 *
 * @param {object} w - The report controller whose active output columns and expression capabilities are used.
 * @param {number|undefined} editIndex - The zero-based rule index to edit; `undefined` creates a new rule.
 * @returns {void} No value.
 *
 * Side effects: opens a dialog; applying it inserts or replaces a computed rule and runs the report.
 */
export function computeDialog(w, editIndex) {
    const ctx = tableContext(w);
    const existing = editIndex !== undefined ? ctx.node(w.doc, "compute")?.computed?.[editIndex] : undefined;
    const computeColumns = existing?.id
        ? ctx.computeTokens.filter(column =>
            column.name.toLowerCase() !== String(existing.id).toLowerCase())
        : ctx.computeTokens;
    const labelInp = el("input", {
        class: "ir-input", type: "text", value: existing?.label ?? "",
        placeholder: w.t("compute.headingPlaceholder"),
    });
    const expression = expressionEditor(w, {
        initial: existing?.expr,
        placeholder: w.t("expression.gridValuePlaceholder"),
        result: "value",
        // Protocol contract: existing computed outputs can participate in the dependency graph.
        // When editing, omit only the rule's own id; the server detects longer cycles.
        columns: computeColumns,
    });

    openDialog({
        owner: w,
        title: w.t(editIndex !== undefined ? "compute.editTitle" : "compute.title"),
        width: "36rem",
        build: body => body.append(
            labeled(w.t("columns.heading"), labelInp),
            expression),
        onApply: () => {
            const expr = expression._read();
            const id = existing?.id ?? nextSyntheticColumnId(
                w.doc,
                [...(w.schema?.columns ?? []), ...(ctx.columns ?? [])]);
            const rule = {
                id,
                label: labelInp.value.trim() || id,
                expr,
                enabled: existing?.enabled ?? true,
            };
            return w.apply(d => {
                ctx.edit(d, "compute", node => {
                    node.computed ??= [];
                    if (editIndex !== undefined) node.computed[editIndex] = rule;
                    else node.computed.push(rule);
                });
            });
        },
    });
}

/**
 * Opens an add or edit dialog for a sequenced row or cell highlight rule.
 *
 * @param {object} w - The report controller whose active columns, owned highlights, and apply pipeline are used.
 * @param {number|undefined} editIndex - The zero-based rule index to edit; `undefined` creates a new rule.
 * @returns {void} No value.
 *
 * Side effects: opens a dialog; applying it validates name and colors, then inserts or replaces a highlight and runs the report.
 */
export function highlightDialog(w, editIndex) {
    const ctx = tableContext(w);
    const existing = editIndex !== undefined ? ctx.node(w.doc, "highlight")?.highlights?.[editIndex] : undefined;
    const rules = ownedHighlights(w.doc);
    const ids = new Set(rules
        .map(rule => String(rule?.id ?? "").trim().toLowerCase())
        .filter(Boolean));
    const freshId = nextFreeId(ids, "h");
    const id = existing?.id ?? freshId;
    const normalized = normalizedHighlightRules(rules);
    const nextSequence = Math.max(0, ...normalized.map(entry => entry.sequence)) + 10;
    const existingSequence = normalized.find(entry => entry.rule === existing)?.sequence;
    const nameInp = el("input", {
        class: "ir-input", type: "text",
        value: existing?.name ?? existing?.id ?? w.t("highlight.defaultName", { number: freshId.slice(1) }),
        placeholder: w.t("highlight.namePlaceholder"), required: true,
    });
    const sequenceInp = el("input", {
        class: "ir-input", type: "number", min: 1, step: 1, required: true,
        value: existing?.sequence ?? existingSequence ?? nextSequence,
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
        placeholder: w.t("expression.gridHighlightPlaceholder"),
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
            el("p", { class: "ir-dialog-note" }, w.t("highlight.sequenceNote")),
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
                ctx.edit(d, "highlight", node => {
                    node.highlights ??= [];
                    if (editIndex !== undefined) node.highlights[editIndex] = rule;
                    else node.highlights.push(rule);
                });
            });
        },
    });
}
