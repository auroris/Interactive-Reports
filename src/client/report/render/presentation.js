// Constrained presentation styles shared by the live table and format preview.
// Returning every supported property lets a caller assign the object repeatedly
// and reliably clear styles from the previous selection.

export function presentationStyle(format = {}, { defaultAlign = "" } = {}) {
    return {
        textAlign: format?.align ?? defaultAlign,
        fontWeight: format?.bold ? "600" : "",
        fontStyle: format?.italic ? "italic" : "",
        color: format?.fg ?? "",
        background: format?.bg ?? "",
    };
}

export function alignmentStyle(format) {
    return format?.align ? { textAlign: format.align } : undefined;
}
