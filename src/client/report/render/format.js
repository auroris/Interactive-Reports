// Scalar formatting shared by grid cells, renderer text, aggregates, and chart
// data. Int64 and decimal values arrive as invariant strings so none of these
// paths need to round-trip an exact database value through JavaScript Number.

import Big from "big.js";

/// Format masks are a closed token vocabulary per column type. Legacy number
/// tokens remain valid report-document data even as the chooser grows.
export const NUMBER_MASKS = [
    { value: "integer", label: "Number: 1,235" },
    { value: "decimal1", label: "Number: 1,234.6" },
    { value: "decimal2", label: "Number: 1,234.57" },
    { value: "decimal3", label: "Number: 1,234.568" },
    { value: "decimal4", label: "Number: 1,234.5679" },
    { value: "plain", label: "Plain: 1234.57" },
    { value: "currency:CAD", label: "Currency: CAD" },
    { value: "currency:USD", label: "Currency: USD" },
    { value: "currency:EUR", label: "Currency: EUR" },
    { value: "currency:GBP", label: "Currency: GBP" },
    { value: "currency:JPY", label: "Currency: JPY" },
    { value: "percent0", label: "Percent: 12%" },
    { value: "percent1", label: "Percent: 12.3%" },
    { value: "percent2", label: "Percent: 12.35%" },
];

export const DATE_MASKS = [
    { value: "date", label: "2026-08-07" },
    { value: "datetime", label: "2026-08-07 14:30" },
    { value: "datetimeSeconds", label: "2026-08-07 14:30:45" },
    { value: "time", label: "2:30 PM" },
    { value: "timeSeconds", label: "2:30:45 PM" },
    { value: "dateMedium", label: "Aug 7, 2026" },
    { value: "dateLong", label: "August 7, 2026" },
    { value: "dateTimeMedium", label: "Aug 7, 2026, 2:30 PM" },
    { value: "dateTimeLong", label: "August 7, 2026, 2:30:45 PM" },
];

const CURRENCY_DIGITS = { CAD: 2, USD: 2, EUR: 2, GBP: 2, JPY: 0 };

export function masksFor(type) {
    return type === "number" ? NUMBER_MASKS : type === "date" ? DATE_MASKS : [];
}

// All number-like column values, whether legacy JSON numbers or exact JSON strings,
// enter one arbitrary-precision representation before comparison or formatting.
export function parseReportNumber(value) {
    if (typeof value !== "number" && typeof value !== "string" && typeof value !== "bigint") return null;
    if (typeof value === "number" && !Number.isFinite(value)) return null;
    try { return new Big(String(value).trim()); }
    catch { return null; }
}

export function hasFraction(value) {
    const number = parseReportNumber(value);
    return number ? !number.mod(1).eq(0) : false;
}

function fixedParts(number, fractionDigits) {
    const fixed = number.toFixed(fractionDigits, Big.roundHalfUp);
    const negative = fixed.startsWith("-");
    const digits = negative ? fixed.slice(1) : fixed;
    const [integer, fraction = ""] = digits.split(".");
    return {
        negative: negative && !/^0(?:\.0*)?$/.test(digits),
        integer,
        fraction,
    };
}

const integerFormat = new Intl.NumberFormat(undefined, { useGrouping: true, maximumFractionDigits: 0 });
const decimalSeparator = new Intl.NumberFormat(undefined, {
    minimumFractionDigits: 1,
    maximumFractionDigits: 1,
}).formatToParts(1.1).find(part => part.type === "decimal")?.value ?? ".";
const digitFormatter = new Intl.NumberFormat(undefined, { useGrouping: false, maximumFractionDigits: 0 });
const localizedDigits = Array.from({ length: 10 }, (_, digit) => digitFormatter.format(digit));
const localizeDigits = value => [...value].map(digit => localizedDigits[+digit]).join("");
const numericPartTypes = new Set(["integer", "group", "decimal", "fraction", "nan", "infinity"]);

function localizedMagnitude(parts, grouping) {
    const integer = grouping ? integerFormat.format(BigInt(parts.integer)) : parts.integer;
    const fraction = grouping ? localizeDigits(parts.fraction) : parts.fraction;
    return fraction ? `${integer}${grouping ? decimalSeparator : "."}${fraction}` : integer;
}

// Obtain signs, spacing, currency placement, and percent placement from Intl, but
// replace its numeric run with our exact string. Intl never receives the real value.
function decorateNumber(numberText, negative, style, currency, fractionDigits) {
    const options = { style, minimumFractionDigits: fractionDigits, maximumFractionDigits: fractionDigits };
    if (currency) options.currency = currency;
    const parts = new Intl.NumberFormat(undefined, options).formatToParts(negative ? -1 : 1);
    let inserted = false;
    let result = "";
    for (const part of parts) {
        if (numericPartTypes.has(part.type)) {
            if (!inserted) result += numberText;
            inserted = true;
        } else {
            result += part.value;
        }
    }
    return result;
}

