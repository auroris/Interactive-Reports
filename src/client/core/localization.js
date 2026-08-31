// Locale resolution and message formatting shared by every packaged widget.
// Catalogs are embedded in the bundles so localization never adds a network request or makes
// error presentation depend on API availability.

import { IntlMessageFormat } from "intl-messageformat";
import { errors as englishErrors, messages as englishMessages } from "../locales/en.js";
import { errors as frenchCanadianErrors, messages as frenchCanadianMessages } from "../locales/fr-CA.js";

const CATALOGS = {
    en: { errors: englishErrors, messages: englishMessages },
    "fr-CA": { errors: frenchCanadianErrors, messages: frenchCanadianMessages },
};
const messageFormats = new Map();

/**
 * Returns the supported locale matching the requested language tag.
 *
 * @param {unknown} value - A language tag or value coercible to one.
 * @returns {'en'|'fr-CA'|null} The matching product locale, or `null` when unsupported.
 */
function supportedLocale(value) {
    const locale = String(value ?? "").trim().toLowerCase();
    if (locale === "fr" || locale.startsWith("fr-")) return "fr-CA";
    if (locale === "en" || locale.startsWith("en-")) return "en";
    return null;
}

/**
 * Finds the nearest inherited language tag for a DOM context.
 *
 * @param {Element|object|string|null} context - A possible DOM element from which to begin walking ancestors and shadow hosts.
 * @returns {string|null} The nearest non-empty `lang` attribute, or `null`.
 */
function nearestLanguage(context) {
    let current = context?.nodeType === 1 ? context : null;
    while (current) {
        const language = current.getAttribute?.("lang");
        if (language) return language;
        if (current.parentElement) {
            current = current.parentElement;
            continue;
        }
        current = current.getRootNode?.()?.host ?? null;
    }
    return null;
}

/**
 * Resolves an explicit locale string or the nearest lang-bearing ancestor, crossing
 * shadow-root hosts. Page language wins over browser preferences; unsupported explicit languages use
 * the English product fallback.
 *
 * @param {Element|object|string|null} [context=null] - An explicit language tag, DOM context, or object carrying an owning document.
 * @returns {'en'|'fr-CA'} The supported locale selected from explicit context, page language, browser preferences, or the English fallback.
 */
export function resolveLocale(context = null) {
    if (typeof context === "string") return supportedLocale(context) ?? "en";

    const elementLanguage = nearestLanguage(context);
    const documentLanguage = context?.ownerDocument?.documentElement?.getAttribute?.("lang")
        ?? globalThis.document?.documentElement?.getAttribute?.("lang");
    for (const candidate of [elementLanguage, documentLanguage]) {
        if (candidate) return supportedLocale(candidate) ?? "en";
    }

    const preferences = globalThis.navigator?.languages
        ?? [globalThis.navigator?.language];
    for (const candidate of preferences) {
        const supported = supportedLocale(candidate);
        if (supported) return supported;
    }
    return "en";
}

/**
 * Translates one stable UI message id. Messages use ICU syntax, which gives translators control over
 * interpolation and plural forms without assembling language-specific sentences in component code.
 *
 * @param {Element|object|string|null} context - The locale or DOM context used to select a catalog.
 * @param {string} key - The localization key to resolve.
 * @param {object} [values={}] - Named ICU interpolation and pluralization values.
 * @returns {string} The formatted localized message, falling back to English and then the key itself.
 *
 * Side effects: caches the compiled message formatter by locale, key, and catalog text.
 */
export function translate(context, key, values = {}) {
    const locale = resolveLocale(context);
    const message = CATALOGS[locale]?.messages[key]
        ?? CATALOGS.en.messages[key]
        ?? key;
    const cacheKey = `${locale}\0${key}\0${message}`;
    let format = messageFormats.get(cacheKey);
    if (!format) {
        format = new IntlMessageFormat(message, locale);
        messageFormats.set(cacheKey, format);
    }
    return String(format.format(values));
}

/**
 * Formats a numeric value with locale-aware separators and requested precision.
 *
 * @param {Element|object|string|null} context - The locale or DOM context used to select number conventions.
 * @param {number|bigint|string} value - The value accepted by `Intl.NumberFormat.format`.
 * @param {Intl.NumberFormatOptions} [options={}] - Standard number-format options.
 * @returns {string} The locale-formatted number.
 */
export function formatNumber(context, value, options = {}) {
    return new Intl.NumberFormat(resolveLocale(context), options).format(value);
}

/**
 * Formats a date-like value with the locale and options resolved from the report context.
 *
 * @param {Element|object|string|null} context - The locale or DOM context used to select date conventions.
 * @param {Date|number|string} value - The date-like value accepted by `Intl.DateTimeFormat.format`.
 * @param {Intl.DateTimeFormatOptions} [options={}] - Standard date-time-format options.
 * @returns {string} The locale-formatted date and time.
 */
export function formatDate(context, value, options = {}) {
    return new Intl.DateTimeFormat(resolveLocale(context), options).format(value);
}

/**
 * Formats a list with the locale and options resolved from the report context.
 *
 * @param {Element|object|string|null} context - The locale or DOM context used to select list conventions.
 * @param {Array<string>} values - The display strings to join.
 * @param {Intl.ListFormatOptions} [options={}] - Standard list-format options.
 * @returns {string} The locale-formatted list.
 */
export function formatList(context, values, options = {}) {
    return new Intl.ListFormat(resolveLocale(context), options).format(values);
}

// Protocol contract: null means the client does not recognize the server's code and should
// display the server-owned fallback description instead.
/**
 * Resolves a server error code to its localized user-facing message.
 *
 * @param {string} code - The server error code used to select a localized message.
 * @param {Element|object|string|null} [context=null] - The locale or DOM context used to select the error catalog.
 * @returns {{title: string, description: string}|null} Localized error text, or `null` when the code is unknown.
 */
export function localizedError(code, context = null) {
    const entry = CATALOGS[resolveLocale(context)]?.errors[code];
    return entry ? { title: entry[0], description: entry[1] } : null;
}

/**
 * Formats a trace identifier as a localized full or compact diagnostic reference.
 *
 * @param {string} traceId - The server correlation identifier to display.
 * @param {Element|object|string|null} [context=null] - The locale or DOM context used to select wording.
 * @param {boolean} [compact=false] - Whether to use the parenthetical abbreviation.
 * @returns {string} The English or Canadian French reference text.
 */
export function errorReference(traceId, context = null, compact = false) {
    const french = resolveLocale(context) === "fr-CA";
    if (compact) return french ? `(réf. ${traceId})` : `(ref ${traceId})`;
    return french ? `Référence : ${traceId}` : `Reference: ${traceId}`;
}

/** Supported locale identifiers in catalog insertion order. */
export const supportedLocales = Object.freeze(Object.keys(CATALOGS));
/** Stable server error codes recognized by the bundled English fallback catalog. */
export const supportedErrorCodes = Object.freeze(Object.keys(englishErrors));
/** Stable UI message keys recognized by the bundled English fallback catalog. */
export const supportedMessageKeys = Object.freeze(Object.keys(englishMessages));
/** Read-only top-level access to the embedded catalogs for diagnostics and tooling. */
export const localeCatalogs = Object.freeze(CATALOGS);
