// Shared authoring-dialog controls: column option lists, repeatable rows, aggregate-function
// selectors, color toggles, and the expression-rule editor.
// Every dialog applies through w.apply(mutate): the widget clones the state doc, mutates,
// re-queries, and rolls back on failure, so a validation problem surfaces inside the dialog
// and the grid never breaks.

import { el, labeled, sel } from "../../core/dom.js";
import { pickable, typeOf, fnsFor, expressionFunctions } from "../schema.js";
import { fnLabel } from "../render/format.js";

/**
 * Builds localized ascending and descending choices.
 *
 * @param {object} w - The report controller providing localization.
 * @returns {Array<{value: string, label: string}>} Sort-direction options in ascending-first order.
 */
export const dirOptions = w => [
    { value: "asc", label: w.t("sort.ascending") },
    { value: "desc", label: w.t("sort.descending") },
];
/**
 * Builds localized provider-default, nulls-first, and nulls-last choices.
 *
 * @param {object} w - The report controller providing localization.
 * @returns {Array<{value: string, label: string}>} Null-ordering options with the empty protocol token first.
 */
export const nullsOptions = w => [
    { value: "", label: w.t("sort.nullDefault") },
    { value: "first", label: w.t("sort.nullFirst") },
    { value: "last", label: w.t("sort.nullLast") },
];

/**
 * Projects schema columns into select options, marking computed columns with a function symbol.
 *
 * @param {object} w - The report controller whose input columns are the fallback source.
 * @param {{none?: string, columns?: Array<object>}} [options={}] - Optional empty-choice label and explicit column universe.
 * @returns {Array<{value: string, label: string}>} Select options in column order, optionally prefixed by an empty choice.
 */
export function colOptions(w, { none, columns } = {}) {
    const opts = (columns ?? pickable(w)).map(c => ({ value: c.name, label: c.computed ? `ƒ ${c.label}` : c.label }));
    return none ? [{ value: "", label: none }, ...opts] : opts;
}

/**
 * Compact, visible labels for controls that share one repeatable dialog row.
 *
 * @param {string} text - The compact visible label.
 * @param {Element} control - The form control associated with the generated label or field row.
 * @returns {HTMLLabelElement} A detached label containing caption and control.
 */
export function rowField(text, control) {
    return el("label", { class: "ir-row-field" },
        el("span", { class: "ir-field-label" }, text), control);
}

/**
 * Native grouping for related rows of controls.
 *
 * @param {string} text - The fieldset legend.
 * @param {...(Node|string|Array<Node|string>)} children - Related controls and explanatory content.
 * @returns {HTMLFieldSetElement} A detached fieldset with its legend.
 */
export function fieldGroup(text, ...children) {
    return el("fieldset", { class: "ir-fieldset" },
        el("legend", { class: "ir-field-label" }, text), ...children);
}

/**
 * Checkbox-gated color input shared by presentation and highlight editors. A null read means the color
 * is disabled; write() lets multi-column editors load another staged value without rebuilding the
 * control.
 *
 * @param {string} label - The visible checkbox label.
 * @param {string|null|undefined} initial - The enabled initial CSS color; a falsy value starts disabled.
 * @param {string} fallback - The color-input value used while the setting is disabled.
 * @param {Element|object|string|null} [context=null] - The localization context for the color input's accessible name.
 * @returns {{node: HTMLDivElement, read: Function, write: Function}} A detached control plus read and staged-value update operations.
 *
 * Side effects: creates detached controls and handlers; `write` mutates their current values.
 */
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
        /** Returns the selected color, or `null` when the checkbox disables it. */
        read: () => enabled.checked ? input.value : null,
        /**
         * Loads a staged color into the checkbox and native color input.
         * @param {string|null|undefined} value - The staged color, or a falsy value to disable it.
         * @returns {void} No value.
         */
        write(value) {
            enabled.checked = !!value;
            if (value) input.value = value;
        },
    };
}

