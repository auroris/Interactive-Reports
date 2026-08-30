// Settings chips: one chip per active setting in the state doc, with inline
// enable/disable, edit (reopens the owning dialog), and remove. The chip strip
// is the doc made visible — everything here reads the doc and mutates it
// through w.apply, never through private render state.
//
// Executable ordinary composables are enumerated with exact document ownership
// across the complete active table ancestry. Array position is a mutation
// address, not execution order. Inherited and foreign/repeated nodes remain
// visible but read-only. Only the last node of each kind owned by the active
// table is safe for the packaged editors to mutate. The view chip summarizes
// the shape directly owned by the active table.
//
// A chip whose owning feature is not whitelisted still renders (the state is
// real — a default or saved report put it there) but renders locked: no toggle,
// no edit, no remove.

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

const modeLabel = (w, mode) => w.t(mode === "groupBy" ? "group.label" : `toolbar.${mode}`);

const nodeFields = {
    filter: "filters",
    compute: "computed",
    highlight: "highlights",
    break: "breaks",
    aggregate: "aggregates",
};

const mutableLocation = (doc, location) => composableLocations(doc).find(candidate =>
    candidate.authorable
    && candidate.tableId.toLowerCase() === location?.tableId?.toLowerCase()
    && candidate.composableIndex === location?.composableIndex
    && candidate.composable?.kind === location?.composable?.kind) ?? null;

function chipToggle(w, kind, index, on, location) {
    w.applyOrBanner(d => {
        const found = mutableLocation(d, location);
        const field = nodeFields[found?.composable?.kind];
        const list = field ? found.composable[field] : null;
        const item = list?.[index];
        if (item) item.enabled = on;
    });
}

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

function chip({ w, kind, index, text, colLabel, off, toggleable = true, removable = true, editable = true, swatch, location }) {
    const node = el("span", {
        class: "ir-chip" + (off ? " ir-chip-off" : ""),
        dataset: {
            kind,
            ...(location ? { table: location.tableId, inherited: String(location.inherited) } : {}),
        },
    });
    // The setting's full description names the checkbox and remove button, so
    // assistive tech hears WHICH setting each control governs; the titles stay
    // short for pointer tooltips.
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

const highlightChip = (w, lock) => ({ h, index, sequence, location }) => chip({
    w, kind: "highlight", index, off: h.enabled === false,
    // Preview whichever color the rule actually sets; the dialog's default
    // background is the last resort for legacy rules with no style at all.
    swatch: h.style?.bg ?? h.style?.fg ?? "#fff3cd",
    colLabel: h.name ?? h.id ?? w.t("highlight.label"),
    text: `#${sequence} · ${h.expr} ${w.t(h.scope === "cell" ? "highlight.scopeCell" : "highlight.scopeRow", { column: h.col })}`,
    location,
    ...lock,
});

/// Repeated active-table Highlight nodes are one semantic priority set. Row rules
/// precede cell rules; within each scope explicit sequence wins. Missing sequence
/// is normalized by stable id, never composable or list position. The displayed
/// fallback uses the canonical first-unused ten-step slot.
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

/// The view chip's text: a compact summary of the active table's owned shape.
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
        // Remove (back to grid) survives the lock — see the header comment.
        const viewLock = featureEnabled(w, mode) ? {} : { editable: false };
        chips.push(chip({
            w, kind: "view", index: 0, toggleable: false,
            colLabel: modeLabel(w, mode), text: shapeSummary(w, mode), ...viewLock,
        }));
    }

    container.replaceChildren(...chips);
    container.hidden = chips.length === 0;
}
