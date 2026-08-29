// Shared building blocks for the Actions-menu dialogs: column option lists, the
// rows-of-controls pattern, the aggregate-function select, and the expression-
// rule editor. Every dialog applies through w.apply(mutate): the widget clones
// the state doc, mutates, re-queries, and rolls back on failure — so a
// validation problem surfaces inside the dialog and the grid never breaks.

import { el, labeled, sel } from "../../core/dom.js";
import { pickable, typeOf, fnsFor, expressionFunctions } from "../schema.js";
import { fnLabel } from "../render/format.js";

export const dirOptions = w => [
    { value: "asc", label: w.t("sort.ascending") },
    { value: "desc", label: w.t("sort.descending") },
];
export const nullsOptions = w => [
    { value: "", label: w.t("sort.nullDefault") },
    { value: "first", label: w.t("sort.nullFirst") },
    { value: "last", label: w.t("sort.nullLast") },
];

export function colOptions(w, { none, columns } = {}) {
    const opts = (columns ?? pickable(w)).map(c => ({ value: c.name, label: c.computed ? `ƒ ${c.label}` : c.label }));
    return none ? [{ value: "", label: none }, ...opts] : opts;
}

/// Compact, visible labels for controls that share one repeatable dialog row.
export function rowField(text, control) {
    return el("label", { class: "ir-row-field" },
        el("span", { class: "ir-field-label" }, text), control);
}

/// Native grouping for related rows of controls.
export function fieldGroup(text, ...children) {
    return el("fieldset", { class: "ir-fieldset" },
        el("legend", { class: "ir-field-label" }, text), ...children);
}

/// Checkbox-gated color input shared by presentation and highlight editors.
/// A null read means the color is disabled; write() lets multi-column editors
/// load another staged value without rebuilding the control.
export function colorPick(label, initial, fallback, context = null) {
    const enabled = el("input", { type: "checkbox", checked: !!initial });
    const input = el("input", {
        type: "color",
        class: "ir-color",
        value: initial || fallback,
        "aria-label": context?.t?.("columns.colorAria", { label }) ?? `${label} color`,
    });
    return {
        node: el("div", { class: "ir-color-pick" },
            el("label", { class: "ir-checkline" }, enabled, label), input),
        read: () => enabled.checked ? input.value : null,
        write(value) {
            enabled.checked = !!value;
            if (value) input.value = value;
        },
    };
}

// --- rows-of-controls pattern ------------------------------------------------

export function rowList(container, items, buildRow, { addLabel = null, max, context = null } = {}) {
    const addRow = item => {
        if (max && container.querySelectorAll(".ir-dlgrow").length >= max) return;
        const row = el("div", { class: "ir-dlgrow" });
        buildRow(row, item);
        row.append(el("button", {
            type: "button", class: "ir-btn ir-row-x",
            title: context?.t?.("common.remove") ?? "Remove",
            "aria-label": context?.t?.("common.removeRow") ?? "Remove row",
            onclick: () => row.remove(),
        }, "×"));
        container.append(row);
    };
    items.forEach(addRow);
    if (items.length === 0) addRow(null);
    const addButton = el("button", { type: "button", class: "ir-btn ir-add-row", onclick: () => addRow(null) },
        `+ ${addLabel ?? context?.t?.("common.add") ?? "Add"}`);
    return { addButton, read: () => [...container.querySelectorAll(".ir-dlgrow")].map(r => r._read()).filter(x => x != null) };
}

// --- aggregate-function select -----------------------------------------------

/// A function select slaved to a column select: the options track the column's
/// type (the server's aggregateFunctions catalog), keeping the current pick
/// when it survives the change. Wires the column select's change event.
export function fnSelectFor(w, colSel, initialFn, columns = null) {
    const fnSel = el("select", { class: "ir-select" });
    const refresh = keep => {
        const selectedType = columns?.find(column =>
            column.name.toLowerCase() === colSel.value.toLowerCase())?.type;
        const fns = colSel.value ? fnsFor(w, selectedType ?? typeOf(w, colSel.value)) : [];
        fnSel.replaceChildren(...fns.map(f => new Option(fnLabel(w, f), f)));
        if (keep && fns.includes(keep)) fnSel.value = keep;
    };
    colSel.addEventListener("change", () => refresh(fnSel.value));
    refresh(initialFn);
    return fnSel;
}

