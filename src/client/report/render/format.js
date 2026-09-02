// Exact scalar formatting shared by grid cells, renderer text, aggregates, and chart data.
// Int64 and decimal values arrive as invariant strings so none of these paths need to
// round-trip an exact database value through JavaScript Number.

import Big from "big.js";
import { resolveLocale, translate } from "../../core/localization.js";

// Format masks are Excel-style format codes entered by the report user (`#,##0.00`,
// `0.0%`, `yyyy-mm-dd hh:mm`). Anything the grammar does not understand renders nothing so
// the cell falls through to its default text.
/** Preset numeric format codes offered by the column-format editor. */
export const NUMBER_MASK_PRESETS = [
    "#,##0",
    "#,##0.0",
    "#,##0.00",
    "#,##0.000",
    "#,##0.0000",
    "0.00",
    "#,##0.00;(#,##0.00)",
    "$#,##0.00",
    "€#,##0.00",
    "£#,##0.00",
    "¥#,##0",
    "#,##0.00 \"CAD\"",
    "0%",
    "0.0%",
    "0.00%",
    "#,##0.00\"%\"",
];

/** Preset date/time format codes offered by the column-format editor. */
export const DATE_MASK_PRESETS = [
    "yyyy-mm-dd",
    "yyyy-mm-dd hh:mm",
    "yyyy-mm-dd hh:mm:ss",
    "h:mm AM/PM",
    "hh:mm:ss",
    "mm/dd/yyyy",
    "dd/mm/yyyy",
    "mmm d, yyyy",
    "mmmm d, yyyy",
    "mmm d, yyyy h:mm AM/PM",
    "dddd, mmmm d, yyyy",
];

/** Sample values rendered beside each preset so the picker documents itself. */
export const MASK_SAMPLES = { number: "1234.567", date: "2026-08-07T14:30:45" };

/** Longest format code accepted from report state; longer masks fall through to default rendering. */
export const MAX_MASK_LENGTH = 64;

/**
 * Returns the preset format codes compatible with a column's data type, each with its rendered sample.
 *
 * @param {string} type - The value or column type to classify.
 * @param {Element|object|string|null} [context=null] - The locale or DOM context used to render the examples.
 * @returns {Array<{value: string, example: string}>} Preset codes for number or date columns; other types return an empty array.
 */
export function masksFor(type, context = null) {
    const presets = type === "number" ? NUMBER_MASK_PRESETS : type === "date" ? DATE_MASK_PRESETS : [];
    return presets.map(value => ({ value, example: applyMask(MASK_SAMPLES[type], type, value, context) ?? "" }));
}

/**
 * Determines whether a mask renders a column type's sample value rather than falling through to default text.
 *
 * @param {string} type - The column type the mask is meant for.
 * @param {string|null|undefined} mask - The mask text to test.
 * @param {Element|object|string|null} [context=null] - The locale or DOM context used for rendering.
 * @returns {boolean} `true` for a blank mask or one the formatter understands.
 */
export function maskIsValid(type, mask, context = null) {
    if (typeof mask !== "string" || !mask.trim()) return true;
    if (!Object.hasOwn(MASK_SAMPLES, type)) return false;
    return applyMask(MASK_SAMPLES[type], type, mask, context) !== null;
}

/**
 * All number-like column values, whether legacy JSON numbers or exact JSON strings, enter one
 * arbitrary-precision representation before comparison or formatting.
 *
 * @param {unknown} value - A finite JavaScript number, invariant numeric string, or bigint.
 * @returns {Big|null} An arbitrary-precision value, or `null` when the input is not a valid report number.
 */
export function parseReportNumber(value) {
    if (typeof value !== "number" && typeof value !== "string" && typeof value !== "bigint") return null;
    if (typeof value === "number" && !Number.isFinite(value)) return null;
    try { return new Big(String(value).trim()); }
    catch { return null; }
}

/**
 * Determines whether an exact report number contains a non-zero fractional component.
 *
 * @param {unknown} value - The report value to inspect as an exact number.
 * @returns {boolean} Whether the parsed number has a non-zero fractional component.
 */
export function hasFraction(value) {
    const number = parseReportNumber(value);
    return number ? !number.mod(1).eq(0) : false;
}

