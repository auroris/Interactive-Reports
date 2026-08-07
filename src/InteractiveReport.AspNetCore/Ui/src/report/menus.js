// The two popup menus: the toolbar Actions menu and the per-column header menu.
// Menus are pure dispatch — every entry opens a dialog or applies a one-line
// doc mutation; nothing here owns state of its own.

import { popupMenu } from "../core/menu.js";
import { visibleColumnNames } from "./schema.js";
import { columnsDialog, renameDialog } from "./dialogs/columns.js";
import { filterDialog, computeDialog, highlightDialog } from "./dialogs/rules.js";
import { sortDialog, breakDialog, aggregateDialog } from "./dialogs/grid.js";
import { groupByDialog, pivotDialog, chartDialog } from "./dialogs/view.js";
import { saveDialog } from "./dialogs/save.js";
import { canManageCurrentSaved, deleteCurrentSaved, resetWorkingCopy } from "./saved.js";
import { exportCsv } from "./export.js";

export function openActionsMenu(w, anchor) {
    const canSave = canManageCurrentSaved(w);
    popupMenu(anchor, [
        { label: "Columns…", onPick: () => columnsDialog(w) },
        { label: "Filter…", onPick: () => filterDialog(w, {}) },
        { label: "Sort…", onPick: () => sortDialog(w) },
        "-",
        { label: "Control Break…", onPick: () => breakDialog(w) },
        { label: "Highlight…", onPick: () => highlightDialog(w) },
        { label: "Aggregate…", onPick: () => aggregateDialog(w) },
        { label: "Compute…", onPick: () => computeDialog(w) },
        "-",
        { label: "Group By…", onPick: () => groupByDialog(w) },
        { label: "Pivot…", onPick: () => pivotDialog(w) },
        { label: "Chart…", onPick: () => chartDialog(w) },
        { heading: "Report" },
        ...(canSave ? [{ label: "Save", onPick: () => saveDialog(w, { asNew: false }) }] : []),
        { label: "Save As…", onPick: () => saveDialog(w, { asNew: true }) },
        ...(canSave ? [{ label: "Delete…", onPick: () => deleteCurrentSaved(w) }] : []),
        { label: "Reset", onPick: () => resetWorkingCopy(w) },
        { heading: "Download" },
        { label: "CSV", onPick: () => exportCsv(w) },
    ]);
}

export function openHeaderMenu(w, col, anchor) {
    const mode = w.doc.view?.mode ?? "grid";
    const sortItems = [
        { label: "Sort Ascending", onPick: () => w.applyOrBanner(d => { d.sorts = [{ col, dir: "asc" }]; }) },
        { label: "Sort Descending", onPick: () => w.applyOrBanner(d => { d.sorts = [{ col, dir: "desc" }]; }) },
    ];
    if (mode !== "grid") { popupMenu(anchor, sortItems); return; }

    const visible = visibleColumnNames(w);
    const breaking = (w.doc.breaks ?? []).includes(col);
    popupMenu(anchor, [
        ...sortItems,
        "-",
        { label: "Rename…", onPick: () => renameDialog(w, col) },
        {
            label: "Hide Column",
            disabled: visible.length <= 1,
            onPick: () => w.applyOrBanner(d => { d.columns = visible.filter(n => n !== col); }),
        },
        {
            label: breaking ? "Remove Control Break" : "Control Break",
            checked: breaking,
            onPick: () => w.applyOrBanner(d => {
                d.breaks = breaking ? (d.breaks ?? []).filter(b => b !== col) : [...(d.breaks ?? []), col];
            }),
        },
        "-",
        { label: "Filter…", onPick: () => filterDialog(w, { col }) },
    ]);
}
