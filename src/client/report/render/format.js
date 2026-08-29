// Scalar formatting shared by grid cells, renderer text, aggregates, and chart
// data. Int64 and decimal values arrive as invariant strings so none of these
// paths need to round-trip an exact database value through JavaScript Number.

import Big from "big.js";
import { resolveLocale, translate } from "../../core/localization.js";

/// Format masks are a closed token vocabulary per column type. Legacy number
/// tokens remain valid report-document data even as the chooser grows.
export const NUMBER_MASKS = [
    { value: "integer", key: "format.number", sample: "1234.6" },
    { value: "decimal1", key: "format.number", sample: "1234.56" },
    { value: "decimal2", key: "format.number", sample: "1234.567" },
    { value: "decimal3", key: "format.number", sample: "1234.5678" },
    { value: "decimal4", key: "format.number", sample: "1234.56789" },
    { value: "plain", key: "format.plain", sample: "1234.567" },
    { value: "currency:CAD", key: "format.currency", currency: "CAD" },
    { value: "currency:USD", key: "format.currency", currency: "USD" },
    { value: "currency:EUR", key: "format.currency", currency: "EUR" },
    { value: "currency:GBP", key: "format.currency", currency: "GBP" },
    { value: "currency:JPY", key: "format.currency", currency: "JPY" },
    { value: "percent0", key: "format.percent", sample: "0.123456" },
    { value: "percent1", key: "format.percent", sample: "0.123456" },
    { value: "percent2", key: "format.percent", sample: "0.123456" },
];

export const DATE_MASKS = [
    { value: "date" },
    { value: "datetime" },
    { value: "datetimeSeconds" },
    { value: "time" },
    { value: "timeSeconds" },
    { value: "dateMedium" },
    { value: "dateLong" },
    { value: "dateTimeMedium" },
    { value: "dateTimeLong" },
];

const CURRENCY_DIGITS = { CAD: 2, USD: 2, EUR: 2, GBP: 2, JPY: 0 };