/**
 * Rounds normalized exact-number components to the requested fractional precision.
 *
 * @param {Big} number - The exact number to round half-up.
 * @param {number} fractionDigits - The exact number of fractional digits to emit initially.
 * @returns {{negative: boolean, integer: string, fraction: string}} Sign and unsigned decimal components, with negative zero suppressed.
 */
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
/**
 * Returns the locale's decimal and grouping separators with digit-group sizes.
 *
 * @param {string} locale - The resolved locale identifier.
 * @returns {{integerFormat: Intl.NumberFormat, decimalSeparator: string, digits: Array<string>}} Cached grouping, separator, and localized-digit information.
 *
 * Side effects: populates the locale-parts cache on first use.
 */
function numberLocaleParts(locale) {
    let parts = localeParts.get(locale);
    if (!parts) {
        const digitFormatter = new Intl.NumberFormat(locale, { useGrouping: false, maximumFractionDigits: 0 });
        const integerFormat = new Intl.NumberFormat(locale, { useGrouping: true, maximumFractionDigits: 0 });
        parts = {
            integerFormat,
            decimalSeparator: new Intl.NumberFormat(locale, {
                minimumFractionDigits: 1,
                maximumFractionDigits: 1,
            }).formatToParts(1.1).find(part => part.type === "decimal")?.value ?? ".",
            groupSeparator: integerFormat.formatToParts(1234567).find(part => part.type === "group")?.value ?? ",",
            minusSign: integerFormat.formatToParts(-1).find(part => part.type === "minusSign")?.value ?? "-",
            digits: Array.from({ length: 10 }, (_, digit) => digitFormatter.format(digit)),
        };
        localeParts.set(locale, parts);
    }
    return parts;
}
/**
 * Applies locale-specific digit grouping to exact integer and fraction text.
 *
 * @param {{integer: string, fraction: string}} parts - Exact unsigned integer and fraction text.
 * @param {boolean} grouping - Whether locale-aware digit grouping should be retained.
 * @param {string} locale - The resolved locale identifier.
 * @returns {string} The localized magnitude.
 */
function localizedMagnitude(parts, grouping, locale) {
    const formats = numberLocaleParts(locale);
    const integer = grouping ? formats.integerFormat.format(BigInt(parts.integer)) : parts.integer;
    const fraction = grouping
        ? [...parts.fraction].map(digit => formats.digits[+digit]).join("")
        : parts.fraction;
    return fraction ? `${integer}${grouping ? formats.decimalSeparator : "."}${fraction}` : integer;
}

/**
 * Formats an exact report number in the locale's default decimal style without converting its
 * significant digits through floating point.
 *
 * @param {unknown} value - A report number accepted by `parseReportNumber`.
 * @param {{minimum?: number, maximum?: number}} [options={}] - The least and most fractional digits to show.
 * @param {Element|object|string|null} [context=null] - The locale or DOM context used for separators.
 * @returns {string|null} The locale-formatted exact number, or `null` when the input is not numeric.
 */
function exactNumber(value, { minimum = 0, maximum = minimum } = {}, context = null) {
    const number = parseReportNumber(value);
    if (!number) return null;
    const parts = fixedParts(number, maximum);
    while (parts.fraction.length > minimum && parts.fraction.endsWith("0"))
        parts.fraction = parts.fraction.slice(0, -1);
    const locale = resolveLocale(context);
    const magnitude = localizedMagnitude(parts, true, locale);
    return (parts.negative ? numberLocaleParts(locale).minusSign : "") + magnitude;
}

/**
 * Dates travel as session-local text (YYYY-MM-DD[ T]HH:MM:SS…). Parse the parts and build a local
 * Date. new Date("YYYY-MM-DD") would read UTC and can shift a day.
 *
 * @param {unknown} value - A value whose leading text may match `YYYY-MM-DD[ T]HH:MM[:SS]`.
 * @returns {{date: Date, text: string, time: string, timeSeconds: string}|null} Parsed local date and canonical display fragments, or `null`.
 */
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

// ---------------------------------------------------------------------------------------------
// Excel-style format codes. The grammar is the subset of Excel number and date format codes
// that survives a round trip into a workbook cell: digit placeholders, grouping, scaling
// commas, percent, quoted or escaped literals, positive;negative;zero sections, and the
// y/m/d/h/s date tokens. Parsing never touches the value; the value is formatted exactly and
// then dropped into the code's literal frame. Unknown constructs make the whole code invalid,
// and an invalid code renders nothing so the caller falls through to default text.
// ---------------------------------------------------------------------------------------------

