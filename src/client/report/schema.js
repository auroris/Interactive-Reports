// Column metadata resolution over the widget's loaded schema and last result.
// Free functions over the widget instance `w`, like the render and dialog
// modules — the widget holds the data, these answer questions about it.

/// Server column metadata with the report's own display labels applied. Labels
/// are client-side presentation: the server sends real names and neutral labels;
/// doc.labels (seeded from the definition via the default report) wins here.
export function pickable(w) {
    const columns = w.lastResult?.availableColumns ?? w.schema?.columns ?? [];
    const labels = w.doc?.labels;
    if (!labels) return columns;
    return columns.map(c => labels[c.name] ? { ...c, label: labels[c.name] } : c);
}

export function typeOf(w, name) { return pickable(w).find(c => c.name === name)?.type ?? "other"; }
export function labelOf(w, name) { return pickable(w).find(c => c.name === name)?.label ?? name; }

export function fnsFor(w, type) {
    const catalog = w.schema?.capabilities?.aggregateFunctions ?? {};
    return catalog[type] ?? catalog.other ?? [];
}

/// Chart metrics must come out numeric, so the server advertises a stricter set.
export function chartFnsFor(w, type) {
    const catalog = w.schema?.capabilities?.chartAggregateFunctions ?? {};
    return catalog[type] ?? catalog.other ?? [];
}

export function expressionFunctions(w) { return w.schema?.capabilities?.expressionFunctions ?? []; }

export function visibleColumnNames(w) {
    if (w.doc?.columns?.length) return [...w.doc.columns];
    return pickable(w).map(c => c.name);
}
