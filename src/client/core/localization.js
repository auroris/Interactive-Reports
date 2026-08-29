// Client-owned language selection and coded-error messages. Server descriptions
// remain the fallback for unknown codes so independently deployed clients and servers
// fail legibly while their catalogs are temporarily out of step.

const ENGLISH_ERRORS = {
    "IR-1000": ["Authentication required", "Sign in to perform this operation."],
    "IR-1001": ["Report not found", "The report was not found or you are not allowed to access it."],
    "IR-1002": ["Saved report not found", "The saved report was not found or you are not allowed to access it."],
    "IR-1003": ["Endpoint not found", "The requested endpoint is not available."],
    "IR-1004": ["Authorization denied", "You are not allowed to perform this operation."],
    "IR-1005": ["Report authorization failed", "An unexpected error occurred while authorizing the report operation."],
    "IR-1100": ["Feature disabled", "This feature is not enabled for the report."],
    "IR-1101": ["Unsupported export format", "The requested export format is not supported."],
    "IR-1200": ["Malformed report state document", "The report state document is not valid JSON."],
    "IR-1201": ["Report state failed validation", "One or more report settings are invalid."],
    "IR-1202": ["Report execution failed", "An unexpected error occurred while processing the report."],
    "IR-1300": ["Malformed save request", "The save request is not valid JSON."],
    "IR-1301": ["Invalid saved report title", "Enter a title between 1 and 200 characters."],
    "IR-1302": ["Saved report state required", "The save request must include report state."],
    "IR-1303": ["Malformed update request", "The update request is not valid JSON."],
    "IR-1304": ["Invalid saved report owner", "The owner must be a non-empty identity value."],
    "IR-1305": ["Malformed report document", "The report document is not valid JSON."],
    "IR-1306": ["Invalid report document title", "Enter a document title between 1 and 200 characters."],
    "IR-1307": ["Report document state required", "The report document must include report state."],
    "IR-1308": ["Report definition state required", "The report definition must include report state."],
    "IR-1309": ["Saved report title conflict", "A saved report with this title already exists."],
    "IR-1310": ["Configured report title conflict", "A read-only configured report uses this title."],
    "IR-1311": ["Read-only report", "Configured report documents cannot be updated or deleted. Use Save As to create an editable copy."],
    "IR-1400": ["Malformed authorization request", "The authorization request is not valid JSON."],
    "IR-1401": ["Restriction value required", "The authorization request must include a restriction value."],
    "IR-1402": ["Invalid identity", "Enter an identity between 1 and 400 characters."],
    "IR-1403": ["Report authorization conflict", "Anonymous and administrators-only reports cannot use user restrictions."],
    "IR-1404": ["Report authorization conflict", "Anonymous and administrators-only reports cannot have user grants."],
    "IR-1500": ["Unsupported GraphQL transport", "Interactive Reports GraphQL supports HTTP GET and POST queries only."],
};

