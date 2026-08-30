// Toolbar search. Unscoped search sets doc.search (server-side all-text-columns
// contains); scoped search compiles the raw value into a typed filter expression
// client-side and adds it to the active table as a regular exported filter rule.

import { popupMenu } from "../core/menu.js";
import { editTerminalComposable, scopedSearchExpression } from "./state.js";
import { tableContext } from "./table.js";

const activeColumn = (w, name) => tableContext(w).columns.find(column =>
    column.name.toLowerCase() === String(name).toLowerCase()) ?? null;

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

export function openSearchScopeMenu(w, anchor) {
    const searchableColumns = tableContext(w).filterColumns
        .filter(c => ["text", "number", "date", "bool"].includes(c.type));
    popupMenu(anchor, [
        { label: w.t("menu.allTextColumns"), checked: !w.searchScopeCol, onPick: () => setSearchScope(w, null) },
        "-",
        ...searchableColumns.map(c => ({ label: c.label, checked: w.searchScopeCol === c.name, onPick: () => setSearchScope(w, c.name) })),
    ]);
}

function setSearchScope(w, col) {
    w.searchScopeCol = col;
    const label = activeColumn(w, col)?.label ?? col;
    w.els.search.placeholder = col
        ? w.t("toolbar.searchColumn", { column: label })
        : w.t("toolbar.search");
    w.els.search.setAttribute("aria-label", w.els.search.placeholder);
    w.els.search.focus();
}
