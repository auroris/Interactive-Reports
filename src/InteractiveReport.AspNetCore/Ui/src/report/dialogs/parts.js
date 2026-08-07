// Shared building blocks for the Actions-menu dialogs: column option lists, the
// rows-of-controls pattern, the aggregate-function select, and the expression-
// rule editor. Every dialog applies through w.apply(mutate): the widget clones
// the state doc, mutates, re-queries, and rolls back on failure — so a
// validation problem surfaces inside the dialog and the grid never breaks.

import { el, labeled } from "../../core/dom.js";
import { pickable, typeOf, fnsFor, expressionFunctions } from "../schema.js";
import { FN_LABELS } from "../render/format.js";

export const DIR_OPTIONS = [{ value: "asc", label: "Ascending" }, { value: "desc", label: "Descending" }];

export function colOptions(w, { none } = {}) {
    const opts = pickable(w).map(c => ({ value: c.name, label: c.computed ? `ƒ ${c.label}` : c.label }));
    return none ? [{ value: "", label: none }, ...opts] : opts;
}

// --- rows-of-controls pattern ------------------------------------------------

export function rowList(container, items, buildRow, { addLabel = "Add", max } = {}) {
    const addRow = item => {
        if (max && container.querySelectorAll(".ir-dlgrow").length >= max) return;
        const row = el("div", { class: "ir-dlgrow" });
        buildRow(row, item);
        row.append(el("button", {
            type: "button", class: "ir-btn ir-row-x", title: "Remove", "aria-label": "Remove row",
            onclick: () => row.remove(),
        }, "×"));
        container.append(row);
    };
    items.forEach(addRow);
    if (items.length === 0) addRow(null);
    const addButton = el("button", { type: "button", class: "ir-btn ir-add-row", onclick: () => addRow(null) }, `+ ${addLabel}`);
    return { addButton, read: () => [...container.querySelectorAll(".ir-dlgrow")].map(r => r._read()).filter(x => x != null) };
}

// --- aggregate-function select -----------------------------------------------

/// A function select slaved to a column select: the options track the column's
/// type (the server's aggregateFunctions catalog), keeping the current pick
/// when it survives the change. Wires colSel.onchange.
export function fnSelectFor(w, colSel, initialFn) {
    const fnSel = el("select", { class: "ir-select" });
    const refresh = keep => {
        const fns = colSel.value ? fnsFor(w, typeOf(w, colSel.value)) : [];
        fnSel.replaceChildren(...fns.map(f => new Option(FN_LABELS[f] ?? f, f)));
        if (keep && fns.includes(keep)) fnSel.value = keep;
    };
    colSel.onchange = () => refresh(fnSel.value);
    refresh(initialFn);
    return fnSel;
}

// --- expression-rule editor --------------------------------------------------

export function expressionEditor(w, { initial, placeholder, result, columns }) {
    const exprInp = el("textarea", {
        class: "ir-textarea", rows: result === "predicate" ? 4 : 3, spellcheck: false, placeholder,
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
        labeled("Expression", exprInp),
        el("div", { class: "ir-token-group" },
            el("span", { class: "ir-field-label" }, "Columns"),
            el("div", {}, ...availableColumns.map(c => tokenBtn(c.label, c.name)))),
        el("div", { class: "ir-token-group" },
            el("span", { class: "ir-field-label" }, "Functions"),
            el("div", {}, ...expressionFunctions(w).map(f => tokenBtn(f, `${f}(`)))),
        el("div", { class: "ir-token-group" },
            el("span", { class: "ir-field-label" }, "Conditions"),
            el("div", {}, ...conditionTokens)),
        el("p", { class: "ir-dialog-note" },
            result === "predicate"
                ? "The expression must resolve to true or false. Strings use single quotes; dates use TO_DATE('YYYY-MM-DD')."
                : "The expression must produce a number, text, or date value. Use CASE WHEN to turn conditions into values."));
    wrap._read = () => {
        const expr = exprInp.value.trim();
        if (!expr) throw new Error(result === "predicate" ? "Enter a condition expression" : "Enter an expression");
        return expr;
    };
    return wrap;
}
