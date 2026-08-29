// Locale resolution and message formatting shared by every packaged widget.
// Catalogs are embedded in the bundles so localization never adds a network
// request or makes error presentation depend on API availability.

import { IntlMessageFormat } from "intl-messageformat";
import { errors as englishErrors, messages as englishMessages } from "../locales/en.js";
import { errors as frenchCanadianErrors, messages as frenchCanadianMessages } from "../locales/fr-CA.js";

const CATALOGS = {
    en: { errors: englishErrors, messages: englishMessages },
    "fr-CA": { errors: frenchCanadianErrors, messages: frenchCanadianMessages },
};
const messageFormats = new Map();

function supportedLocale(value) {
    const locale = String(value ?? "").trim().toLowerCase();
    if (locale === "fr" || locale.startsWith("fr-")) return "fr-CA";
    if (locale === "en" || locale.startsWith("en-")) return "en";
    return null;
}

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

/// Resolve an explicit locale string or the nearest lang-bearing ancestor,
/// crossing shadow-root hosts. Page language wins over browser preferences;
/// unsupported explicit languages use the English product fallback.
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

/// Translate one stable UI message id. Messages use ICU syntax, which gives
/// translators control over interpolation and plural forms without assembling
/// language-specific sentences in component code.
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

export function formatNumber(context, value, options = {}) {
    return new Intl.NumberFormat(resolveLocale(context), options).format(value);
}

export function formatDate(context, value, options = {}) {
    return new Intl.DateTimeFormat(resolveLocale(context), options).format(value);
}

export function formatList(context, values, options = {}) {
    return new Intl.ListFormat(resolveLocale(context), options).format(values);
}

/// Null means the client does not recognize the server's code and should display
/// the server-owned fallback description instead.
export function localizedError(code, context = null) {
    const entry = CATALOGS[resolveLocale(context)]?.errors[code];
    return entry ? { title: entry[0], description: entry[1] } : null;
}

export function errorReference(traceId, context = null, compact = false) {
    const french = resolveLocale(context) === "fr-CA";
    if (compact) return french ? `(réf. ${traceId})` : `(ref ${traceId})`;
    return french ? `Référence : ${traceId}` : `Reference: ${traceId}`;
}

export const supportedLocales = Object.freeze(Object.keys(CATALOGS));
export const supportedErrorCodes = Object.freeze(Object.keys(englishErrors));
export const supportedMessageKeys = Object.freeze(Object.keys(englishMessages));
export const localeCatalogs = Object.freeze(CATALOGS);
