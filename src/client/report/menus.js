// The two popup menus: the toolbar Actions menu and the per-column header menu.
// Menus are pure dispatch — every entry opens a dialog or applies a one-line
// doc mutation; nothing here owns state of its own. Every entry is gated by the
// definition's feature whitelist; sections that empty out take their separators
// and headings with them.

import { popupMenu } from "../core/menu.js";
import { anyMutableFeature, featureEnabled, visibleColumnNames } from "./schema.js";
import { columnsDialog, renameDialog } from "./dialogs/columns.js";
import { filterDialog, computeDialog, highlightDialog } from "./dialogs/rules.js";
import { sortDialog, breakDialog, aggregateDialog } from "./dialogs/grid.js";
import { groupByDialog, pivotDialog, chartDialog } from "./dialogs/view.js";
import { saveDialog } from "./dialogs/save.js";
import { canManageCurrentSaved, deleteCurrentSaved, resetWorkingCopy } from "./saved.js";
import { exportCsv } from "./export.js";

/// Features whose entries live in the grid header menu; if none of them are
/// whitelisted the header offers nothing and should not open (nor look clickable).
const HEADER_FEATURES = ["sort", "rename", "columns", "controlBreak", "filter"];

export function headerMenuAvailable(w, mode) {
    if (mode !== "grid") return featureEnabled(w, "sort");
    return HEADER_FEATURES.some(f => featureEnabled(w, f));
}

const joinSections = sections => sections.filter(s => s.length)
    .flatMap((section, i) => i === 0 ? section : ["-", ...section]);

/// The Actions menu entries the whitelist leaves standing. Exported so the
/// toolbar can hide the Actions button when nothing remains.
export function actionsMenuItems(w) {
    const feature = (name, ...entries) => featureEnabled(w, name) ? entries : [];
    const canSave = canManageCurrentSaved(w);
    const items = joinSections([
        [
            ...feature("columns", { label: "Columns…", onPick: () => columnsDialog(w) }),
            ...feature("filter", { label: "Filter…", onPick: () => filterDialog(w, {}) }),
            ...feature("sort", { label: "Sort…", onPick: () => sortDialog(w) }),
        ],
        [
            ...feature("controlBreak", { label: "Control Break…", onPick: () => breakDialog(w) }),
            ...feature("highlight", { label: "Highlight…", onPick: () => highlightDialog(w) }),
            ...feature("aggregate", { label: "Aggregate…", onPick: () => aggregateDialog(w) }),
            ...feature("compute", { label: "Compute…", onPick: () => computeDialog(w) }),
        ],
        [
            ...feature("groupBy", { label: "Group By…", onPick: () => groupByDialog(w) }),
            ...feature("pivot", { label: "Pivot…", onPick: () => pivotDialog(w) }),
            ...feature("chart", { label: "Chart…", onPick: () => chartDialog(w) }),
        ],
    ]);
    const report = [
        ...feature("savedReports",
            ...(canSave ? [{ label: "Save", onPick: () => saveDialog(w, { asNew: false }) }] : []),
            { label: "Save As…", onPick: () => saveDialog(w, { asNew: true }) },
            ...(canSave ? [{ label: "Delete…", onPick: () => deleteCurrentSaved(w) }] : [])),
        // Reset stays as long as the doc can diverge at all — it is the way back
        // from a state the disabled dialogs could no longer undo.
        ...(anyMutableFeature(w) ? [{ label: "Reset", onPick: () => resetWorkingCopy(w) }] : []),
    ];
    if (report.length) items.push({ heading: "Report" }, ...report);
    if (featureEnabled(w, "download"))
        items.push({ heading: "Download" }, { label: "CSV", onPick: () => exportCsv(w) });
    return items;
}

export function openActionsMenu(w, anchor) {
    popupMenu(anchor, actionsMenuItems(w));
}

export function openHeaderMenu(w, col, anchor) {
    const mode = w.doc.view?.mode ?? "grid";
    const feature = (name, ...entries) => featureEnabled(w, name) ? entries : [];
    const sortItems = feature("sort",
        { label: "Sort Ascending", onPick: () => w.applyOrBanner(d => { d.sorts = [{ col, dir: "asc" }]; }) },
        { label: "Sort Descending", onPick: () => w.applyOrBanner(d => { d.sorts = [{ col, dir: "desc" }]; }) });
    if (mode !== "grid") {
        if (sortItems.length) popupMenu(anchor, sortItems);
        return;
    }

    const visible = visibleColumnNames(w);
    const breaking = (w.doc.breaks ?? []).includes(col);
    const items = joinSections([
        sortItems,
        [
            ...feature("rename", { label: "Rename…", onPick: () => renameDialog(w, col) }),
            ...feature("columns", {
                label: "Hide Column",
                disabled: visible.length <= 1,
                onPick: () => w.applyOrBanner(d => { d.columns = visible.filter(n => n !== col); }),
            }),
            ...feature("controlBreak", {
                label: breaking ? "Remove Control Break" : "Control Break",
                checked: breaking,
                onPick: () => w.applyOrBanner(d => {
                    d.breaks = breaking ? (d.breaks ?? []).filter(b => b !== col) : [...(d.breaks ?? []), col];
                }),
            }),
        ],
        feature("filter", { label: "Filter…", onPick: () => filterDialog(w, { col }) }),
    ]);
    if (items.length) popupMenu(anchor, items);
}
