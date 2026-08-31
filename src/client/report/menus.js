// The two popup menus: the toolbar Actions menu and the per-column header menu. Menus are pure
// dispatch: every entry opens a dialog or applies a one-line document mutation; nothing here owns
// state of its own. Every entry is gated by the definition's feature whitelist and enabled per
// the active table's capabilities: the same Columns/Compute/Filter/Sort/Highlight surfaces
// operate on whichever named table is active.

import { popupMenu } from "../core/menu.js";
import { anyMutableFeature, columnFilterable, columnHelp, columnSortable, featureEnabled } from "./schema.js";
import { tableContext, visibleTableColumnNames } from "./table.js";
import { sameColumn } from "./state.js";
import { columnSettingsDialog, columnsDialog, renameDialog } from "./dialogs/columns.js";
import { filterDialog, computeDialog, highlightDialog } from "./dialogs/rules.js";
import { paginationDialog, sortDialog, breakDialog, aggregateDialog } from "./dialogs/grid.js";
import { groupByDialog, pivotDialog, chartDialog } from "./dialogs/view.js";
import { saveDialog } from "./dialogs/save.js";
import { canManageCurrentSaved, deleteCurrentSaved, resetWorkingCopy } from "./saved.js";
import { downloadExport } from "./export.js";

/**
 * Ordinary table features have the same header surface regardless of which shape composable precedes
 * them.
 *
 * @param {object} w - The report controller containing the feature whitelist.
 * @returns {boolean} Whether any header-menu feature is enabled for the definition.
 */
export function headerMenuAvailable(w) {
    const features = ["sort", "rename", "columnSettings", "columns", "controlBreak", "filter"];
    return features.some(f => featureEnabled(w, f));
}

/**
 * Flattens menu sections and inserts separators between non-empty groups.
 *
 * @param {Array<object|string>} sections - The menu sections to flatten while retaining meaningful separators.
 * @returns {Array<object|string>} The flattened menu entries with separators between non-empty sections.
 */
const joinSections = sections => sections.filter(s => s.length)
    .flatMap((section, i) => i === 0 ? section : ["-", ...section]);

// Invariant: the Actions menu entries the whitelist leaves standing. Exported so the toolbar
// can hide the Actions button when nothing remains. Entries the active table cannot use stay
// visible but disabled. The menu shape is stable while the active table changes.
/**
 * Builds the actions-menu entries allowed by the current schema and state.
 *
 * @param {object} w - The report controller containing state, schema features, table capabilities, and actions.
 * @returns {Array<object|string>} Action, heading, and separator entries in display order.
 */
export function actionsMenuItems(w) {
    const ctx = w.doc ? tableContext(w) : null;
    const caps = ctx?.caps ?? {};
    const feature = (name, ...entries) => featureEnabled(w, name) ? entries : [];
    const canSave = canManageCurrentSaved(w);
    const items = joinSections([
        [
            ...feature("columns", { label: w.t("menu.columns"), disabled: !caps.columns, onPick: () => columnsDialog(w) }),
            ...feature("columnSettings", { label: w.t("menu.columnSettings"), disabled: !caps.columnSettings, onPick: () => columnSettingsDialog(w) }),
            ...feature("filter", { label: w.t("menu.filter"), disabled: !caps.filter, onPick: () => filterDialog(w, {}) }),
            ...feature("sort", { label: w.t("menu.sort"), disabled: !caps.sort, onPick: () => sortDialog(w) }),
            ...feature("pagination", {
                label: w.t("menu.pagination"),
                disabled: !caps.pagination,
                onPick: () => paginationDialog(w),
            }),
        ],
        [
            ...feature("controlBreak", { label: w.t("menu.controlBreak"), disabled: !caps.break, onPick: () => breakDialog(w) }),
            ...feature("highlight", { label: w.t("menu.highlight"), disabled: !caps.highlight, onPick: () => highlightDialog(w) }),
            ...feature("aggregate", { label: w.t("menu.aggregate"), disabled: !caps.aggregate, onPick: () => aggregateDialog(w) }),
            ...feature("compute", { label: w.t("menu.compute"), disabled: !caps.compute, onPick: () => computeDialog(w) }),
        ],
        [
            ...feature("groupBy", { label: w.t("menu.groupBy"), onPick: () => groupByDialog(w) }),
            ...feature("pivot", { label: w.t("menu.pivot"), onPick: () => pivotDialog(w) }),
            ...feature("chart", { label: w.t("menu.chart"), onPick: () => chartDialog(w) }),
        ],
    ]);
    const report = [
        ...feature("savedReports",
            ...(canSave ? [{ label: w.t("menu.save"), onPick: () => saveDialog(w, { asNew: false }) }] : []),
            { label: w.t("menu.saveAs"), onPick: () => saveDialog(w, { asNew: true }) },
            ...(canSave ? [{ label: w.t("menu.delete"), onPick: () => deleteCurrentSaved(w) }] : [])),
        // Reset stays as long as the document can diverge at all. It is the way back from a state
        // the disabled dialogs could no longer undo.
        ...(anyMutableFeature(w) ? [{ label: w.t("menu.reset"), onPick: () => resetWorkingCopy(w) }] : []),
    ];
    if (report.length) items.push({ heading: w.t("menu.report") }, ...report);
    if (featureEnabled(w, "download"))
        items.push({ heading: w.t("menu.download") }, { label: w.t("menu.csv"), onPick: () => downloadExport(w, "csv") });
    return items;
}

