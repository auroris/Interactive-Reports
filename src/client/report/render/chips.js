// Settings-chip renderer: one chip per active report-state setting, with inline
// enable/disable, edit (reopens the owning dialog), and remove. The chip strip is the doc made
// visible. Everything here reads the document and mutates it through w.apply, never through private
// render state. Executable ordinary composables are enumerated with exact document ownership
// across the complete active table ancestry. Array position is a mutation address, not
// execution order. Inherited and foreign/repeated nodes remain visible but read-only. Only the
// last node of each kind owned by the active table is safe for the packaged editors to mutate.
// The view chip summarizes the shape directly owned by the active table. A chip whose owning
// feature is disabled by the effective control policy still renders (the state is real — a default or saved report put
// it there) but renders locked, with no toggle, edit, or remove action.

import { el, icon } from "../../core/dom.js";
import { formatList } from "../../core/localization.js";
import { featureEnabled, labelOf } from "../schema.js";
import { fnLabel } from "./format.js";
import { chartSummary } from "./chart-view.js";
import {
    activeShapeLocation,
    composableLocations,
    modeOf,
    normalizedHighlightRules,
    removeInputComputedColumn,
    removeTerminalComputedColumn,
} from "../state.js";
import { filterDialog, computeDialog, highlightDialog } from "../dialogs/rules.js";
import { breakDialog, aggregateDialog } from "../dialogs/grid.js";
import { openViewDialog } from "../dialogs/view.js";
import { tableContext } from "../table.js";

/**
 * Returns the localized label of a report view mode.
 *
 * @param {object} w - The report controller providing localization.
 * @param {string} mode - The canonical terminal mode.
 * @returns {string} The mode label.
 */
const modeLabel = (w, mode) => w.t(mode === "groupBy" ? "group.label" : `toolbar.${mode}`);

const nodeFields = {
    filter: "filters",
    compute: "computed",
    highlight: "highlights",
    break: "breaks",
    aggregate: "aggregates",
};

/**
 * Returns the current authorable location matching a previously rendered chip.
 *
 * @param {object} doc - The current mutable state clone being edited.
 * @param {object} location - The previously rendered table id, composable index, and kind.
 * @returns {object|null} The mutable location.
 */
const mutableLocation = (doc, location) => composableLocations(doc).find(candidate =>
    candidate.authorable
    && candidate.tableId.toLowerCase() === location?.tableId?.toLowerCase()
    && candidate.composableIndex === location?.composableIndex
    && candidate.composable?.kind === location?.composable?.kind) ?? null;

/**
 * Updates the enabled state of the report setting represented by a chip.
 *
 * @param {object} w - The report controller whose mutation pipeline applies the change.
 * @param {string} kind - The rendered chip kind; retained to keep chip action signatures uniform.
 * @param {number} index - The zero-based rule index within the owning composable collection.
 * @param {boolean} on - The new enabled state.
 * @param {object} location - The rendered composable location to re-resolve in the mutable clone.
 * @returns {void} No value.
 *
 * Side effects: applies a report-state mutation and reruns the report through `applyOrBanner`.
 */
function chipToggle(w, kind, index, on, location) {
    w.applyOrBanner(d => {
        const found = mutableLocation(d, location);
        const field = nodeFields[found?.composable?.kind];
        const list = field ? found.composable[field] : null;
        const item = list?.[index];
        if (item) item.enabled = on;
    });
}

/**
 * Removes the report setting represented by a chip and cleans dependent state.
 *
 * @param {object} w - The report controller whose state and notifications may change.
 * @param {string} kind - The chip kind, which selects search, view, computed, or ordinary rule removal.
 * @param {number} index - The zero-based rule index within the owning collection.
 * @param {object} location - The rendered composable location used to recover current ownership.
 * @returns {void} No value.
 *
 * Side effects: may switch views, mutate state, clear the search input, run the report, remove dependent configuration, and show warnings or errors.
 */
function chipRemove(w, kind, index, location) {
    if (kind === "view") {
        w.switchView("grid");
        return;
    }
    if (kind === "computed") {
        const found = mutableLocation(w.doc, location);
        const column = found?.composable?.computed?.[index]?.id;
        if (!column) return;
        w.apply(d => {
            const dropped = found.source
                ? removeInputComputedColumn(d, column)
                : removeTerminalComputedColumn(d, column, found.tableId);
            if (dropped.length) {
                w.notify(
                    w.t("chip.configurationRemoved", {
                        modes: formatList(w, dropped.map(mode => modeLabel(w, mode))),
                        column,
                    }),
                    "warn");
            }
        }).catch(err => w.showError(err));
        return;
    }
    w.applyOrBanner(d => {
        switch (kind) {
            case "search": d.search = ""; w.els.search.value = ""; break;
            default: {
                const found = mutableLocation(d, location);
                const field = nodeFields[found?.composable?.kind];
                if (field && Array.isArray(found.composable[field]))
                    found.composable[field].splice(index, 1);
                break;
            }
        }
    });
}

