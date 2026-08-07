// Settings chips: one chip per active setting in the state doc, with inline
// enable/disable, edit (reopens the owning dialog), and remove. The chip strip
// is the doc made visible — everything here reads the doc and mutates it
// through w.apply, never through private render state.

import { el, icon } from "../../core/dom.js";
import { labelOf } from "../schema.js";
import { FN_LABELS } from "./format.js";
import { chartSummary } from "./chart-view.js";
import { filterDialog, computeDialog, highlightDialog } from "../dialogs/rules.js";
import { breakDialog, aggregateDialog } from "../dialogs/grid.js";
import { openViewDialog } from "../dialogs/view.js";

function chipArray(d, kind) {
    return { filter: d.filters, aggregate: d.aggregates, computed: d.computed, highlight: d.highlights }[kind];
}

function chipToggle(w, kind, index, on) {
    w.applyOrBanner(d => {
        if (kind !== "filter" && kind !== "computed" && kind !== "highlight") return;
        const item = chipArray(d, kind)?.[index];
        if (item) item.enabled = on;
    });
}

function chipRemove(w, kind, index) {
    w.applyOrBanner(d => {
        switch (kind) {
            case "search": d.search = ""; w.els.search.value = ""; break;
            case "break": {
                d.breaks = (d.breaks ?? []).filter((_, i) => i !== index);
                break;
            }
            case "view": d.view = { mode: "grid" }; break;
            default: chipArray(d, kind)?.splice(index, 1);
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
        case "view": openViewDialog(w, w.doc.view?.mode ?? "groupBy"); break;
    }
}

function chip({ w, kind, index, text, colLabel, off, toggleable = true, removable = true, swatch }) {
    const node = el("span", { class: "ir-chip" + (off ? " ir-chip-off" : ""), dataset: { kind } });
    if (toggleable) {
        node.append(el("input", {
            type: "checkbox", class: "ir-chip-check", checked: !off,
            title: off ? "Enable" : "Disable",
            onchange: e => chipToggle(w, kind, index, e.target.checked),
        }));
    }
    if (swatch) node.append(el("span", { class: "ir-chip-swatch", style: { background: swatch } }));
    const label = el("button", {
        type: "button", class: "ir-chip-label", title: "Edit",
        onclick: () => chipEdit(w, kind, index),
    });
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

export function renderChips(w, container) {
    const d = w.doc;
    const chips = [];

    if (d.search) {
        chips.push(chip({ w, kind: "search", index: 0, toggleable: false, colLabel: "Search", text: `'${d.search}'` }));
    }
    (d.filters ?? []).forEach((f, i) =>
        chips.push(chip({ w, kind: "filter", index: i, off: f.enabled === false, colLabel: "Filter", text: f.expr })));
    (d.breaks ?? []).forEach((b, i) =>
        chips.push(chip({ w, kind: "break", index: i, toggleable: false, colLabel: "Break", text: labelOf(w, b) })));
    (d.aggregates ?? []).forEach((a, i) =>
        chips.push(chip({ w, kind: "aggregate", index: i, toggleable: false, colLabel: "Σ", text: `${FN_LABELS[a.fn] ?? a.fn} of ${labelOf(w, a.col)}` })));
    (d.computed ?? []).forEach((c, i) =>
        chips.push(chip({ w, kind: "computed", index: i, off: c.enabled === false, colLabel: "ƒ", text: c.label ?? c.id })));
    (d.highlights ?? []).forEach((h, i) =>
        chips.push(chip({
            w, kind: "highlight", index: i, off: h.enabled === false,
            swatch: h.style?.bg ?? "#fff3a0",
            colLabel: "Highlight",
            text: h.expr + (h.scope === "cell" ? ` (${labelOf(w, h.col)} cell)` : " (row)"),
        })));
    if (d.view?.mode === "groupBy") {
        chips.push(chip({
            w, kind: "view", index: 0, toggleable: false, colLabel: "Group by",
            text: (d.view.groupBy ?? []).map(c => labelOf(w, c)).join(", "),
        }));
    } else if (d.view?.mode === "pivot") {
        chips.push(chip({
            w, kind: "view", index: 0, toggleable: false, colLabel: "Pivot",
            text: `${(d.view.rows ?? []).map(c => labelOf(w, c)).join(", ")} × ${(d.view.cols ?? []).map(c => labelOf(w, c)).join(", ")}`,
        }));
    } else if (d.view?.mode === "chart") {
        chips.push(chip({
            w, kind: "view", index: 0, toggleable: false, colLabel: "Chart",
            text: chartSummary(w, d.view),
        }));
    }

    container.replaceChildren(...chips);
    container.hidden = chips.length === 0;
}
