// Toolbar search. Unscoped search sets doc.search (server-side all-text-columns
// contains); scoped search compiles the raw value into a typed filter expression
// client-side and adds it as a regular filter rule.

import { popupMenu } from "../core/menu.js";
import { pickable, typeOf, labelOf } from "./schema.js";
import { scopedSearchExpression, sourceLayer } from "./state.js";

export function doSearch(w) {
    const raw = w.els.search.value.trim();
    if (!w.searchScopeCol) {
        w.applyOrBanner(d => { d.search = raw; });
        return;
    }
    if (!raw) return;
    const col = w.searchScopeCol;
    const type = typeOf(w, col);
    let expr;
    try { expr = scopedSearchExpression(col, type, raw); }
    catch (error) { w.showError(error); return; }
    w.els.search.value = "";
    w.applyOrBanner(d => { (sourceLayer(d).filters ??= []).push({ enabled: true, expr }); });
}

export function openSearchScopeMenu(w, anchor) {
    const searchableColumns = pickable(w).filter(c => ["text", "number", "date", "bool"].includes(c.type));
    popupMenu(anchor, [
        { label: "All Text Columns", checked: !w.searchScopeCol, onPick: () => setSearchScope(w, null) },
        "-",
        ...searchableColumns.map(c => ({ label: c.label, checked: w.searchScopeCol === c.name, onPick: () => setSearchScope(w, c.name) })),
    ]);
}

function setSearchScope(w, col) {
    w.searchScopeCol = col;
    w.els.search.placeholder = col ? `Search: ${labelOf(w, col)}` : "Search";
    w.els.search.setAttribute("aria-label", w.els.search.placeholder);
    w.els.search.focus();
}