/**
 * Splits a format code at semicolons that sit outside quotes, brackets, and escapes.
 *
 * @param {string} code - The complete format code.
 * @returns {Array<string>} The raw section texts in order.
 */
function splitSections(code) {
    const sections = [];
    let current = "";
    let quoted = false;
    let bracket = false;
    for (let i = 0; i < code.length; i++) {
        const ch = code[i];
        if (quoted) { current += ch; if (ch === "\"") quoted = false; continue; }
        if (bracket) { current += ch; if (ch === "]") bracket = false; continue; }
        if (ch === "\\") { current += ch + (code[i + 1] ?? ""); i++; continue; }
        if (ch === "\"") { quoted = true; current += ch; continue; }
        if (ch === "[") { bracket = true; current += ch; continue; }
        if (ch === ";") { sections.push(current); current = ""; continue; }
        current += ch;
    }
    sections.push(current);
    return sections;
}

/**
 * Reads one literal construct at a position shared by number and date codes.
 *
 * @param {string} section - The section text.
 * @param {number} index - The index of the construct's first character.
 * @returns {{text: string, next: number}|null} The literal text and the index after it, or `null` when no literal construct starts here or it is malformed.
 */
function readLiteral(section, index) {
    const ch = section[index];
    if (ch === "\"") {
        const end = section.indexOf("\"", index + 1);
        return end < 0 ? null : { text: section.slice(index + 1, end), next: end + 1 };
    }
    if (ch === "\\") return index + 1 < section.length ? { text: section[index + 1], next: index + 2 } : null;
    if (ch === "_") return index + 1 < section.length ? { text: " ", next: index + 2 } : null;
    if (ch === "*") return index + 1 < section.length ? { text: "", next: index + 2 } : null;
    if (ch === "[") {
        const end = section.indexOf("]", index + 1);
        if (end < 0) return null;
        const body = section.slice(index + 1, end);
        // [$€-407] carries a currency symbol; [Red] and [Color 10] are display colors with no text.
        if (body.startsWith("$")) return { text: body.slice(1).split("-")[0], next: end + 1 };
        if (/^[A-Za-z]+\s?\d*$/.test(body)) return { text: "", next: end + 1 };
        return null;
    }
    return null;
}

const NUMBER_LITERAL_CHARS = new Set([" ", "$", "+", "-", "/", "(", ")", ":", "!", "^", "&", "'", "~", "{", "}", "<", ">", "="]);

/**
 * Parses one number section of a format code.
 *
 * @param {string} section - The section text.
 * @returns {object|null} The section shape, or `null` when it uses an unsupported construct.
 */
function parseNumberSection(section) {
    const spec = { prefix: "", suffix: "", minInteger: 0, minFraction: 0, maxFraction: 0, grouping: false, scale: 0, digits: false };
    let literal = "";
    let inFraction = false;
    let started = false;
    let pendingCommas = 0;
    // Literals seen after the digit body become the suffix; another digit after them has no
    // sensible cell position.
    const numeric = () => {
        if (started && literal) return false;
        if (!started) { spec.prefix += literal; literal = ""; started = true; }
        return true;
    };
    for (let i = 0; i < section.length;) {
        const ch = section[i];
        const read = readLiteral(section, i);
        if (read) { literal += read.text; i = read.next; continue; }
        if (ch === "0" || ch === "#" || ch === "?") {
            if (!numeric()) return null;
            if (inFraction) {
                if (pendingCommas) return null;
                spec.maxFraction++;
                if (ch === "0") spec.minFraction = spec.maxFraction;
            } else {
                if (pendingCommas) { spec.grouping = true; pendingCommas = 0; }
                if (ch === "0") spec.minInteger++;
            }
            spec.digits = true;
            i++;
            continue;
        }
        if (ch === "." && !inFraction) {
            if (!numeric()) return null;
            inFraction = true;
            i++;
            continue;
        }
        if (ch === "," && started && !literal) { pendingCommas++; i++; continue; }
        if (ch === "%") { spec.scale += 2; literal += "%"; i++; continue; }
        if (ch === "\"" || ch === "\\" || ch === "_" || ch === "*" || ch === "[") return null;
        if (NUMBER_LITERAL_CHARS.has(ch) || ch === "." || ch === "," || (ch >= "1" && ch <= "9") || ch.charCodeAt(0) > 127) {
            literal += ch;
            i++;
            continue;
        }
        return null;
    }
    // Commas after the last integer digit divide by a thousand each (#,##0, shows thousands).
    spec.scale -= 3 * pendingCommas;
    if (started) spec.suffix = literal;
    else spec.prefix = literal;
    return spec;
}