/// The aggregate function/column row shared by report aggregates and grouped
/// view values. One implementation keeps option behavior, labels, and reading
/// semantics aligned across every caller.
export function aggregateRowList(w, initial, { addLabel = null, columns = null } = {}) {
    const container = el("div", {});
    const list = rowList(container, initial ?? [], (row, item) => {
        const colSel = sel(colOptions(w, { none: w.t("common.select"), columns }), item?.col ?? "");
        const fnSel = fnSelectFor(w, colSel, item?.fn, columns);
        row.append(
            rowField(w.t("common.function"), fnSel),
            el("span", { class: "ir-row-of", "aria-hidden": "true" }, w.t("common.of")),
            rowField(w.t("common.column"), colSel));
        row._read = () => colSel.value && fnSel.value
            ? { col: colSel.value, fn: fnSel.value }
            : null;
    }, { addLabel: addLabel ?? w.t("common.value"), context: w });
    return { container, list };
}

// --- expression-rule editor --------------------------------------------------

export function expressionColumnToken(name) {
    const ordinary = /^[A-Za-z_][A-Za-z0-9_$#]*$/.test(name);
    const keyword = /^(CASE|WHEN|THEN|ELSE|END|AND|OR|NOT|IS|NULL|BETWEEN)$/i.test(name);
    return ordinary && !keyword ? name : `\`${name.replaceAll("`", "``")}\``;
}

export function expressionEditor(w, { initial, placeholder, result, columns }) {
    const exprInp = el("textarea", {
        class: "ir-textarea", rows: result === "predicate" ? 4 : 3,
        spellcheck: false, placeholder, required: true,
    });
    exprInp.value = initial ?? "";
    const availableColumns = columns ?? pickable(w);

    const insert = token => {
        const at = exprInp.selectionStart ?? exprInp.value.length;
        exprInp.setRangeText(token, at, exprInp.selectionEnd ?? at, "end");
        exprInp.focus();
    };
    const tokenBtn = (label, token) =>
        el("button", { type: "button", class: "ir-token", onclick: () => insert(token) }, label);

    const conditionTokens = [
        tokenBtn("=", " = "), tokenBtn("≠", " <> "),
        tokenBtn("<", " < "), tokenBtn("≤", " <= "),
        tokenBtn(">", " > "), tokenBtn("≥", " >= "),
        tokenBtn("AND", " AND "), tokenBtn("OR", " OR "), tokenBtn("NOT", "NOT "),
        tokenBtn("BETWEEN", " BETWEEN  AND "),
        tokenBtn("IS NULL", " IS NULL"), tokenBtn("IS NOT NULL", " IS NOT NULL"),
    ];
    if (result === "value")
        conditionTokens.unshift(tokenBtn("CASE WHEN … END", "CASE WHEN  THEN  ELSE  END"));

    const wrap = el("div", { class: "ir-condition" },
        labeled(w.t("expression.expression"), exprInp),
        el("div", { class: "ir-token-group" },
            el("span", { class: "ir-field-label" }, w.t("expression.columns")),
            el("div", {}, ...availableColumns.map(c => tokenBtn(c.label, expressionColumnToken(c.name))))),
        el("div", { class: "ir-token-group" },
            el("span", { class: "ir-field-label" }, w.t("expression.functions")),
            el("div", {}, ...expressionFunctions(w).map(f => tokenBtn(f, `${f}(`)))),
        el("div", { class: "ir-token-group" },
            el("span", { class: "ir-field-label" }, w.t("expression.conditions")),
            el("div", {}, ...conditionTokens)),
        el("p", { class: "ir-dialog-note" },
            result === "predicate"
                ? w.t("expression.predicateNote")
                : w.t("expression.valueNote")));
    wrap._read = () => {
        const expr = exprInp.value.trim();
        if (!expr) throw new Error(w.t(result === "predicate" ? "expression.enterCondition" : "expression.enterExpression"));
        return expr;
    };
    return wrap;
}