/**
 * Builds and opens the report actions menu beside its invoking control.
 *
 * @param {object} w - The report controller used to build menu entries.
 * @param {Element} anchor - The Actions control that anchors and owns the popup.
 * @returns {void} No value.
 *
 * Side effects: closes any existing popup and mounts a newly wired Actions menu.
 */
export function openActionsMenu(w, anchor) {
    popupMenu(anchor, actionsMenuItems(w));
}

/**
 * Builds and opens the selected column's header menu.
 *
 * @param {object} w - The report controller containing active-table capabilities and column metadata.
 * @param {string} col - The logical column identifier.
 * @param {Element} anchor - The column-header button that anchors and owns the popup.
 * @returns {void} No value.
 *
 * Side effects: may mount a popup whose actions mutate report state or open editors.
 */
export function openHeaderMenu(w, col, anchor) {
    const ctx = tableContext(w);
    const feature = (name, ...entries) => featureEnabled(w, name) ? entries : [];

    // Sorting follows the current table, including generated Pivot cells.
    const sortable = ctx.caps.sort && columnSortable(w, col);
    const sortItems = sortable
        ? feature("sort",
            {
                label: w.t("menu.sortAscending"),
                onPick: () => w.applyOrBanner(d =>
                    ctx.edit(d, "sort", node => { node.sorts = [{ col, dir: "asc" }]; })),
            },
            {
                label: w.t("menu.sortDescending"),
                onPick: () => w.applyOrBanner(d =>
                    ctx.edit(d, "sort", node => { node.sorts = [{ col, dir: "desc" }]; })),
            })
        : [];

    const presentation = [
        ...(ctx.caps.rename ? feature("rename", { label: w.t("menu.rename"), onPick: () => renameDialog(w, col) }) : []),
        ...(ctx.caps.columnSettings
            ? feature("columnSettings", { label: w.t("menu.columnSettings"), onPick: () => columnSettingsDialog(w, col) })
            : []),
    ];

    // Hiding is simply a terminal select composable. Dimensions and generated columns obey the
    // same rule as every other column.
    if (ctx.caps.columns && ctx.caps.visibility) {
        const visible = visibleTableColumnNames(ctx, w);
        presentation.push(...feature("columns", {
            label: w.t("menu.hideColumn"),
            disabled: visible.length <= 1,
            onPick: () => w.applyOrBanner(d => ctx.edit(d, "select", node => {
                node.columns = visible.filter(n => !sameColumn(n, col));
            })),
        }));
    }

    if (ctx.caps.break && columnSortable(w, col)) {
        const breaking = (ctx.node(w.doc, "break")?.breaks ?? []).some(b => sameColumn(b, col));
        presentation.push(...feature("controlBreak", {
            label: breaking ? w.t("menu.removeControlBreak") : w.t("break.title"),
            checked: breaking,
            onPick: () => w.applyOrBanner(d => ctx.edit(d, "break", node => {
                node.breaks = breaking
                    ? (node.breaks ?? []).filter(b => !sameColumn(b, col))
                    : [...(node.breaks ?? []), col];
            })),
        }));
    }

    const filterable = ctx.caps.filter && columnFilterable(w, col);
    const filterItems = filterable
        ? feature("filter", { label: w.t("menu.filter"), onPick: () => filterDialog(w, { col }) })
        : [];

    const items = joinSections([sortItems, presentation, filterItems]);
    // The definition's help text closes the menu. It is reachable wherever the menu itself is (a
    // report whose whitelist empties the menu offers no help path; accepted and documented).
    const help = columnHelp(w, col);
    if (help) items.push(...(items.length ? ["-"] : []), { note: help });
    if (items.length) popupMenu(anchor, items);
}
