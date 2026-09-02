// Page-size choices behind Actions → Pagination. The Actions menu presents them as a submenu of
// pickable sizes; this module owns which sizes are offered, which one is current, and how a pick
// reaches the report document, so the menu stays pure dispatch.

const PAGE_LIMITS = [10, 25, 50, 100, 500, 1000];

/**
 * Resolves the page size the report is currently using.
 *
 * @param {object} w - The report controller containing the last result, the working document, and schema limits.
 * @returns {number} The effective page size; `0` means All.
 */
export function currentPageSize(w) {
    return w.lastResult?.page?.size ?? w.doc?.page?.size ?? w.schema?.limits?.defaultPageSize ?? 50;
}

/**
 * Formats a page size the way the menu shows it.
 *
 * @param {object} w - The report controller supplying localization.
 * @param {number} size - A page size; `0` means All.
 * @returns {string} The display label.
 */
export const pageSizeLabel = (w, size) => size === 0 ? w.t("common.all") : String(size);

/**
 * Lists the page sizes the report offers, using server limits and preserving a nonstandard current size.
 *
 * @param {object} w - The report controller containing page state, limits, and localization.
 * @returns {Array<{size: number, label: string, current: boolean}>} Ascending numeric sizes followed by All.
 */
export function pageSizeChoices(w) {
    const current = currentPageSize(w);
    const max = w.schema?.limits?.maxPageSize ?? 1000;
    const numeric = PAGE_LIMITS.filter(size => size <= max);
    // Preserve a developer-defined/default size that is outside the APEX choices. It remains
    // selectable until the user deliberately replaces it.
    if (current > 0 && current <= max && !numeric.includes(current)) numeric.push(current);
    numeric.sort((a, b) => a - b);
    return [...numeric, 0].map(size => ({ size, label: pageSizeLabel(w, size), current: size === current }));
}

/**
 * Applies a page size to the report document and runs the report.
 *
 * @param {object} w - The report controller whose document receives the new size.
 * @param {number} size - The page size to store; `0` means All.
 * @returns {Promise<void>} The controller's apply operation.
 *
 * Side effects: updates `doc.page.size` (the page index resets to 1 like any other user change) and runs the report; failures surface as a banner.
 */
export function applyPageSize(w, size) {
    return w.applyOrBanner(d => {
        d.page ??= { index: 1, size: currentPageSize(w) };
        d.page.size = size;
    });
}