/**
 * Builds a repeatable list of dialog rows with removal, addition, a maximum count, and filtered reading.
 *
 * @param {Element} container - The initially empty row container to populate and later read.
 * @param {Array<unknown>} items - Initial item values passed to `buildRow`.
 * @param {Function} buildRow - Configures each row and assigns its `_read()` operation.
 * @param {{addLabel?: string|null, max?: number, context?: object|null}} [options={}] - Add-button text, optional row cap, and localization context.
 * @returns {{addButton: HTMLButtonElement, read: Function}} The detached add button and an operation that reads non-null row values.
 *
 * Side effects: populates `container`; add/remove controls later mutate that container.
 */
export function rowList(container, items, buildRow, { addLabel = null, max, context = null } = {}) {
    // Adds one row unless the current mounted-row count has reached the configured cap.
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

// Protocol contract: a function select slaved to a column select: the options track the
// column's type (the server's aggregateFunctions catalog), keeping the current pick when it
// survives the change. Wires the column select's change event.
/**
 * Builds an aggregate-function selector constrained by the selected column.
 *
 * @param {object} w - The report controller containing aggregate capabilities, schema types, and localization.
 * @param {HTMLSelectElement} colSel - The column selector whose value constrains the function choices.
 * @param {string} initialFn - The aggregate function preselected when the control is created.
 * @param {Array<object>|null} [columns=null] - Optional terminal column universe used before falling back to the input schema.
 * @returns {HTMLSelectElement} A detached function select kept synchronized with `colSel`.
 *
 * Side effects: registers a change listener on `colSel` and replaces function options whenever its value changes.
 */
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

/**
 * The aggregate function/column row shared by report aggregates and grouped view values. One
 * implementation keeps option behavior, labels, and reading semantics aligned across every caller.
 *
 * @param {object} w - The report controller providing capabilities, columns, and localization.
 * @param {Array<object>|null|undefined} initial - Existing `{col, fn}` aggregate rules.
 * @param {{addLabel?: string|null, columns?: Array<object>|null}} [options={}] - Add-button label and explicit column universe.
 * @returns {{container: HTMLDivElement, list: object}} The row container and its add/read controller.
 *
 * Side effects: creates detached controls and registers column/function synchronization handlers.
 */
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

/**
 * Encodes a column name as portable expression syntax, quoting names that are not ordinary non-keyword identifiers.
 *
 * @param {string} name - The exact logical column identifier.
 * @returns {string} The bare identifier or a backtick-quoted token with embedded backticks doubled.
 */
export function expressionColumnToken(name) {
    const ordinary = /^[A-Za-z_][A-Za-z0-9_$#]*$/.test(name);
    const keyword = /^(CASE|WHEN|THEN|ELSE|END|AND|OR|NOT|IS|NULL|BETWEEN)$/i.test(name);
    return ordinary && !keyword ? name : `\`${name.replaceAll("`", "``")}\``;
}

/**
 * Builds the shared expression textarea and token palettes for predicate or value rules.
 *
 * @param {object} w - The report controller providing columns, function capabilities, localization, and validation text.
 * @param {{initial?: string, placeholder?: string, result: 'predicate'|'value', columns?: Array<object>}} options - Initial text, expected result kind, and optional column universe.
 * @returns {HTMLDivElement} A detached editor whose `_read()` method returns trimmed non-empty expression text and whose `_set()` method replaces it.
 *
 * Side effects: creates controls and token-button handlers that edit and focus the textarea.
 */
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
    /** Returns trimmed expression text or throws the localized required-value error. */
    wrap._read = () => {
        const expr = exprInp.value.trim();
        if (!expr) throw new Error(w.t(result === "predicate" ? "expression.enterCondition" : "expression.enterExpression"));
        return expr;
    };
    /** Replaces the complete expression and returns focus to its textarea. */
    wrap._set = value => {
        exprInp.value = String(value ?? "");
        exprInp.focus();
        exprInp.setSelectionRange(exprInp.value.length, exprInp.value.length);
    };
    return wrap;
}
