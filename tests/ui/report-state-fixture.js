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

export function composables(layer = {}) {
    return Object.entries(layer).flatMap(([property, value]) => {
        const spec = layerKinds[property];
        return spec && value !== undefined ? [{ kind: spec[0], [spec[1]]: value }] : [];
    });
}

export function reportState(layer = {}, stage = null) {
    const tables = {
        base: { from: "definition", composables: composables(layer) },
    };
    let activeTable = "base";
    if (stage) {
        activeTable = stage.kind === "group" ? "groupBy" : stage.kind;
        const { layer: stageLayer, ...shape } = stage;
        tables[activeTable] = {
            from: "base",
            composables: [shape, ...composables(stageLayer)],
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
