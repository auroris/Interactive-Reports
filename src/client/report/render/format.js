// Value formatting for grid cells, aggregates, and chart data — pure functions,
// no DOM and no widget state.

/// decimal: the column is known to carry fractional values, so whole numbers in it
/// still format as decimals (14474 → 14,474.00) instead of looking like ids.
export function formatValue(v, type, decimal = false) {
    if (v === null || v === undefined) return "";
    if (typeof v === "boolean") return v ? "true" : "false";
    if (type === "number" && typeof v === "number") {
        if (!decimal && Number.isInteger(v)) return String(v);
        return v.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    }
    if (type === "date") {
        const s = String(v);
        return s.endsWith("T00:00:00") ? s.slice(0, 10) : s.replace("T", " ");
    }
    return String(v);
}

export function formatAgg(v) {
    if (v === null || v === undefined) return "—";
    return typeof v === "number" ? v.toLocaleString(undefined, { maximumFractionDigits: 2 }) : String(v);
}

export const FN_LABELS = {
    sum: "Sum", avg: "Avg", min: "Min", max: "Max",
    count: "Count", countDistinct: "Count Distinct",
};

export const FN_ORDER = ["sum", "avg", "min", "max", "count", "countDistinct"];
