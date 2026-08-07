// Value formatting for grid cells, aggregates, and chart data — pure functions,
// no DOM and no widget state.

/// Format masks are a closed token vocabulary per column type (the house rule:
/// validated named tokens over freeform mask strings). Unknown tokens and values
/// a mask cannot digest fall through to the default rendering — a mask is a
/// lens, never a gate.
export const NUMBER_MASKS = [
    { value: "integer", label: "1,235" },
    { value: "decimal2", label: "1,234.57" },
    { value: "decimal4", label: "1,234.5679" },
    { value: "plain", label: "1234.57" },
];

export const DATE_MASKS = [
    { value: "date", label: "2026-08-07" },
    { value: "datetime", label: "2026-08-07 14:30" },
    { value: "dateMedium", label: "Aug 7, 2026" },
    { value: "dateLong", label: "August 7, 2026" },
];

export function masksFor(type) {
    return type === "number" ? NUMBER_MASKS : type === "date" ? DATE_MASKS : [];
}

/// Dates travel as session-local text (YYYY-MM-DD[ T]HH:MM:SS…). Parse the parts
/// and build a local Date — new Date("YYYY-MM-DD") would read it as UTC midnight
/// and shift a day in western timezones.
const parseDateText = value => {
    const m = /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2}))?)?/.exec(String(value));
    if (!m) return null;
    return {
        date: new Date(+m[1], +m[2] - 1, +m[3], +(m[4] ?? 0), +(m[5] ?? 0), +(m[6] ?? 0)),
        text: `${m[1]}-${m[2]}-${m[3]}`,
        time: `${m[4] ?? "00"}:${m[5] ?? "00"}`,
    };
};

function applyMask(v, type, mask) {
    if (type === "number" && typeof v === "number") {
        switch (mask) {
            case "integer": return Math.round(v).toLocaleString();
            case "decimal2": return v.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
            case "decimal4": return v.toLocaleString(undefined, { minimumFractionDigits: 4, maximumFractionDigits: 4 });
            case "plain": return v.toFixed(2);
        }
        return null;
    }
    if (type === "date") {
        const parsed = parseDateText(v);
        if (!parsed) return null;
        switch (mask) {
            case "date": return parsed.text;
            case "datetime": return `${parsed.text} ${parsed.time}`;
            case "dateMedium":
                return parsed.date.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
            case "dateLong":
                return parsed.date.toLocaleDateString(undefined, { year: "numeric", month: "long", day: "numeric" });
        }
    }
    return null;
}

/// decimal: the column is known to carry fractional values, so whole numbers in it
/// still format as decimals (14474 → 14,474.00) instead of looking like ids.
export function formatValue(v, type, decimal = false, mask = null) {
    if (v === null || v === undefined) return "";
    if (mask) {
        const masked = applyMask(v, type, mask);
        if (masked !== null) return masked;
    }
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