const FRENCH_CANADIAN_ERRORS = {
    "IR-1000": ["Authentification requise", "Connectez-vous pour effectuer cette opération."],
    "IR-1001": ["Rapport introuvable", "Le rapport est introuvable ou vous n’êtes pas autorisé à y accéder."],
    "IR-1002": ["Rapport enregistré introuvable", "Le rapport enregistré est introuvable ou vous n’êtes pas autorisé à y accéder."],
    "IR-1003": ["Point de terminaison introuvable", "Le point de terminaison demandé n’est pas disponible."],
    "IR-1004": ["Autorisation refusée", "Vous n’êtes pas autorisé à effectuer cette opération."],
    "IR-1005": ["Échec de l’autorisation du rapport", "Une erreur inattendue s’est produite pendant l’autorisation de l’opération."],
    "IR-1100": ["Fonctionnalité désactivée", "Cette fonctionnalité n’est pas activée pour le rapport."],
    "IR-1101": ["Format d’exportation non pris en charge", "Le format d’exportation demandé n’est pas pris en charge."],
    "IR-1200": ["État du rapport mal formé", "Le document d’état du rapport n’est pas au format JSON valide."],
    "IR-1201": ["Échec de la validation de l’état du rapport", "Un ou plusieurs paramètres du rapport ne sont pas valides."],
    "IR-1202": ["Échec de l’exécution du rapport", "Une erreur inattendue s’est produite pendant le traitement du rapport."],
    "IR-1300": ["Demande d’enregistrement mal formée", "La demande d’enregistrement n’est pas au format JSON valide."],
    "IR-1301": ["Titre du rapport enregistré non valide", "Entrez un titre de 1 à 200 caractères."],
    "IR-1302": ["État du rapport requis", "La demande d’enregistrement doit contenir l’état du rapport."],
    "IR-1303": ["Demande de mise à jour mal formée", "La demande de mise à jour n’est pas au format JSON valide."],
    "IR-1304": ["Propriétaire du rapport non valide", "Le propriétaire doit être une valeur d’identité non vide."],
    "IR-1305": ["Document de rapport mal formé", "Le document de rapport n’est pas au format JSON valide."],
    "IR-1306": ["Titre du document non valide", "Entrez un titre de document de 1 à 200 caractères."],
    "IR-1307": ["État du document requis", "Le document de rapport doit contenir l’état du rapport."],
    "IR-1308": ["État de la définition requis", "La définition du rapport doit contenir l’état du rapport."],
    "IR-1309": ["Conflit de titre de rapport enregistré", "Un rapport enregistré porte déjà ce titre."],
    "IR-1310": ["Conflit de titre de rapport configuré", "Un rapport configuré en lecture seule porte déjà ce titre."],
    "IR-1311": ["Rapport en lecture seule", "Les documents de rapport configurés ne peuvent pas être modifiés ni supprimés. Utilisez « Enregistrer sous » pour créer une copie modifiable."],
    "IR-1400": ["Demande d’autorisation mal formée", "La demande d’autorisation n’est pas au format JSON valide."],
    "IR-1401": ["Valeur de restriction requise", "La demande d’autorisation doit contenir une valeur de restriction."],
    "IR-1402": ["Identité non valide", "Entrez une identité de 1 à 400 caractères."],
    "IR-1403": ["Conflit d’autorisation du rapport", "Les rapports anonymes et réservés aux administrateurs ne peuvent pas utiliser de restrictions par utilisateur."],
    "IR-1404": ["Conflit d’autorisation du rapport", "Les rapports anonymes et réservés aux administrateurs ne peuvent pas accorder d’accès aux utilisateurs."],
    "IR-1500": ["Transport GraphQL non pris en charge", "Le module GraphQL d’Interactive Reports accepte uniquement les requêtes HTTP GET et POST."],
};

const CATALOGS = {
    en: ENGLISH_ERRORS,
    "fr-CA": FRENCH_CANADIAN_ERRORS,
};

function supportedLocale(value) {
    const locale = String(value ?? "").trim().toLowerCase();
    if (locale === "fr" || locale.startsWith("fr-")) return "fr-CA";
    if (locale === "en" || locale.startsWith("en-")) return "en";
    return null;
}

/// Resolve an explicit locale string or the nearest lang-bearing ancestor. The page
/// language wins over browser preferences; unsupported languages fall back to English.
export function resolveLocale(context = null) {
    if (typeof context === "string") return supportedLocale(context) ?? "en";

    const elementLanguage = context?.closest?.("[lang]")?.getAttribute?.("lang");
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

/// Null means the client does not recognize the server's code and should display the
/// server-owned fallback description instead.
export function localizedError(code, context = null) {
    const entry = CATALOGS[resolveLocale(context)]?.[code];
    return entry ? { title: entry[0], description: entry[1] } : null;
}

export function errorReference(traceId, context = null, compact = false) {
    const french = resolveLocale(context) === "fr-CA";
    if (compact) return french ? `(réf. ${traceId})` : `(ref ${traceId})`;
    return french ? `Référence : ${traceId}` : `Reference: ${traceId}`;
}

export const supportedLocales = Object.freeze(Object.keys(CATALOGS));
export const supportedErrorCodes = Object.freeze(Object.keys(ENGLISH_ERRORS));