function exactNumber(value, { minimum = 0, maximum = minimum, grouping = true, style = "decimal", currency = null, scale = 0 } = {}) {
    const number = parseReportNumber(value);
    if (!number) return null;
    const adjusted = scale
        ? number.times(new Big(10).pow(scale))
        : number;
    const parts = fixedParts(adjusted, maximum);
    while (parts.fraction.length > minimum && parts.fraction.endsWith("0"))
        parts.fraction = parts.fraction.slice(0, -1);
    const magnitude = localizedMagnitude(parts, grouping);
    if (!grouping && style === "decimal") return `${parts.negative ? "-" : ""}${magnitude}`;
    return decorateNumber(magnitude, parts.negative, style, currency, parts.fraction.length);
}

/// Dates travel as session-local text (YYYY-MM-DD[ T]HH:MM:SS…). Parse the parts
/// and build a local Date. new Date("YYYY-MM-DD") would read UTC and can shift a day.
const parseDateText = value => {
    const m = /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2}))?)?/.exec(String(value));
    if (!m) return null;
    return {
        date: new Date(+m[1], +m[2] - 1, +m[3], +(m[4] ?? 0), +(m[5] ?? 0), +(m[6] ?? 0)),
        text: `${m[1]}-${m[2]}-${m[3]}`,
        time: `${m[4] ?? "00"}:${m[5] ?? "00"}`,
        timeSeconds: `${m[4] ?? "00"}:${m[5] ?? "00"}:${m[6] ?? "00"}`,
    };
};

export function applyMask(value, type, mask) {
    if (type === "number") {
        const fixed = /^decimal([1-4])$/.exec(mask);
        if (fixed) return exactNumber(value, { minimum: +fixed[1], maximum: +fixed[1] });
        const currency = /^currency:([A-Z]{3})$/.exec(mask);
        if (currency && Object.hasOwn(CURRENCY_DIGITS, currency[1])) {
            const digits = CURRENCY_DIGITS[currency[1]];
            try {
                return exactNumber(value, {
                    minimum: digits,
                    maximum: digits,
                    style: "currency",
                    currency: currency[1],
                });
            } catch (error) {
                if (!(error instanceof RangeError)) throw error;
                return null;
            }
        }
        const percent = /^percent([0-2])$/.exec(mask);
        if (percent) {
            const digits = +percent[1];
            return exactNumber(value, { minimum: digits, maximum: digits, style: "percent", scale: 2 });
        }
        switch (mask) {
            case "integer": return exactNumber(value, { maximum: 0 });
            case "plain": return exactNumber(value, { minimum: 2, maximum: 2, grouping: false });
        }
        return null;
    }

    if (type === "date") {
        const parsed = parseDateText(value);
        if (!parsed) return null;
        switch (mask) {
            case "date": return parsed.text;
            case "datetime": return `${parsed.text} ${parsed.time}`;
            case "datetimeSeconds": return `${parsed.text} ${parsed.timeSeconds}`;
            case "time": return parsed.date.toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" });
            case "timeSeconds": return parsed.date.toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit", second: "2-digit" });
            case "dateMedium":
                return parsed.date.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
            case "dateLong":
                return parsed.date.toLocaleDateString(undefined, { year: "numeric", month: "long", day: "numeric" });
            case "dateTimeMedium":
                return parsed.date.toLocaleString(undefined, { year: "numeric", month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
            case "dateTimeLong":
                return parsed.date.toLocaleString(undefined, { year: "numeric", month: "long", day: "numeric", hour: "numeric", minute: "2-digit", second: "2-digit" });
        }
    }
    return null;
}

/// decimal: another value in this result column has a fractional component, so
/// whole values still render consistently as decimals.
export function formatValue(value, type, decimal = false, mask = null) {
    if (value === null || value === undefined) return "";
    if (mask) {
        const masked = applyMask(value, type, mask);
        if (masked !== null) return masked;
    }
    if (typeof value === "boolean") return value ? "true" : "false";
    if (type === "number") {
        const number = parseReportNumber(value);
        if (number) {
            if (!decimal && number.mod(1).eq(0))
                return number.toFixed(0);
            return exactNumber(value, { minimum: 2, maximum: 2 });
        }
    }
    if (type === "date") {
        const text = String(value);
        return text.endsWith("T00:00:00") ? text.slice(0, 10) : text.replace("T", " ");
    }
    return String(value);
}

export function formatAgg(value, type = "number", mask = null) {
    if (value === null || value === undefined) return "—";
    if (mask) {
        const masked = applyMask(value, type, mask);
        if (masked !== null) return masked;
    }
    if (type === "number") return exactNumber(value, { maximum: 2 }) ?? String(value);
    return formatValue(value, type);
}

export function formatInteger(value) {
    return exactNumber(value, { maximum: 0 }) ?? String(value);
}

export const FN_LABELS = {
    sum: "Sum", avg: "Avg", min: "Min", max: "Max",
    count: "Count", countDistinct: "Count Distinct",
};

export const FN_ORDER = ["sum", "avg", "min", "max", "count", "countDistinct"];