export function masksFor(type, context = null) {
    if (type === "number") return NUMBER_MASKS.map(mask => ({
        value: mask.value,
        label: translate(context, mask.key, mask.currency
            ? { currency: mask.currency }
            : { example: applyMask(mask.sample, "number", mask.value, context) }),
    }));
    if (type === "date") return DATE_MASKS.map(mask => ({
        value: mask.value,
        label: applyMask("2026-08-07T14:30:45", "date", mask.value, context),
    }));
    return [];
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

const localeParts = new Map();
function numberLocaleParts(locale) {
    let parts = localeParts.get(locale);
    if (!parts) {
        const digitFormatter = new Intl.NumberFormat(locale, { useGrouping: false, maximumFractionDigits: 0 });
        parts = {
            integerFormat: new Intl.NumberFormat(locale, { useGrouping: true, maximumFractionDigits: 0 }),
            decimalSeparator: new Intl.NumberFormat(locale, {
                minimumFractionDigits: 1,
                maximumFractionDigits: 1,
            }).formatToParts(1.1).find(part => part.type === "decimal")?.value ?? ".",
            digits: Array.from({ length: 10 }, (_, digit) => digitFormatter.format(digit)),
        };
        localeParts.set(locale, parts);
    }
    return parts;
}
const numericPartTypes = new Set(["integer", "group", "decimal", "fraction", "nan", "infinity"]);

function localizedMagnitude(parts, grouping, locale) {
    const formats = numberLocaleParts(locale);
    const integer = grouping ? formats.integerFormat.format(BigInt(parts.integer)) : parts.integer;
    const fraction = grouping
        ? [...parts.fraction].map(digit => formats.digits[+digit]).join("")
        : parts.fraction;
    return fraction ? `${integer}${grouping ? formats.decimalSeparator : "."}${fraction}` : integer;
}

// Intl formatter construction dominates per-cell formatting cost (an order of
// magnitude over formatting itself), so instances are cached per shape. The
// closed mask vocabulary keeps both caches tiny.
const decorationFormats = new Map();
function decorationFormat(locale, style, currency, fractionDigits) {
    const key = `${locale}:${style}:${currency ?? ""}:${fractionDigits}`;
    let format = decorationFormats.get(key);
    if (!format) {
        const options = { style, minimumFractionDigits: fractionDigits, maximumFractionDigits: fractionDigits };
        if (currency) options.currency = currency;
        format = new Intl.NumberFormat(locale, options);
        decorationFormats.set(key, format);
    }
    return format;
}

// Obtain signs, spacing, currency placement, and percent placement from Intl, but
// replace its numeric run with our exact string. Intl never receives the real value.
function decorateNumber(numberText, negative, locale, style, currency, fractionDigits) {
    const parts = decorationFormat(locale, style, currency, fractionDigits).formatToParts(negative ? -1 : 1);
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

function exactNumber(value, { minimum = 0, maximum = minimum, grouping = true, style = "decimal", currency = null, scale = 0 } = {}, context = null) {
    const number = parseReportNumber(value);
    if (!number) return null;
    const adjusted = scale
        ? number.times(new Big(10).pow(scale))
        : number;
    const parts = fixedParts(adjusted, maximum);
    while (parts.fraction.length > minimum && parts.fraction.endsWith("0"))
        parts.fraction = parts.fraction.slice(0, -1);
    const locale = resolveLocale(context);
    const magnitude = localizedMagnitude(parts, grouping, locale);
    if (!grouping && style === "decimal") return `${parts.negative ? "-" : ""}${magnitude}`;
    return decorateNumber(magnitude, parts.negative, locale, style, currency, parts.fraction.length);
}

const LOCALIZED_DATE_MASKS = {
    time: { hour: "numeric", minute: "2-digit" },
    timeSeconds: { hour: "numeric", minute: "2-digit", second: "2-digit" },
    dateMedium: { year: "numeric", month: "short", day: "numeric" },
    dateLong: { year: "numeric", month: "long", day: "numeric" },
    dateTimeMedium: { year: "numeric", month: "short", day: "numeric", hour: "numeric", minute: "2-digit" },
    dateTimeLong: { year: "numeric", month: "long", day: "numeric", hour: "numeric", minute: "2-digit", second: "2-digit" },
};
const dateMaskFormats = new Map();
function dateMaskFormat(locale, mask) {
    const key = `${locale}:${mask}`;
    let format = dateMaskFormats.get(key);
    if (!format) {
        format = new Intl.DateTimeFormat(locale, LOCALIZED_DATE_MASKS[mask]);
        dateMaskFormats.set(key, format);
    }
    return format;
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

export function applyMask(value, type, mask, context = null) {
    if (type === "number") {
        const fixed = /^decimal([1-4])$/.exec(mask);
        if (fixed) return exactNumber(value, { minimum: +fixed[1], maximum: +fixed[1] }, context);
        const currency = /^currency:([A-Z]{3})$/.exec(mask);
        if (currency && Object.hasOwn(CURRENCY_DIGITS, currency[1])) {
            const digits = CURRENCY_DIGITS[currency[1]];
            try {
                return exactNumber(value, {
                    minimum: digits,
                    maximum: digits,
                    style: "currency",
                    currency: currency[1],
                }, context);
            } catch (error) {
                if (!(error instanceof RangeError)) throw error;
                return null;
            }
        }
        const percent = /^percent([0-2])$/.exec(mask);
        if (percent) {
            const digits = +percent[1];
            return exactNumber(value, { minimum: digits, maximum: digits, style: "percent", scale: 2 }, context);
        }
        switch (mask) {
            case "integer": return exactNumber(value, { maximum: 0 }, context);
            case "plain": return exactNumber(value, { minimum: 2, maximum: 2, grouping: false }, context);
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
        }
        if (Object.hasOwn(LOCALIZED_DATE_MASKS, mask))
            return dateMaskFormat(resolveLocale(context), mask).format(parsed.date);
    }
    return null;
}

/// decimal: another value in this result column has a fractional component, so
/// whole values still render consistently as decimals.
export function formatValue(value, type, decimal = false, mask = null, context = null) {
    if (value === null || value === undefined) return "";
    if (mask) {
        const masked = applyMask(value, type, mask, context);
        if (masked !== null) return masked;
    }
    if (typeof value === "boolean") return translate(context, value ? "format.true" : "format.false");
    if (type === "number") {
        const number = parseReportNumber(value);
        if (number) {
            if (!decimal && number.mod(1).eq(0))
                return number.toFixed(0);
            return exactNumber(value, { minimum: 2, maximum: 2 }, context);
        }
    }
    if (type === "date") {
        const text = String(value);
        return text.endsWith("T00:00:00") ? text.slice(0, 10) : text.replace("T", " ");
    }
    return String(value);
}

export function formatAgg(value, type = "number", mask = null, context = null) {
    if (value === null || value === undefined) return "—";
    if (mask) {
        const masked = applyMask(value, type, mask, context);
        if (masked !== null) return masked;
    }
    if (type === "number") return exactNumber(value, { maximum: 2 }, context) ?? String(value);
    return formatValue(value, type, false, null, context);
}

export function formatInteger(value, context = null) {
    return exactNumber(value, { maximum: 0 }, context) ?? String(value);
}

export const FN_LABELS = {
    sum: "Sum", avg: "Avg", median: "Median", min: "Min", max: "Max",
    count: "Count", countDistinct: "Count Distinct",
    // Spread totals for a group-layer computed cell aren't any one aggregate —
    // the expression re-evaluates over the totals grouping.
    total: "Total",
};

export function fnLabel(context, fn) {
    const key = `aggregate.${fn}`;
    const label = translate(context, key);
    return label === key ? FN_LABELS[fn] ?? fn : label;
}

export const FN_ORDER = ["sum", "avg", "median", "min", "max", "count", "countDistinct", "total"];