const numberCodes = new Map();
/**
 * Parses and caches a complete number format code.
 *
 * @param {string} code - The format code text.
 * @returns {{positive: object, negative: object|null, zero: object|null}|null} The section shapes, or `null` when the code is invalid.
 *
 * Side effects: populates the number-code cache on first use.
 */
function numberCode(code) {
    if (numberCodes.has(code)) return numberCodes.get(code);
    if (numberCodes.size > 256) numberCodes.clear();
    const sections = splitSections(code).map(parseNumberSection);
    const spec = sections.length <= 3 && sections.every(Boolean) && sections[0].digits
        ? { positive: sections[0], negative: sections[1] ?? null, zero: sections[2] ?? null }
        : null;
    numberCodes.set(code, spec);
    return spec;
}

/**
 * Renders unsigned integer digits with locale grouping, preserving zero padding the code requested.
 *
 * @param {string} integer - The unsigned integer digits, possibly zero padded or empty.
 * @param {boolean} grouping - Whether the code asked for thousands grouping.
 * @param {object} formats - The locale's cached separators and digits.
 * @returns {string} The localized integer text.
 */
function codeInteger(integer, grouping, formats) {
    if (!integer) return "";
    if (grouping && (integer[0] !== "0" || integer === "0")) return formats.integerFormat.format(BigInt(integer));
    const digits = [...integer].map(digit => formats.digits[+digit]);
    if (!grouping) return digits.join("");
    const groups = [];
    for (let end = digits.length; end > 0; end -= 3) groups.unshift(digits.slice(Math.max(0, end - 3), end).join(""));
    return groups.join(formats.groupSeparator);
}

/**
 * Formats one exact number through one parsed section.
 *
 * @param {Big} number - The exact value; negative only when the code has no negative section.
 * @param {object} section - The parsed section shape.
 * @param {string} locale - The resolved locale identifier.
 * @returns {string} The section's literal frame around the localized digits.
 */
function formatNumberSection(number, section, locale) {
    if (!section.digits) return section.prefix + section.suffix;
    const formats = numberLocaleParts(locale);
    const scaled = section.scale ? number.times(new Big(10).pow(section.scale)) : number;
    const parts = fixedParts(scaled, section.maxFraction);
    while (parts.fraction.length > section.minFraction && parts.fraction.endsWith("0"))
        parts.fraction = parts.fraction.slice(0, -1);
    let integer = parts.integer === "0" && section.minInteger === 0 ? "" : parts.integer;
    if (integer.length < section.minInteger) integer = integer.padStart(section.minInteger, "0");
    const fraction = [...parts.fraction].map(digit => formats.digits[+digit]).join("");
    const magnitude = codeInteger(integer, section.grouping, formats)
        + (fraction ? formats.decimalSeparator + fraction : "");
    return (parts.negative ? formats.minusSign : "") + section.prefix + magnitude + section.suffix;
}

/**
 * Formats an exact report number through an Excel-style number format code.
 *
 * @param {unknown} value - A report number accepted by `parseReportNumber`.
 * @param {string} code - The format code text.
 * @param {Element|object|string|null} context - The locale or DOM context used for separators.
 * @returns {string|null} The formatted text, or `null` when the code or value is unsupported.
 */
function formatNumberCode(value, code, context) {
    const spec = numberCode(code);
    if (!spec) return null;
    const number = parseReportNumber(value);
    if (!number) return null;
    const locale = resolveLocale(context);
    if (number.lt(0) && spec.negative) return formatNumberSection(number.abs(), spec.negative, locale);
    if (number.eq(0) && spec.zero) return formatNumberSection(number, spec.zero, locale);
    return formatNumberSection(number, spec.positive, locale);
}

/**
 * Parses a date format code into date tokens and literal runs.
 *
 * @param {string} code - The format code text.
 * @returns {Array<{token: string, width: number}|{literal: string}>|null} The token list, or `null` when the code is invalid.
 */