/**
 * Opens the editor associated with a report-setting chip.
 *
 * @param {object} w - The report controller whose editor or search input will open.
 * @param {string} kind - The chip kind used to select an editor.
 * @param {number} index - The zero-based rule index to edit where applicable.
 * @param {object} location - The rendered composable location used to reject stale or read-only chips.
 * @returns {void} No value.
 *
 * Side effects: focuses the search input or opens the corresponding modeless editor dialog.
 */
function chipEdit(w, kind, index, location) {
    if (kind !== "search" && kind !== "view" && !mutableLocation(w.doc, location)) return;
    switch (kind) {
        case "search": w.els.search.focus(); w.els.search.select(); break;
        case "filter": filterDialog(w, { editIndex: index }); break;
        case "break": breakDialog(w); break;
        case "aggregate": aggregateDialog(w); break;
        case "computed": computeDialog(w, index); break;
        case "highlight": highlightDialog(w, index); break;
        case "view": openViewDialog(w, modeOf(w.doc)); break;
    }
}

/**
 * Creates the interactive DOM element representing one report setting.
 *
 * @param {object} options - Controller, chip identity and text, enabled state, control permissions, optional swatch, and composable ownership.
 * @returns {HTMLSpanElement} A detached chip with the permitted controls wired to state actions.
 *
 * Side effects: creates detached DOM nodes and event handlers; it does not mount the chip.
 */
function chip({ w, kind, index, text, colLabel, off, toggleable = true, removable = true, editable = true, swatch, location }) {
    const node = el("span", {
        class: "ir-chip" + (off ? " ir-chip-off" : ""),
        dataset: {
            kind,
            ...(location ? { table: location.tableId, inherited: String(location.inherited) } : {}),
        },
    });
    // The setting's full description names the checkbox and remove button, so assistive tech
    // hears WHICH setting each control governs; the titles stay short for pointer tooltips.
    const name = [colLabel, text].filter(Boolean).join(" ");
    if (toggleable) {
        node.append(el("input", {
            type: "checkbox", class: "ir-chip-check", checked: !off,
            title: w.t(off ? "common.enable" : "common.disable"),
            "aria-label": name,
            onchange: e => chipToggle(w, kind, index, e.target.checked, location),
        }));
    }
    if (swatch) node.append(el("span", { class: "ir-chip-swatch", style: { background: swatch } }));
    const label = editable
        ? el("button", {
            type: "button", class: "ir-chip-label", title: w.t("common.edit"),
            onclick: () => chipEdit(w, kind, index, location),
        })
        : el("span", { class: "ir-chip-label ir-chip-static" });
    if (colLabel) label.append(el("b", {}, colLabel), " ");
    label.append(text);
    node.append(label);
    if (removable) {
        node.append(el("button", {
            type: "button", class: "ir-chip-x", "aria-label": w.t("chip.remove", { name }), title: w.t("common.remove"),
            onclick: () => chipRemove(w, kind, index, location),
        }, icon("close")));
    }
    return node;
}

/**
 * Returns a renderer that creates one highlight chip with sequence and ownership metadata.
 *
 * @param {object} w - The report controller providing localization and chip actions.
 * @param {object} lock - Control flags that make the chip editable or read-only.
 * @returns {Function} A renderer accepting a normalized highlight entry and returning its chip element.
 */
const highlightChip = (w, lock) => ({ h, index, sequence, location }) => chip({
    w, kind: "highlight", index, off: h.enabled === false,
    // Preview whichever color the rule actually sets; the dialog's default background is the
    // last resort for legacy rules with no style at all.
    swatch: h.style?.bg ?? h.style?.fg ?? "#fff3cd",
    colLabel: h.name ?? h.id ?? w.t("highlight.label"),
    text: `#${sequence} · ${h.expr} ${w.t(h.scope === "cell" ? "highlight.scopeCell" : "highlight.scopeRow", { column: h.col })}`,
    location,
    ...lock,
});

// Invariant: repeated active-table Highlight nodes are one semantic priority set. Row rules
// precede cell rules; within each scope explicit sequence wins. Missing sequence is normalized
// by stable id, never composable or list position. The displayed fallback uses the canonical
// first-unused ten-step slot.
/**
 * Returns participating highlight rules in their canonical scope and sequence order.
 *
 * @param {Array<object>} locations - Participating and non-participating composable locations across active ancestry.
 * @returns {Array<object>} Participating highlight entries in canonical scope, sequence, and stable-id order, with original ownership retained.
 */
