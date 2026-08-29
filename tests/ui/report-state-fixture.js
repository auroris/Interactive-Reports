const layerKinds = {
    computed: ["compute", "computed"],
    filters: ["filter", "filters"],
    sorts: ["sort", "sorts"],
    breaks: ["break", "breaks"],
    aggregates: ["aggregate", "aggregates"],
    highlights: ["highlight", "highlights"],
    columns: ["select", "columns"],
    labels: ["labels", "labels"],
    formats: ["formats", "formats"],
};

export function composables(fields = {}) {
    return Object.entries(fields).flatMap(([property, value]) => {
        const spec = layerKinds[property];
        return spec && value !== undefined ? [{ kind: spec[0], [spec[1]]: value }] : [];
    });
}

export function reportState(input = {}, shape = null, terminal = {}) {
    const tables = {
        base: { from: "definition", composables: composables(input) },
    };
    let activeTable = "base";
    if (shape) {
        activeTable = shape.kind === "group" ? "groupBy" : shape.kind;
        tables[activeTable] = {
            from: "base",
            composables: [shape, ...composables(terminal)],
        };
    }
    return { activeTable, tables };
}

export function composableOf(doc, kind, tableId = doc.activeTable) {
    return doc.tables?.[tableId]?.composables?.find(item => item.kind === kind);
}

export function sourceComposableOf(doc, kind) {
    return composableOf(doc, kind, "base");
}
