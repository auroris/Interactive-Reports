// The two popup menus: the toolbar Actions menu and the per-column header menu.
// Menus are pure dispatch — every entry opens a dialog or applies a one-line
// doc mutation; nothing here owns state of its own. Every entry is gated by the
// definition's feature whitelist and enabled per the current stage's
// capabilities (stage.js): the same Columns/Compute/Sort/Highlight surfaces
// operate on whichever table the pipeline's tail produces, while Filter always
// edits the source stage.

import { popupMenu } from "../core/menu.js";
import { anyMutableFeature, featureEnabled } from "./schema.js";
import { stageContext } from "./stage.js";
import { sameColumn } from "./state.js";
import { columnSettingsDialog, columnsDialog, renameDialog } from "./dialogs/columns.js";
import { filterDialog, computeDialog, highlightDialog } from "./dialogs/rules.js";
import { paginationDialog, sortDialog, breakDialog, aggregateDialog } from "./dialogs/grid.js";
import { groupByDialog, pivotDialog, chartDialog } from "./dialogs/view.js";
import { saveDialog } from "./dialogs/save.js";
import { canManageCurrentSaved, deleteCurrentSaved, resetWorkingCopy } from "./saved.js";
import { exportCsv } from "./export.js";

/// Features whose entries live in the header menu per mode; if none of them are
/// whitelisted the header offers nothing and should not open (nor look clickable).
export function headerMenuAvailable(w, mode) {
    const features = mode === "grid"
        ? ["sort", "rename", "columnSettings", "columns", "controlBreak", "filter"]
        : mode === "groupBy"
            ? ["sort", "rename", "columnSettings", "columns", "filter"]
            : mode === "pivot"
                ? ["sort", "rename", "columnSettings"]
                : [];
    return features.some(f => featureEnabled(w, f));
}

const joinSections = sections => sections.filter(s => s.length)
    .flatMap((section, i) => i === 0 ? section : ["-", ...section]);

/// The Actions menu entries the whitelist leaves standing. Exported so the
/// toolbar can hide the Actions button when nothing remains. Entries a stage
/// cannot use stay visible but disabled — the menu shape is stable; the table
/// under it changes.
export function actionsMenuItems(w) {
    const ctx = w.doc ? stageContext(w) : null;
    const caps = ctx?.caps ?? {};
    const feature = (name, ...entries) => featureEnabled(w, name) ? entries : [];
    const canSave = canManageCurrentSaved(w);
    const items = joinSections([
        [
            ...feature("columns", { label: "Columns…", disabled: !caps.columns, onPick: () => columnsDialog(w) }),
            ...feature("columnSettings", { label: "Column Settings…", disabled: !caps.columnSettings, onPick: () => columnSettingsDialog(w) }),
            ...feature("filter", { label: "Filter…", onPick: () => filterDialog(w, {}) }),
            ...feature("sort", { label: "Sort…", disabled: !caps.sort, onPick: () => sortDialog(w) }),
            ...feature("pagination", {
                label: "Pagination…",
                disabled: !caps.pagination,
                onPick: () => paginationDialog(w),
            }),
        ],
        [
            ...feature("controlBreak", { label: "Control Break…", disabled: !caps.break, onPick: () => breakDialog(w) }),
            ...feature("highlight", { label: "Highlight…", disabled: !caps.highlight, onPick: () => highlightDialog(w) }),
            ...feature("aggregate", { label: "Aggregate…", disabled: !caps.aggregate, onPick: () => aggregateDialog(w) }),
            ...feature("compute", { label: "Compute…", disabled: !caps.compute, onPick: () => computeDialog(w) }),
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
    const ctx = stageContext(w);
    const feature = (name, ...entries) => featureEnabled(w, name) ? entries : [];
    const isDim = (ctx.dims ?? []).some(d => sameColumn(d, col));
    const column = ctx.columns.find(c => sameColumn(c.name, col));

    // Sorting: grid sorts the source table; group/pivot sort through the group
    // layer (pivot restricted to row dims — cells have no single order column).
    const sortable = ctx.mode === "grid"
        || (ctx.mode === "groupBy" && ctx.caps.sort)
        || (ctx.mode === "pivot" && ctx.caps.sort && isDim);
    const sortItems = sortable
        ? feature("sort",
            {
                label: "Sort Ascending",
                onPick: () => w.applyOrBanner(d => { ctx.sortLayer(d).sorts = [{ col, dir: "asc" }]; }),
            },
            {
                label: "Sort Descending",
                onPick: () => w.applyOrBanner(d => { ctx.sortLayer(d).sorts = [{ col, dir: "desc" }]; }),
            })
        : [];

    if (ctx.mode === "chart") return;

    const presentation = [
        ...(ctx.caps.rename ? feature("rename", { label: "Rename…", onPick: () => renameDialog(w, col) }) : []),
        ...(ctx.caps.columnSettings
            ? feature("columnSettings", { label: "Column Settings…", onPick: () => columnSettingsDialog(w, col) })
            : []),
    ];

    // Hiding: the terminal table's column selection. Group dims stay visible at
    // T0 (hiding a dim makes rows look duplicated); spread output has no column
    // selection at all.
    if (ctx.caps.columns && ctx.caps.visibility && !isDim) {
        const visible = visibleStageColumnNames(ctx, w);
        presentation.push(...feature("columns", {
            label: "Hide Column",
            disabled: visible.length <= 1,
            onPick: () => w.applyOrBanner(d => {
                ctx.columnsLayer(d).columns = visible.filter(n => !sameColumn(n, col));
            }),
        }));
    }

    if (ctx.mode === "grid") {
        const breaking = (ctx.columnsLayer(w.doc).breaks ?? []).some(b => sameColumn(b, col));
        presentation.push(...feature("controlBreak", {
            label: breaking ? "Remove Control Break" : "Control Break",
            checked: breaking,
            onPick: () => w.applyOrBanner(d => {
                const layer = ctx.columnsLayer(d);
                layer.breaks = breaking
                    ? (layer.breaks ?? []).filter(b => !sameColumn(b, col))
                    : [...(layer.breaks ?? []), col];
            }),
        }));
    }

    // Filter always targets the source stage; offer it where the clicked column
    // exists there (grid columns; group/pivot pass-through dims).
    const filterable = ctx.mode === "grid" || (isDim && !column?.metric);
    const filterItems = filterable
        ? feature("filter", { label: "Filter…", onPick: () => filterDialog(w, { col }) })
        : [];

    const items = joinSections([sortItems, presentation, filterItems]);
    if (items.length) popupMenu(anchor, items);
}

/// The terminal table's currently visible column names (explicit selection or all).
export function visibleStageColumnNames(ctx, w) {
    const explicit = ctx.columnsLayer?.(w.doc)?.columns;
    if (explicit?.length) return [...explicit];
    return ctx.columns.map(c => c.name);
}
