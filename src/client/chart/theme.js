// Chart theme resolution translates the documented --ir-* custom properties into concrete
// colors and fonts, plus the color arithmetic required by chart forms. These presentation
// choices remain outside the report protocol and saved state.

// Fallbacks mirror the token defaults in ir.css; getComputedStyle resolves any host overrides
// of the documented --ir-* custom properties.
const FALLBACKS = {
    "--ir-chart-1": "#0572ce",
    "--ir-chart-2": "#eb6834",
    "--ir-chart-3": "#1baf7a",
    "--ir-chart-4": "#eda100",
    "--ir-chart-5": "#e87ba4",
    "--ir-chart-6": "#008300",
    "--ir-chart-7": "#4a3aa7",
    "--ir-chart-8": "#e34948",
    "--ir-chart-grid": "#e8ebee",
    "--ir-chart-text": "#5d6771",
    "--ir-bg": "#ffffff",
    "--ir-font": 'system-ui, -apple-system, "Segoe UI", sans-serif',
};

/**
 * Reads the chart palette, typography, and surface colors from the canvas's computed styles.
 *
 * @param {HTMLCanvasElement} canvas - The canvas used to resolve theme colors or render the chart.
 * @returns {object} The resolved chart palette, font, grid, and surface theme.
 */
export function readTheme(canvas) {
    const styles = getComputedStyle(canvas);
    const token = name => styles.getPropertyValue(name).trim() || FALLBACKS[name];
    return {
        palette: [1, 2, 3, 4, 5, 6, 7, 8].map(i => token(`--ir-chart-${i}`)),
        grid: token("--ir-chart-grid"),
        text: token("--ir-chart-text"),
        surface: token("--ir-bg"),
        font: { family: token("--ir-font"), size: 12 },
    };
}

/**
 * Applies an alpha value after using the canvas color parser to normalize any valid CSS color.
 *
 * @param {string} color - A CSS color accepted by the browser canvas implementation.
 * @param {number} alpha - The opacity to place in the returned `rgba()` value.
 * @returns {string} The normalized red, green, and blue channels with the requested alpha.
 */
export function withAlpha(color, alpha) {
    const probe = document.createElement("canvas").getContext("2d");
    probe.fillStyle = "#000";
    probe.fillStyle = color;
    const parsed = probe.fillStyle;
    if (parsed.startsWith("#")) {
        const r = parseInt(parsed.slice(1, 3), 16);
        const g = parseInt(parsed.slice(3, 5), 16);
        const b = parseInt(parsed.slice(5, 7), 16);
        return `rgba(${r}, ${g}, ${b}, ${alpha})`;
    }
    const inner = parsed.slice(parsed.indexOf("(") + 1, parsed.lastIndexOf(")")).split(",");
    return `rgba(${inner[0].trim()}, ${inner[1].trim()}, ${inner[2].trim()}, ${alpha})`;
}

// Invariant: fixed hue order, never re-cut per dataset; past the 8th slice the same hues return
// as lighter tints (a step of the same hue, not an invented color).
/**
 * Returns enough chart colors for every slice, deriving translucent variants as needed.
 *
 * @param {number} count - The number of colors required by the chart series.
 * @param {Array<string>} palette - The base chart colors to expand to the requested series count.
 * @returns {Array<string>} The slice colors.
 */
export function sliceColors(count, palette) {
    const tiers = [1, 0.72, 0.5];
    return Array.from({ length: count }, (_, i) => {
        const tier = tiers[Math.min(Math.floor(i / palette.length), tiers.length - 1)];
        const hue = palette[i % palette.length];
        return tier === 1 ? hue : withAlpha(hue, tier);
    });
}