const orderedHighlights = locations => {
    const entries = locations
        .filter(location => location.participates
            && String(location.composable?.kind ?? "").trim().toLowerCase() === "highlight")
        .flatMap(location => (location.composable?.highlights ?? [])
            .map((h, index) => ({ h, index, location })));
    return normalizedHighlightRules(entries.map(entry => entry.h))
        .map(normalized => ({
            ...entries[normalized.index],
            sequence: normalized.sequence,
        }));
};

/**
 * The view chip's text: a compact summary of the active table's owned shape.
 *
 * @param {object} w - The report controller providing the active shape and labels.
 * @param {string} mode - The active non-grid mode.
 * @returns {string} The shape summary.
 */
function shapeSummary(w, mode) {
    const shape = activeShapeLocation(w.doc)?.composable ?? {};
    if (mode === "chart") {
        return shape.kind === "chart" ? chartSummary(w, shape) : w.t("toolbar.chart");
    }
    if (mode === "groupBy") {
        return (shape.by ?? []).map(c => labelOf(w, c)).join(", ");
    }
    return `${(shape.rows ?? []).map(c => labelOf(w, c)).join(", ")} × ${(shape.cols ?? []).map(c => labelOf(w, c)).join(", ")}`
        + (shape.totals ? ` · ${w.t("pivot.totals")}` : "");
}

/**
 * Rebuilds the chip strip from search, participating composables, and the active table's owned shape.
 *
 * @param {object} w - The report controller containing state, schema, feature permissions, and actions.
 * @param {Element} container - The chip strip whose children and hidden state will be replaced.
 * @returns {void} No value.
 *
 * Side effects: replaces the chip strip with newly wired controls and updates its hidden state.
 */
export function renderChips(w, container) {
    const d = w.doc;
    const mode = modeOf(d);
    const chips = [];
    const lock = feature => featureEnabled(w, feature)
        ? {}
        : { toggleable: false, removable: false, editable: false };
    const readOnly = { toggleable: false, removable: false, editable: false };
    const controls = (location, feature) => location.authorable ? lock(feature) : readOnly;

    if (d.search) {
        chips.push(chip({ w, kind: "search", index: 0, toggleable: false, colLabel: w.t("chip.search"), text: `“${d.search}”`, ...lock("search") }));
    }
    const terminalColumns = new Map(tableContext(w).columns
        .map(column => [column.name.toLowerCase(), column]));
    const columnLabel = name => terminalColumns.get(String(name).toLowerCase())?.label ?? labelOf(w, name);

    const locations = composableLocations(d);
    for (const location of locations) {
        if (!location.participates) continue;
        const node = location.composable ?? {};
        switch (node.kind) {
            case "filter":
                (node.filters ?? []).forEach((rule, index) => chips.push(chip({
                    w, kind: "filter", index,
                    off: rule.enabled === false, colLabel: w.t("filter.label"), text: rule.expr,
                    location, ...controls(location, "filter"),
                })));
                break;
            case "break":
                (node.breaks ?? []).forEach((column, index) => chips.push(chip({
                    w, kind: "break", index,
                    toggleable: false, colLabel: w.t("break.label"), text: columnLabel(column),
                    location, ...controls(location, "controlBreak"),
                })));
                break;
            case "aggregate":
                (node.aggregates ?? []).forEach((rule, index) => chips.push(chip({
                    w, kind: "aggregate", index,
                    toggleable: false, colLabel: "Σ",
                    text: w.t("aggregate.ofColumn", {
                        function: fnLabel(w, rule.fn),
                        column: columnLabel(rule.col),
                    }),
                    location, ...controls(location, "aggregate"),
                })));
                break;
            case "compute":
                (node.computed ?? []).forEach((rule, index) => chips.push(chip({
                    w, kind: "computed", index,
                    off: rule.enabled === false,
                    colLabel: "ƒ",
                    text: rule.label ?? rule.id,
                    location, ...controls(location, "compute"),
                })));
                break;
            case "highlight": break;
        }
    }

    for (const entry of orderedHighlights(locations))
        chips.push(highlightChip(
            w,
            controls(entry.location, "highlight"))(entry));

    if (mode !== "grid" && mode !== "custom" && activeShapeLocation(d)) {
        // Remove (back to grid) survives the feature lock; see the module design comment.
        const viewLock = featureEnabled(w, mode) ? {} : { editable: false };
        chips.push(chip({
            w, kind: "view", index: 0, toggleable: false,
            colLabel: modeLabel(w, mode), text: shapeSummary(w, mode), ...viewLock,
        }));
    }

    container.replaceChildren(...chips);
    container.hidden = chips.length === 0;
}
