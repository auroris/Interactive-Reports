// Settings chips: one chip per active setting in the state doc, with inline
// enable/disable, edit (reopens the owning dialog), and remove. The chip strip
// is the doc made visible — everything here reads the doc and mutates it
// through w.apply, never through private render state.
//
// Source-layer chips (search, filters, breaks, aggregates, computed, highlights)
// always display: they are the stage-1 state every view consumes or returns to.
// When the pipeline has a group tail, the group layer's computed and highlight
// rules chip alongside them, marked as view-scoped. The view chip summarizes the
// tail; its remove returns to the grid (parking the tail on the shelf) and stays
// available as the way out of a view whose button and dialog are gone.
//
// A chip whose owning feature is not whitelisted still renders (the state is
// real — a default or saved report put it there) but renders locked: no toggle,
// no edit, no remove.

import { el, icon } from "../../core/dom.js";
import { featureEnabled, labelOf } from "../schema.js";
import { FN_LABELS } from "./format.js";
import { chartSummary } from "./chart-view.js";
import {
    activateTail,
    modeOf,
    removeSourceComputedColumn,
    removeStageComputedColumn,
    sourceLayer,
    stageOf,
    tailOf,
} from "../state.js";
import { filterDialog, computeDialog, highlightDialog } from "../dialogs/rules.js";
import { breakDialog, aggregateDialog } from "../dialogs/grid.js";
import { openViewDialog } from "../dialogs/view.js";

const MODE_LABELS = { groupBy: "Group by", pivot: "Pivot", chart: "Chart" };

function chipToggle(w, kind, index, on) {
    w.applyOrBanner(d => {
        const list = kind === "filter" ? sourceLayer(d).filters
            : kind === "computed" ? sourceLayer(d).computed
            : kind === "highlight" ? sourceLayer(d).highlights
            : kind === "stageComputed" ? stageOf(d, "group")?.layer?.computed
            : kind === "stageHighlight" ? stageOf(d, "group")?.layer?.highlights
            : null;
        const item = list?.[index];
        if (item) item.enabled = on;
    });
}

function chipRemove(w, kind, index) {
    if (kind === "computed") {
        const column = sourceLayer(w.doc).computed?.[index]?.id;
        if (!column) return;
        w.apply(d => {
            const dropped = removeSourceComputedColumn(d, column);
            if (dropped.length) {
                w.notify(
                    `${dropped.map(m => MODE_LABELS[m] ?? m).join(" and ")} configuration removed — it depended on ${column}.`,
                    "warn");
            }
        }).catch(err => w.showError(err));
        return;
    }
    if (kind === "stageComputed") {
        const column = stageOf(w.doc, "group")?.layer?.computed?.[index]?.id;
        if (!column) return;
        w.apply(d => removeStageComputedColumn(d, stageOf(d, "group"), column))
            .catch(err => w.showError(err));
        return;
    }
    w.applyOrBanner(d => {
        const layer = sourceLayer(d);
        switch (kind) {
            case "search": d.search = ""; w.els.search.value = ""; break;
            case "filter": layer.filters?.splice(index, 1); break;
            case "break": layer.breaks = (layer.breaks ?? []).filter((_, i) => i !== index); break;
            case "aggregate": layer.aggregates?.splice(index, 1); break;
            case "highlight": layer.highlights?.splice(index, 1); break;
            case "stageHighlight": stageOf(d, "group")?.layer?.highlights?.splice(index, 1); break;
            case "view": activateTail(d, "grid"); break;
        }
    });
}

function chipEdit(w, kind, index) {
    switch (kind) {
        case "search": w.els.search.focus(); w.els.search.select(); break;
        case "filter": filterDialog(w, { editIndex: index }); break;
        case "break": breakDialog(w); break;
        case "aggregate": aggregateDialog(w); break;
        case "computed": computeDialog(w, index); break;
        case "highlight": highlightDialog(w, index); break;
        // Stage rules edit through the same dialogs — the stage context already
        // routes them to the group layer while its view is active.
        case "stageComputed": computeDialog(w, index); break;
        case "stageHighlight": highlightDialog(w, index); break;
        case "view": openViewDialog(w, modeOf(w.doc)); break;
    }
}

function chip({ w, kind, index, text, colLabel, off, toggleable = true, removable = true, editable = true, swatch }) {
    const node = el("span", { class: "ir-chip" + (off ? " ir-chip-off" : ""), dataset: { kind } });
    if (toggleable) {
        node.append(el("input", {
            type: "checkbox", class: "ir-chip-check", checked: !off,
            title: off ? "Enable" : "Disable",
            onchange: e => chipToggle(w, kind, index, e.target.checked),
        }));
    }
    if (swatch) node.append(el("span", { class: "ir-chip-swatch", style: { background: swatch } }));
    const label = editable
        ? el("button", {
            type: "button", class: "ir-chip-label", title: "Edit",
            onclick: () => chipEdit(w, kind, index),
        })
        : el("span", { class: "ir-chip-label ir-chip-static" });
    if (colLabel) label.append(el("b", {}, colLabel), " ");
    label.append(text);
    node.append(label);
    if (removable) {
        node.append(el("button", {
            type: "button", class: "ir-chip-x", "aria-label": "Remove", title: "Remove",
            onclick: () => chipRemove(w, kind, index),
        }, icon("close")));
    }
    return node;
}