function parseDateCode(code) {
    const tokens = [];
    const literal = text => {
        const last = tokens[tokens.length - 1];
        if (last && "literal" in last) last.literal += text;
        else tokens.push({ literal: text });
    };
    for (let i = 0; i < code.length;) {
        const ch = code[i];
        // Elapsed-time brackets ([h]:mm) and colors have no place in a date cell.
        if (ch === "[") return null;
        const read = readLiteral(code, i);
        if (read) { literal(read.text); i = read.next; continue; }
        const meridiem = /^(AM\/PM|am\/pm|A\/P|a\/p)/.exec(code.slice(i));
        if (meridiem) { tokens.push({ token: "ampm", width: meridiem[1].length, upper: meridiem[1][0] === "A" }); i += meridiem[1].length; continue; }
        const lower = ch.toLowerCase();
        if ("ymdhs".includes(lower)) {
            let width = 0;
            while (code[i + width]?.toLowerCase() === lower) width++;
            tokens.push({ token: lower, width });
            i += width;
            continue;
        }
        if (/[a-z"\\_*\[\]]/i.test(ch)) return null;
        literal(ch);
        i++;
    }
    const dateTokens = tokens.filter(t => "token" in t);
    if (!dateTokens.length) return null;
    // m means minutes beside hours or seconds, and months everywhere else.
    dateTokens.forEach((t, index) => {
        if (t.token !== "m" || t.width > 2) return;
        const previous = dateTokens[index - 1]?.token;
        const next = dateTokens[index + 1]?.token;
        if (previous === "h" || next === "s") t.token = "minute";
    });
    const limits = { y: 4, m: 5, d: 4, h: 2, s: 2, minute: 2 };
    if (dateTokens.some(t => t.token !== "ampm" && t.width > limits[t.token])) return null;
    return tokens;
}

const dateCodes = new Map();
const dateNameFormats = new Map();
/**
 * Returns a cached localized month or weekday name formatter.
 *
 * @param {string} locale - The resolved locale identifier.
 * @param {string} part - `month` or `weekday`.
 * @param {string} style - `long` or `short`.
 * @returns {Intl.DateTimeFormat} The cached formatter.
 *
 * Side effects: populates the name-formatter cache on first use.
 */
function dateNameFormat(locale, part, style) {
    const key = `${locale}:${part}:${style}`;
    let format = dateNameFormats.get(key);
    if (!format) {
        format = new Intl.DateTimeFormat(locale, { [part]: style });
        dateNameFormats.set(key, format);
    }
    return format;
}

/**
 * Formats a session-local date through an Excel-style date format code.
 *
 * @param {unknown} value - A value whose leading text matches `YYYY-MM-DD[ T]HH:MM[:SS]`.
 * @param {string} code - The format code text.
 * @param {Element|object|string|null} context - The locale or DOM context used for month and weekday names.
 * @returns {string|null} The formatted text, or `null` when the code or value is unsupported.
 *
 * Side effects: populates the date-code cache on first use.
 */
function formatDateCode(value, code, context) {
    if (!dateCodes.has(code)) {
        if (dateCodes.size > 256) dateCodes.clear();
        dateCodes.set(code, parseDateCode(code));
    }
    const tokens = dateCodes.get(code);
    if (!tokens) return null;
    const parsed = parseDateText(value);
    if (!parsed) return null;
    const date = parsed.date;
    const locale = resolveLocale(context);
    const twelveHour = tokens.some(t => t.token === "ampm");
    const pad = (n, width) => String(n).padStart(width, "0");
    let result = "";
    for (const t of tokens) {
        if ("literal" in t) { result += t.literal; continue; }
        switch (t.token) {
            case "y": result += t.width <= 2 ? pad(date.getFullYear() % 100, 2) : pad(date.getFullYear(), 4); break;
            case "m":
                result += t.width === 1 ? String(date.getMonth() + 1)
                    : t.width === 2 ? pad(date.getMonth() + 1, 2)
                    : t.width === 3 ? dateNameFormat(locale, "month", "short").format(date)
                    : t.width === 4 ? dateNameFormat(locale, "month", "long").format(date)
                    : dateNameFormat(locale, "month", "long").format(date).slice(0, 1).toLocaleUpperCase(locale);
                break;
            case "d":
                result += t.width === 1 ? String(date.getDate())
                    : t.width === 2 ? pad(date.getDate(), 2)
                    : t.width === 3 ? dateNameFormat(locale, "weekday", "short").format(date)
                    : dateNameFormat(locale, "weekday", "long").format(date);
                break;
            case "h": {
                const hours = twelveHour ? (date.getHours() % 12 || 12) : date.getHours();
                result += t.width === 1 ? String(hours) : pad(hours, 2);
                break;
            }
            case "minute": result += t.width === 1 ? String(date.getMinutes()) : pad(date.getMinutes(), 2); break;
            case "s": result += t.width === 1 ? String(date.getSeconds()) : pad(date.getSeconds(), 2); break;
            case "ampm": {
                const text = date.getHours() < 12 ? "AM" : "PM";
                const shown = t.width === 3 ? text[0] : text;
                result += t.upper ? shown : shown.toLowerCase();
                break;
            }
        }
    }
    return result;
}

/**
 * Applies one supported numeric or date mask without converting exact numeric digits through floating point.
 *
 * @param {unknown} value - The report value to format.
 * @param {string} type - The protocol column type.
 * @param {string} mask - An Excel-style format code.
 * @param {Element|object|string|null} [context=null] - The locale or DOM context used for localized output.
 * @returns {string|null} The formatted value, or `null` when the type, mask, or input is unsupported.
 */
export function applyMask(value, type, mask, context = null) {
    if (typeof mask !== "string" || !mask || mask.length > MAX_MASK_LENGTH) return null;
    if (type === "number") return formatNumberCode(value, mask, context);
    if (type === "date") return formatDateCode(value, mask, context);
    return null;
}

/**
 * Decimal: another value in this result column has a fractional component, so whole values still
 * render consistently as decimals.
 *
 * @param {unknown} value - The scalar result value.
 * @param {string} type - The protocol column type.
 * @param {boolean} [decimal=false] - Whether the numeric value should retain decimal precision.
 * @param {string|null} [mask=null] - An optional supported mask token.
 * @param {Element|object|string|null} [context=null] - The locale or DOM context used for booleans and numbers.
 * @returns {string} Empty text for nullish input, masked output when valid, or the type's default display representation.
 */
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

/**
 * Formats one aggregate value, using an em dash for null and up to two fractional digits by default.
 *
 * @param {unknown} value - The aggregate result value.
 * @param {string} [type="number"] - The aggregate's effective protocol type.
 * @param {string|null} [mask=null] - An optional supported mask token.
 * @param {Element|object|string|null} [context=null] - The locale or DOM context used for formatting.
 * @returns {string} The aggregate display text.
 */
export function formatAgg(value, type = "number", mask = null, context = null) {
    if (value === null || value === undefined) return "—";
    if (mask) {
        const masked = applyMask(value, type, mask, context);
        if (masked !== null) return masked;
    }
    if (type === "number") return exactNumber(value, { maximum: 2 }, context) ?? String(value);
    return formatValue(value, type, false, null, context);
}

/**
 * Formats an integer-like report value with localized grouping and no fractional digits.
 *
 * @param {unknown} value - The report number to format.
 * @param {Element|object|string|null} [context=null] - The locale or DOM context used for grouping.
 * @returns {string} The rounded localized integer, or the original value coerced to text when non-numeric.
 */
export function formatInteger(value, context = null) {
    return exactNumber(value, { maximum: 0 }, context) ?? String(value);
}

/** English fallbacks for aggregate names when a locale catalog does not contain the function key. */
export const FN_LABELS = {
    sum: "Sum", avg: "Avg", median: "Median", min: "Min", max: "Max",
    count: "Count", countDistinct: "Count Distinct",
    // Retained for previously stored aggregate payloads using this neutral label.
    total: "Total",
};

/**
 * Resolves an aggregate function's localized display label with a stable fallback.
 *
 * @param {Element|object|string|null} context - The locale or DOM context used for translation.
 * @param {string} fn - The protocol aggregate token.
 * @returns {string} The localized catalog value, English fallback, or original token.
 */
export function fnLabel(context, fn) {
    const key = `aggregate.${fn}`;
    const label = translate(context, key);
    return label === key ? FN_LABELS[fn] ?? fn : label;
}

/** Canonical aggregate order used by menus and subtotal rows. */
export const FN_ORDER = ["sum", "avg", "median", "min", "max", "count", "countDistinct", "total"];
