// Toolbar search. Unscoped search sets `doc.search`, which asks the server to search all text
// columns; scoped search compiles the raw value into a typed filter
// expression client-side and adds it to the active table as a regular exported filter rule.

import { popupMenu } from "../core/menu.js";
import { editTerminalComposable, scopedSearchExpression } from "./state.js";
import { tableContext } from "./table.js";

/**
 * Returns the schema column selected by the current search scope.
 *
 * @param {object} w - The report controller whose active table schema will be searched.
 * @param {string|null} name - The logical column name selected as the search scope.
 * @returns {object|null} The matching active column, ignoring case, or `null`.
 */
const activeColumn = (w, name) => tableContext(w).columns.find(column =>
    column.name.toLowerCase() === String(name).toLowerCase()) ?? null;

/**
 * Applies an unscoped search or appends a typed scoped-search filter.
 *
 * @param {object} w - The report controller providing the search input, scope, and state mutation pipeline.
 * @returns {void} No value.
 *
 * Side effects: may clear the search input, mutate report state through `applyOrBanner`, run a query, or display a validation error.
 */
export function doSearch(w) {
    const raw = w.els.search.value.trim();
    if (!w.searchScopeCol) {
        w.applyOrBanner(d => { d.search = raw; });
        return;
    }
    if (!raw) return;
    const col = w.searchScopeCol;
    const type = activeColumn(w, col)?.type ?? "other";
    let expr;
    try { expr = scopedSearchExpression(col, type, raw, w); }
    catch (error) { w.showError(error); return; }
    w.els.search.value = "";
    w.applyOrBanner(d => editTerminalComposable(d, "filter", node => {
        (node.filters ??= []).push({ enabled: true, expr });
    }));
}

/**
 * Opens the search-scope menu with the columns allowed by the active schema.
 *
 * @param {object} w - The report controller providing filterable columns and localization.
 * @param {Element} anchor - The toolbar element beside which the menu opens.
 * @returns {void} No value.
 *
 * Side effects: opens a popup menu whose selections update controller state.
 */
export function openSearchScopeMenu(w, anchor) {
    const searchableColumns = tableContext(w).filterColumns
        .filter(c => ["text", "number", "date", "bool"].includes(c.type));
    popupMenu(anchor, [
        { label: w.t("menu.allTextColumns"), checked: !w.searchScopeCol, onPick: () => setSearchScope(w, null) },
        "-",
        ...searchableColumns.map(c => ({ label: c.label, checked: w.searchScopeCol === c.name, onPick: () => setSearchScope(w, c.name) })),
    ]);
}

/**
 * Updates the active search column and synchronizes the search input's localized prompt.
 *
 * @param {object} w - The report controller whose search UI state will be updated.
 * @param {string|null} col - The scoped column identifier, or `null` for all text columns.
 * @returns {void} No value.
 *
 * Side effects: updates `searchScopeCol`, input attributes, and focus. It does not run a query by itself.
 */
function setSearchScope(w, col) {
    w.searchScopeCol = col;
    const label = activeColumn(w, col)?.label ?? col;
    w.els.search.placeholder = col
        ? w.t("toolbar.searchColumn", { column: label })
        : w.t("toolbar.search");
    w.els.search.setAttribute("aria-label", w.els.search.placeholder);
    w.els.search.focus();
}
