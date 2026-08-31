// Constrained presentation-style projection shared by the live table and format preview.
// Returning every supported property lets a caller assign the object repeatedly and reliably clear styles from
// the previous selection.

/**
 * Projects the supported column-format fields into an inline-style object that also clears omitted styles.
 *
 * @param {object} [format={}] - A column format containing alignment, emphasis, and colors.
 * @param {{defaultAlign?: string}} [options={}] - Alignment to use when the format does not provide one.
 * @returns {{textAlign: string, fontWeight: string, fontStyle: string, color: string, background: string}} A complete supported style projection.
 */
export function presentationStyle(format = {}, { defaultAlign = "" } = {}) {
    return {
        textAlign: format?.align ?? defaultAlign,
        fontWeight: format?.bold ? "600" : "",
        fontStyle: format?.italic ? "italic" : "",
        color: format?.fg ?? "",
        background: format?.bg ?? "",
    };
}

/**
 * Returns an inline alignment override only when the format declares one.
 *
 * @param {object|null|undefined} format - The column format to inspect.
 * @returns {{textAlign: string}|undefined} The alignment style, or `undefined` to leave inherited alignment unchanged.
 */
export function alignmentStyle(format) {
    return format?.align ? { textAlign: format.align } : undefined;
}