const highlightChip = (w, kind, lock) => ({ h, i, sequence }) => chip({
    w, kind, index: i, off: h.enabled === false,
    swatch: h.style?.bg ?? "#fff3a0",
    colLabel: h.name ?? h.id ?? "Highlight",
    text: `#${sequence} · ${h.expr}` + (h.scope === "cell" ? ` (${h.col} cell)` : " (row)"),
    ...lock,
});

const bySequence = rules => (rules ?? [])
    .map((h, i) => ({ h, i, sequence: h.sequence ?? ((i + 1) * 10) }))
    .sort((left, right) => left.sequence - right.sequence);

/// The view chip's text: a compact summary of the tail's shape.
function tailSummary(w, mode) {
    if (mode === "chart") {
        const shape = stageOf(w.doc, "chart")?.shape;
        return shape ? chartSummary(w, shape) : "Chart";
    }
    const group = stageOf(w.doc, "group")?.shape ?? {};
    if (mode === "groupBy")
        return (group.by ?? []).map(c => labelOf(w, c)).join(", ");
    const spread = stageOf(w.doc, "spread")?.shape ?? {};
    const colNames = (spread.cols ?? []).map(c => c.toLowerCase());
    const rows = (group.by ?? []).filter(c => !colNames.includes(c.toLowerCase()));
    return `${rows.map(c => labelOf(w, c)).join(", ")} × ${(spread.cols ?? []).map(c => labelOf(w, c)).join(", ")}`
        + (spread.totals ? " · totals" : "");
}

export function renderChips(w, container) {
    const d = w.doc;
    const layer = sourceLayer(d);
    const mode = modeOf(d);
    const chips = [];
    const lock = feature => featureEnabled(w, feature)
        ? {}
        : { toggleable: false, removable: false, editable: false };

    if (d.search) {
        chips.push(chip({ w, kind: "search", index: 0, toggleable: false, colLabel: "Search", text: `'${d.search}'`, ...lock("search") }));
    }
    (layer.filters ?? []).forEach((f, i) =>
        chips.push(chip({ w, kind: "filter", index: i, off: f.enabled === false, colLabel: "Filter", text: f.expr, ...lock("filter") })));
    (layer.breaks ?? []).forEach((b, i) =>
        chips.push(chip({ w, kind: "break", index: i, toggleable: false, colLabel: "Break", text: labelOf(w, b), ...lock("controlBreak") })));
    (layer.aggregates ?? []).forEach((a, i) =>
        chips.push(chip({ w, kind: "aggregate", index: i, toggleable: false, colLabel: "Σ", text: `${FN_LABELS[a.fn] ?? a.fn} of ${labelOf(w, a.col)}`, ...lock("aggregate") })));
    // Editing a source rule reopens its dialog, and the dialogs route to the
    // CURRENT stage's layer — so outside the grid, source computed/highlight
    // chips keep toggle and remove but drop edit (switch to grid to edit them).
    const sourceRuleLock = feature => ({
        ...lock(feature),
        ...(mode !== "grid" ? { editable: false } : {}),
    });
    (layer.computed ?? []).forEach((c, i) =>
        chips.push(chip({ w, kind: "computed", index: i, off: c.enabled === false, colLabel: "ƒ", text: c.label ?? c.id, ...sourceRuleLock("compute") })));
    bySequence(layer.highlights).forEach(entry =>
        chips.push(highlightChip(w, "highlight", sourceRuleLock("highlight"))(entry)));

    // The active group stage's own rules: computed metrics (group and pivot)
    // and, when the group is the terminal table, its highlights.
    const group = stageOf(d, "group");
    if (group && (mode === "groupBy" || mode === "pivot")) {
        (group.layer?.computed ?? []).forEach((c, i) =>
            chips.push(chip({ w, kind: "stageComputed", index: i, off: c.enabled === false, colLabel: "ƒ view", text: c.label ?? c.id, ...lock("compute") })));
        if (mode === "groupBy")
            bySequence(group.layer?.highlights).forEach(entry =>
                chips.push(highlightChip(w, "stageHighlight", lock("highlight"))(entry)));
    }

    if (mode !== "grid" && tailOf(d).length) {
        // Remove (back to grid) survives the lock — see the header comment.
        const viewLock = featureEnabled(w, mode) ? {} : { editable: false };
        chips.push(chip({
            w, kind: "view", index: 0, toggleable: false,
            colLabel: MODE_LABELS[mode], text: tailSummary(w, mode), ...viewLock,
        }));
    }

    container.replaceChildren(...chips);
    container.hidden = chips.length === 0;
}
