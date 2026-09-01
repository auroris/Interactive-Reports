// Shared list-of-values UI for header filtering and the filter expression editor. Every request
// carries the complete current document and addresses exactly its active table and one column.

import { el } from "../core/dom.js";
import { openDialog } from "../core/dialog.js";
import { tableContext } from "./table.js";
import { expressionColumnToken } from "./dialogs/parts.js";
import { formatForColumn } from "./render/column-renderers.js";
import { formatValue, parseReportNumber } from "./render/format.js";

const LOV_SEARCH_DEBOUNCE_MS = 200;

/** Interprets only `*` and its `\*` escape in a user-entered LOV value. */
function userWildcard(value) {
    const source = String(value);
    let exact = "";
    let wildcard = false;
    for (let index = 0; index < source.length; index++) {
        const character = source[index];
        if (character === "\\" && (source[index + 1] === "*" || source[index + 1] === "\\")) {
            exact += source[++index];
        } else {
            exact += character;
            wildcard ||= character === "*";
        }
    }
    return { source, exact, wildcard };
}

const textLiteral = value => `'${String(value).replaceAll("'", "''")}'`;

/**
 * Builds a portable predicate that matches one LOV value in one current-table column.
 * Text is exact and case-insensitive; only an unescaped `*` in typed input enables a
 * partial wildcard match. Selected values always remain exact.
 *
 * @param {object} column - The current table's column metadata.
 * @param {unknown} value - The selected server-returned scalar or editable-combobox text.
 * @param {{typed?: boolean}} [options={}] - Whether `value` came from unselected user input.
 * @returns {string} A complete predicate expression suitable for a filter rule.
 */
export function valueFilterExpression(column, value, { typed = false } = {}) {
    const token = expressionColumnToken(column.name);
    if (value === null || value === undefined) return `${token} IS NULL`;
    switch (column.type) {
        case "text": {
            const pattern = typed ? userWildcard(value) : { exact: String(value), wildcard: false };
            return pattern.wildcard
                ? `WILDCARD_MATCH(${token}, ${textLiteral(pattern.source)})`
                : `LOWER(${token}) = LOWER(${textLiteral(pattern.exact)})`;
        }
        case "number": {
            const number = parseReportNumber(value);
            if (!number && typed) return "1 = 0";
            if (!number) throw new TypeError("The selected value is not a portable report number.");
            return `${token} = ${number.toFixed()}`;
        }
        case "bool": {
            if (value === true || /^(true|1|yes|on)$/i.test(String(value))) return token;
            if (value === false || /^(false|0|no|off)$/i.test(String(value))) return `NOT ${token}`;
            if (typed) return "1 = 0";
            throw new TypeError("The selected value is not a portable report boolean.");
        }
        case "date": {
            const date = /^\d{4}-\d{2}-\d{2}/.exec(String(value))?.[0];
            if (!date && typed) return "1 = 0";
            if (!date) throw new TypeError("The selected value is not an ISO report date.");
            return `DATE_TRUNC('DAY', ${token}) = TO_DATE('${date}')`;
        }
        default:
            if (typed) return "1 = 0";
            throw new TypeError("The selected column does not support a portable filter expression.");
    }
}

/** Returns the value text shown in the LOV chooser. */
function displayValue(w, column, value) {
    if (value === null || value === undefined) return w.t("lov.null");
    if (value === "") return w.t("lov.emptyValue");
    const format = formatForColumn(w, column);
    return formatValue(value, column.type, false, format?.mask, w);
}

/**
 * Opens a searchable, server-backed list for one column of the current active table.
 * Lookup text is a case-insensitive substring; wildcard rules apply only when the same
 * text is accepted as a filter or highlight value.
 *
 * @param {object} w - The report controller providing current document state and transport.
 * @param {object|string} requestedColumn - Current-table column metadata or its logical name.
 * @param {{onPick: (value: unknown, column: object, details: {typed: boolean}) => (void|Promise<void>)}} options - Selection or typed-value callback.
 * @returns {object} The dialog controller.
 */
export function lovDialog(w, requestedColumn, { onPick }) {
    const ctx = tableContext(w);
    const column = typeof requestedColumn === "string"
        ? ctx.columns.find(candidate => candidate.name.toLowerCase() === requestedColumn.toLowerCase())
        : requestedColumn;
    if (!column) throw new TypeError("The LOV column is not available in the current table.");

    const search = el("input", {
        class: "ir-input ir-input-wide",
        type: "search",
        maxLength: 200,
        placeholder: w.t("lov.search"),
        "aria-label": w.t("lov.search"),
    });
    const status = el("p", { class: "ir-dialog-note ir-lov-status", "aria-live": "polite" });
    const help = el("p", { class: "ir-dialog-note" }, w.t("lov.matchHelp"));
    const items = el("div", { class: "ir-lov-items", role: "listbox" });
    let timer = null;
    let request = null;
    let sequence = 0;
    let dlg;

    const load = async () => {
        const current = ++sequence;
        request?.abort();
        request = new AbortController();
        dlg?.setError(null);
        status.textContent = w.t("lov.loading");
        items.replaceChildren();
        try {
            const result = await w.getListOfValues({
                document: w.serialize(),
                table: ctx.tableId ?? "definition",
                column: column.name,
                search: search.value,
                signal: request.signal,
            });
            if (current !== sequence) return;
            const buttons = (result.items ?? []).map(value => el("button", {
                type: "button",
                class: "ir-lov-item",
                role: "option",
                onclick: async () => {
                    try {
                        await onPick(value, column, { typed: false });
                        dlg.close();
                    } catch (error) {
                        dlg.setError(error);
                    }
                },
            }, displayValue(w, column, value)));
            items.replaceChildren(...buttons);
            status.textContent = buttons.length === 0
                ? w.t("lov.empty")
                : result.truncated
                    ? w.t("lov.truncated")
                    : "";
        } catch (error) {
            if (error.name !== "AbortError" && current === sequence) dlg.setError(error);
        }
    };

    search.addEventListener("input", () => {
        clearTimeout(timer);
        timer = setTimeout(() => void load(), LOV_SEARCH_DEBOUNCE_MS);
    });
    dlg = openDialog({
        owner: w,
        title: w.t("lov.title", { column: column.label }),
        width: "26rem",
        build: body => body.append(search, help, status, items),
        applyLabel: w.t("lov.useTyped"),
        onApply: () => onPick(search.value, column, { typed: true }),
    });
    const close = dlg.close.bind(dlg);
    dlg.close = () => {
        clearTimeout(timer);
        request?.abort();
        close();
    };
    void load();
    return dlg;
}

/** Opens the current column's LOV and adds the selected value as an enabled filter. */
export function filterByLovDialog(w, columnName) {
    const ctx = tableContext(w);
    const column = ctx.filterColumns.find(candidate =>
        candidate.name.toLowerCase() === String(columnName).toLowerCase());
    return lovDialog(w, column, {
        onPick: (value, selectedColumn, details) => w.apply(document => {
            ctx.edit(document, "filter", node => {
                node.filters ??= [];
                node.filters.push({
                    expr: valueFilterExpression(selectedColumn, value, details),
                    enabled: true,
                });
            });
        }),
    });
}
