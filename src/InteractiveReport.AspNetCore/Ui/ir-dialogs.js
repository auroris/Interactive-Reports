// The Actions-menu dialogs. Every apply goes through w.apply(mutate): the widget
// clones the state doc, mutates, re-queries, and rolls back on failure — so a
// validation problem surfaces inside the dialog and the grid never breaks.

import { el, labeled, sel, openDialog } from "./ir-ui.js";
import { FN_LABELS } from "./ir-render.js";

const DIR_OPTIONS = [{ value: "asc", label: "Ascending" }, { value: "desc", label: "Descending" }];
function colOptions(w, { none } = {}) {
    const opts = w.pickable().map(c => ({ value: c.name, label: c.computed ? `ƒ ${c.label}` : c.label }));
    return none ? [{ value: "", label: none }, ...opts] : opts;
}

// --- shared: rows-of-controls pattern ---------------------------------------

function rowList(container, items, buildRow, { addLabel = "Add", max } = {}) {
    const addRow = item => {
        if (max && container.querySelectorAll(".ir-dlgrow").length >= max) return;
        const row = el("div", { class: "ir-dlgrow" });
        buildRow(row, item);
        row.append(el("button", {
            type: "button", class: "ir-btn ir-row-x", title: "Remove", "aria-label": "Remove row",
            onclick: () => row.remove(),
        }, "×"));
        container.append(row);
    };
    items.forEach(addRow);
    if (items.length === 0) addRow(null);
    const addButton = el("button", { type: "button", class: "ir-btn ir-add-row", onclick: () => addRow(null) }, `+ ${addLabel}`);
    return { addButton, read: () => [...container.querySelectorAll(".ir-dlgrow")].map(r => r._read()).filter(x => x != null) };
}

// --- shared: expression-rule editor ------------------------------------------

function expressionEditor(w, { initial, placeholder, result, columns }) {
    const exprInp = el("textarea", {
        class: "ir-textarea", rows: result === "predicate" ? 4 : 3, spellcheck: false, placeholder,
    });
    exprInp.value = initial ?? "";
    const availableColumns = columns ?? w.pickable();

    const insert = token => {
        const at = exprInp.selectionStart ?? exprInp.value.length;
        exprInp.setRangeText(token, at, exprInp.selectionEnd ?? at, "end");
        exprInp.focus();
    };
    const tokenBtn = (label, token) =>
        el("button", { type: "button", class: "ir-token", onclick: () => insert(token) }, label);

    const conditionTokens = [
        tokenBtn("=", " = "), tokenBtn("≠", " <> "),
        tokenBtn("<", " < "), tokenBtn("≤", " <= "),
        tokenBtn(">", " > "), tokenBtn("≥", " >= "),
        tokenBtn("AND", " AND "), tokenBtn("OR", " OR "), tokenBtn("NOT", "NOT "),
        tokenBtn("BETWEEN", " BETWEEN  AND "),
        tokenBtn("IS NULL", " IS NULL"), tokenBtn("IS NOT NULL", " IS NOT NULL"),
    ];
    if (result === "value")
        conditionTokens.unshift(tokenBtn("CASE WHEN … END", "CASE WHEN  THEN  ELSE  END"));

    const wrap = el("div", { class: "ir-condition" },
        labeled("Expression", exprInp),
        el("div", { class: "ir-token-group" },
            el("span", { class: "ir-field-label" }, "Columns"),
            el("div", {}, ...availableColumns.map(c => tokenBtn(c.label, c.name)))),
        el("div", { class: "ir-token-group" },
            el("span", { class: "ir-field-label" }, "Functions"),
            el("div", {}, ...w.expressionFunctions().map(f => tokenBtn(f, `${f}(`)))),
        el("div", { class: "ir-token-group" },
            el("span", { class: "ir-field-label" }, "Conditions"),
            el("div", {}, ...conditionTokens)),
        el("p", { class: "ir-dialog-note" },
            result === "predicate"
                ? "The expression must resolve to true or false. Strings use single quotes; dates use TO_DATE('YYYY-MM-DD')."
                : "The expression must produce a number, text, or date value. Use CASE WHEN to turn conditions into values."));
    wrap._read = () => {
        const expr = exprInp.value.trim();
        if (!expr) throw new Error(result === "predicate" ? "Enter a condition expression" : "Enter an expression");
        return expr;
    };
    return wrap;
}

// --- Columns -----------------------------------------------------------------

export function columnsDialog(w) {
    const universe = w.pickable();
    const byName = new Map(universe.map(c => [c.name, c]));
    const displayedNames = w.visibleColumnNames().filter(n => byName.has(n));
    const hiddenNames = universe.map(c => c.name).filter(n => !displayedNames.includes(n));

    const listbox = names => el("select", { multiple: true, size: 12, class: "ir-shuttle-list" },
        ...names.map(n => new Option(byName.get(n).computed ? `ƒ ${byName.get(n).label}` : byName.get(n).label, n)));
    const hidden = listbox(hiddenNames);
    const shown = listbox(displayedNames);

    const move = (from, to, all) => {
        const picked = all ? [...from.options] : [...from.selectedOptions];
        for (const o of picked) to.append(o);
    };
    const nudge = delta => {
        const opts = [...shown.selectedOptions];
        if (delta > 0) opts.reverse();
        for (const o of opts) {
            const sibling = delta < 0 ? o.previousElementSibling : o.nextElementSibling;
            if (!sibling || sibling.selected) continue;
            delta < 0 ? sibling.before(o) : sibling.after(o);
        }
    };
    const btn = (label, title, onclick) =>
        el("button", { type: "button", class: "ir-btn", title, onclick }, label);

    openDialog({
        owner: w,
        title: "Select Columns",
        width: "34rem",
        build: body => body.append(
            el("div", { class: "ir-shuttle" },
                el("div", { class: "ir-shuttle-col" }, el("div", { class: "ir-shuttle-head" }, "Do Not Display"), hidden),
                el("div", { class: "ir-shuttle-btns" },
                    btn("›", "Display selected", () => move(hidden, shown)),
                    btn("‹", "Hide selected", () => move(shown, hidden)),
                    btn("»", "Display all", () => move(hidden, shown, true)),
                    btn("«", "Hide all", () => move(shown, hidden, true))),
                el("div", { class: "ir-shuttle-col" }, el("div", { class: "ir-shuttle-head" }, "Display in Report"), shown),
                el("div", { class: "ir-shuttle-btns" },
                    btn("↑", "Move up", () => nudge(-1)),
                    btn("↓", "Move down", () => nudge(1))))),
        onApply: () => {
            const names = [...shown.options].map(o => o.value);
            if (!names.length) throw new Error("Display at least one column");
            return w.apply(d => { d.columns = names; });
        },
    });
}

// --- Rename ------------------------------------------------------------------

/// Column headings only: a base column writes doc.labels (kept as an explicit map so
/// clearing an inherited default sticks); a computed column edits its rule's label.
/// The expression name never changes — that is always the real column name or id.
export function renameDialog(w, col) {
    const computedRule = (w.doc.computed ?? []).find(c => c.id === col);
    const schemaDefault = computedRule
        ? computedRule.id
        : w.schema?.columns?.find(c => c.name === col)?.label ?? col;
    const input = el("input", {
        class: "ir-input", type: "text",
        value: computedRule ? computedRule.label ?? "" : w.doc.labels?.[col] ?? "",
        placeholder: schemaDefault,
    });

    openDialog({
        owner: w,
        title: "Rename Column",
        width: "24rem",
        build: body => body.append(
            labeled("Column Heading", input),
            el("p", { class: "ir-dialog-note" },
                `Changes the heading only — expressions keep using ${col}. Leave blank to restore "${schemaDefault}".`)),
        onApply: () => {
            const label = input.value.trim();
            return w.apply(d => {
                if (computedRule) {
                    const rule = (d.computed ?? []).find(c => c.id === col);
                    if (rule) rule.label = label || rule.id;
                } else if (!label || label === schemaDefault) {
                    if (d.labels) delete d.labels[col];
                } else {
                    (d.labels ??= {})[col] = label;
                }
            });
        },
    });
}

// --- Filter ------------------------------------------------------------------

export function filterDialog(w, { editIndex, col } = {}) {
    const existing = editIndex !== undefined ? w.doc.filters?.[editIndex] : undefined;
    const condition = expressionEditor(w, {
        initial: existing?.expr ?? (col ? `${col} = ` : ""),
        placeholder: "e.g. AMOUNT > 1000 AND STATUS <> 'CANCELLED'",
        result: "predicate",
    });

    openDialog({
        owner: w,
        title: editIndex !== undefined ? "Edit Filter" : "Add Filter",
        width: "30rem",
        build: body => body.append(condition),
        onApply: () => {
            const rule = { expr: condition._read(), enabled: existing?.enabled ?? true };
            return w.apply(d => {
                d.filters ??= [];
                if (editIndex !== undefined) d.filters[editIndex] = rule;
                else d.filters.push(rule);
            });
        },
    });
}

// --- Sort --------------------------------------------------------------------

export function sortDialog(w) {
    const container = el("div", {});
    const list = rowList(container, w.doc.sorts ?? [], (row, item) => {
        const colSel = sel(colOptions(w, { none: "— Select —" }), item?.col ?? "");
        const dirSel = sel(DIR_OPTIONS, item?.dir ?? "asc");
        row.append(colSel, dirSel);
        row._read = () => colSel.value ? { col: colSel.value, dir: dirSel.value } : null;
    }, { addLabel: "Sort", max: 6 });

    openDialog({
        owner: w,
        title: "Sort",
        width: "26rem",
        build: body => body.append(container, list.addButton,
            el("p", { class: "ir-dialog-note" }, "Control-break columns always sort first.")),
        onApply: () => w.apply(d => { d.sorts = list.read(); }),
    });
}

// --- Control Break -----------------------------------------------------------

export function breakDialog(w) {
    const container = el("div", {});
    const list = rowList(container, (w.doc.breaks ?? []).map(b => ({ col: b })), (row, item) => {
        const colSel = sel(colOptions(w, { none: "— Select —" }), item?.col ?? "");
        row.append(colSel);
        row._read = () => colSel.value || null;
    }, { addLabel: "Break Column", max: 3 });

    openDialog({
        owner: w,
        title: "Control Break",
        width: "24rem",
        build: body => body.append(container, list.addButton,
            el("p", { class: "ir-dialog-note" }, "Rows group under a heading per break value; aggregates subtotal per group.")),
        onApply: () => w.apply(d => {
            d.breaks = [...new Set(list.read())];
        }),
    });
}

// --- Aggregate ---------------------------------------------------------------

export function aggregateDialog(w) {
    const container = el("div", {});
    const list = rowList(container, w.doc.aggregates ?? [], (row, item) => {
        const colSel = sel(colOptions(w, { none: "— Select —" }), item?.col ?? "");
        const fnSel = el("select", { class: "ir-select" });
        const refreshFns = keep => {
            const fns = colSel.value ? w.fnsFor(w.typeOf(colSel.value)) : [];
            fnSel.replaceChildren(...fns.map(f => new Option(FN_LABELS[f] ?? f, f)));
            if (keep && fns.includes(keep)) fnSel.value = keep;
        };
        colSel.onchange = () => refreshFns(fnSel.value);
        refreshFns(item?.fn);
        row.append(fnSel, el("span", { class: "ir-row-of" }, "of"), colSel);
        row._read = () => colSel.value && fnSel.value ? { col: colSel.value, fn: fnSel.value } : null;
    }, { addLabel: "Aggregate" });

    openDialog({
        owner: w,
        title: "Aggregate",
        width: "28rem",
        build: body => body.append(container, list.addButton,
            el("p", { class: "ir-dialog-note" }, "Computed over the whole filtered set — grand total and per-break subtotals.")),
        onApply: () => w.apply(d => { d.aggregates = list.read(); }),
    });
}

// --- Compute -----------------------------------------------------------------

export function computeDialog(w, editIndex) {
    const existing = editIndex !== undefined ? w.doc.computed?.[editIndex] : undefined;
    const labelInp = el("input", { class: "ir-input", type: "text", value: existing?.label ?? "", placeholder: "Column heading" });
    const expression = expressionEditor(w, {
        initial: existing?.expr,
        placeholder: "e.g. ROUND(AMOUNT * 1.0825, 2)",
        result: "value",
        columns: w.schema.columns,
    });

    openDialog({
        owner: w,
        title: editIndex !== undefined ? "Edit Computed Column" : "Compute Column",
        width: "36rem",
        build: body => body.append(
            labeled("Column Heading", labelInp),
            expression),
        onApply: () => {
            const expr = expression._read();
            const ids = (w.doc.computed ?? []).map(c => c.id);
            let n = 1;
            while (ids.includes(`c${n}`)) n++;
            const rule = {
                id: existing?.id ?? `c${n}`,
                label: labelInp.value.trim() || (existing?.id ?? `c${n}`),
                expr,
                enabled: existing?.enabled ?? true,
            };
            return w.apply(d => {
                d.computed ??= [];
                if (editIndex !== undefined) d.computed[editIndex] = rule;
                else d.computed.push(rule);
            });
        },
    });
}

// --- Highlight ---------------------------------------------------------------

export function highlightDialog(w, editIndex) {
    const existing = editIndex !== undefined ? w.doc.highlights?.[editIndex] : undefined;

    const scopeSel = sel([{ value: "row", label: "Row" }, { value: "cell", label: "Cell" }], existing?.scope ?? "row");
    const targetSel = sel(colOptions(w), existing?.col);
    const targetField = labeled("Highlight Column", targetSel);
    const condition = expressionEditor(w, {
        initial: existing?.expr,
        placeholder: "e.g. ROUND(AMOUNT, 2) > 1000 OR NOTES IS NULL",
        result: "predicate",
    });

    const bgInp = el("input", { type: "color", class: "ir-color", value: existing?.style?.bg ?? "#fff3cd" });
    const bgOn = el("input", { type: "checkbox", checked: existing ? !!existing.style?.bg : true });
    const fgInp = el("input", { type: "color", class: "ir-color", value: existing?.style?.fg ?? "#9f1239" });
    const fgOn = el("input", { type: "checkbox", checked: !!existing?.style?.fg });

    const syncScope = () => { targetField.hidden = scopeSel.value !== "cell"; };
    scopeSel.onchange = syncScope;
    syncScope();

    openDialog({
        owner: w,
        title: editIndex !== undefined ? "Edit Highlight" : "Highlight",
        width: "30rem",
        build: body => body.append(
            labeled("Highlight", scopeSel),
            targetField,
            el("div", { class: "ir-field-label ir-condition-head" }, "When"),
            condition,
            el("div", { class: "ir-colors" },
                el("label", { class: "ir-color-pick" }, bgOn, "Background", bgInp),
                el("label", { class: "ir-color-pick" }, fgOn, "Text", fgInp))),
        onApply: () => {
            const expr = condition._read();
            if (!bgOn.checked && !fgOn.checked) throw new Error("Pick a background or text color");
            const ids = (w.doc.highlights ?? []).map(h => h.id);
            let n = 1;
            while (ids.includes(`h${n}`)) n++;
            const rule = {
                id: existing?.id ?? `h${n}`,
                enabled: existing?.enabled ?? true,
                scope: scopeSel.value,
                expr,
            };
            if (scopeSel.value === "cell") rule.col = targetSel.value;
            rule.style = {};
            if (bgOn.checked) rule.style.bg = bgInp.value;
            if (fgOn.checked) rule.style.fg = fgInp.value;
            return w.apply(d => {
                d.highlights ??= [];
                if (editIndex !== undefined) d.highlights[editIndex] = rule;
                else d.highlights.push(rule);
            });
        },
    });
}

// --- Group By / Pivot --------------------------------------------------------

function dimList(w, initial, { addLabel, max }) {
    const container = el("div", {});
    const list = rowList(container, (initial ?? []).map(c => ({ col: c })), (row, item) => {
        const colSel = sel(colOptions(w, { none: "— Select —" }), item?.col ?? "");
        row.append(colSel);
        row._read = () => colSel.value || null;
    }, { addLabel, max });
    return { container, list };
}

function valueList(w, initial) {
    const container = el("div", {});
    const list = rowList(container, initial ?? [], (row, item) => {
        const colSel = sel(colOptions(w, { none: "— Select —" }), item?.col ?? "");
        const fnSel = el("select", { class: "ir-select" });
        const refreshFns = keep => {
            const fns = colSel.value ? w.fnsFor(w.typeOf(colSel.value)) : [];
            fnSel.replaceChildren(...fns.map(f => new Option(FN_LABELS[f] ?? f, f)));
            if (keep && fns.includes(keep)) fnSel.value = keep;
        };
        colSel.onchange = () => refreshFns(fnSel.value);
        refreshFns(item?.fn);
        row.append(fnSel, el("span", { class: "ir-row-of" }, "of"), colSel);
        row._read = () => colSel.value && fnSel.value ? { col: colSel.value, fn: fnSel.value } : null;
    }, { addLabel: "Value" });
    return { container, list };
}

export function groupByDialog(w) {
    const active = w.doc.view?.mode === "groupBy" ? w.doc.view : w.viewMemory.groupBy;
    const dims = dimList(w, active?.groupBy, { addLabel: "Group Column", max: 3 });
    const values = valueList(w, active?.values);

    openDialog({
        owner: w,
        title: "Group By",
        width: "30rem",
        build: body => body.append(
            el("div", { class: "ir-field-label" }, "Group by"),
            dims.container, dims.list.addButton,
            el("div", { class: "ir-field-label ir-gap-above" }, "Aggregate values"),
            values.container, values.list.addButton,
            el("p", { class: "ir-dialog-note" }, "A row count per group is always included.")),
        onApply: () => {
            const groupBy = [...new Set(dims.list.read())];
            if (!groupBy.length) throw new Error("Pick at least one group column");
            const spec = { mode: "groupBy", groupBy, values: values.list.read() };
            return w.apply(d => { d.view = spec; }).then(() => { w.viewMemory.groupBy = spec; });
        },
    });
}

export function pivotDialog(w) {
    const active = w.doc.view?.mode === "pivot" ? w.doc.view : w.viewMemory.pivot;
    const rows = dimList(w, active?.rows, { addLabel: "Row Column", max: 2 });
    const cols = dimList(w, active?.cols, { addLabel: "Column", max: 2 });
    const values = valueList(w, active?.values);

    openDialog({
        owner: w,
        title: "Pivot",
        width: "30rem",
        build: body => body.append(
            el("div", { class: "ir-field-label" }, "Rows"),
            rows.container, rows.list.addButton,
            el("div", { class: "ir-field-label ir-gap-above" }, "Columns (become headings)"),
            cols.container, cols.list.addButton,
            el("div", { class: "ir-field-label ir-gap-above" }, "Values"),
            values.container, values.list.addButton,
            el("p", { class: "ir-dialog-note" }, "No values = a count per cell.")),
        onApply: () => {
            const rowDims = [...new Set(rows.list.read())];
            const colDims = [...new Set(cols.list.read())].filter(c => !rowDims.includes(c));
            if (!rowDims.length || !colDims.length) throw new Error("Pick at least one row column and one distinct column heading");
            const spec = { mode: "pivot", rows: rowDims, cols: colDims, values: values.list.read() };
            return w.apply(d => { d.view = spec; }).then(() => { w.viewMemory.pivot = spec; });
        },
    });
}

// --- Chart -------------------------------------------------------------------

const CHART_TYPES = [
    { value: "bar", label: "Bar" },
    { value: "line", label: "Line" },
    { value: "area", label: "Line with Area" },
    { value: "pie", label: "Pie" },
];

export function chartDialog(w) {
    const active = w.doc.view?.mode === "chart" ? w.doc.view : w.viewMemory.chart;
    const chartable = w.pickable().filter(c => c.type !== "other");

    const typeSel = sel(CHART_TYPES, active?.type ?? "bar");
    const labelSel = sel([
        { value: "", label: "— Select —" },
        ...chartable.map(c => ({ value: c.name, label: c.computed ? `ƒ ${c.label}` : c.label })),
    ], active?.label ?? "");
    const valueSel = sel([
        { value: "", label: "— Row Count —" },
        ...w.pickable().map(c => ({ value: c.name, label: c.computed ? `ƒ ${c.label}` : c.label })),
    ], active?.value ?? "");

    const fnSel = el("select", { class: "ir-select" });
    const refreshFns = keep => {
        const options = [];
        if (!valueSel.value) {
            options.push({ value: "count", label: FN_LABELS.count });
        } else {
            const type = w.typeOf(valueSel.value);
            options.push(...w.chartFnsFor(type).map(f => ({ value: f, label: FN_LABELS[f] ?? f })));
            if (type === "number") options.push({ value: "", label: "— Each Row —" });
        }
        fnSel.replaceChildren(...options.map(o => new Option(o.label, o.value)));
        if (keep !== undefined && [...fnSel.options].some(o => o.value === keep)) fnSel.value = keep;
    };
    valueSel.onchange = () => refreshFns(fnSel.value);
    refreshFns(active ? (active.fn ?? "") : undefined);

    const orientSel = sel([
        { value: "vertical", label: "Vertical" },
        { value: "horizontal", label: "Horizontal" },
    ], active?.orientation ?? "vertical");
    const sortBySel = sel([{ value: "label", label: "Label" }, { value: "value", label: "Value" }], active?.sort?.by ?? "label");
    const sortDirSel = sel(DIR_OPTIONS, active?.sort?.dir ?? "asc");

    const labelTitleInp = el("input", { class: "ir-input", type: "text", value: active?.labelAxisTitle ?? "", placeholder: "Optional" });
    const valueTitleInp = el("input", { class: "ir-input", type: "text", value: active?.valueAxisTitle ?? "", placeholder: "Optional" });

    const orientField = labeled("Orientation", orientSel);
    const labelTitleField = labeled("Label Axis Title", labelTitleInp);
    const valueTitleField = labeled("Value Axis Title", valueTitleInp);
    const syncType = () => {
        const pie = typeSel.value === "pie";
        orientField.hidden = pie;
        labelTitleField.hidden = pie;
        valueTitleField.hidden = pie;
    };
    typeSel.onchange = syncType;
    syncType();

    openDialog({
        owner: w,
        title: "Chart",
        width: "30rem",
        build: body => body.append(
            labeled("Chart Type", typeSel),
            labeled("Label", labelSel),
            el("div", { class: "ir-field" },
                el("span", { class: "ir-field-label" }, "Value"),
                el("div", { class: "ir-dlgrow ir-chart-valuerow" },
                    fnSel, el("span", { class: "ir-row-of" }, "of"), valueSel)),
            orientField,
            el("div", { class: "ir-field" },
                el("span", { class: "ir-field-label" }, "Sort"),
                el("div", { class: "ir-dlgrow" }, sortBySel, sortDirSel)),
            labelTitleField,
            valueTitleField,
            el("p", { class: "ir-dialog-note" },
                "The chart draws the whole filtered result — never just the visible page — up to the report's point limit.")),
        onApply: () => {
            if (!labelSel.value) throw new Error("Pick a label column");
            const spec = {
                mode: "chart",
                type: typeSel.value,
                label: labelSel.value,
                sort: { by: sortBySel.value, dir: sortDirSel.value },
            };
            if (valueSel.value) spec.value = valueSel.value;
            if (fnSel.value) spec.fn = fnSel.value;
            if (typeSel.value !== "pie") {
                spec.orientation = orientSel.value;
                if (labelTitleInp.value.trim()) spec.labelAxisTitle = labelTitleInp.value.trim();
                if (valueTitleInp.value.trim()) spec.valueAxisTitle = valueTitleInp.value.trim();
            }
            return w.apply(d => { d.view = spec; }).then(() => { w.viewMemory.chart = spec; });
        },
    });
}

// --- Save --------------------------------------------------------------------

export function saveDialog(w, { asNew }) {
    const updating = !asNew && w.canManageCurrentSaved();
    const titleInp = el("input", {
        class: "ir-input", type: "text", maxLength: 200,
        value: updating ? w.currentSaved.title : "",
        placeholder: "Saved report name",
    });
    const globalChk = el("input", {
        type: "checkbox",
        checked: updating ? !!w.currentSaved.isGlobal : false,
    });

    openDialog({
        owner: w,
        title: updating ? "Save Report" : "Save Report As",
        width: "26rem",
        applyLabel: "Save",
        build: body => {
            body.append(labeled("Name", titleInp));
            if (w.whoami?.isAdministrator)
                body.append(el("label", { class: "ir-checkline" }, globalChk, "Global — visible to everyone with access to this report"));
        },
        onApply: () => {
            const title = titleInp.value.trim();
            if (!title) throw new Error("Enter a name");
            return w.saveReport({
                title,
                isGlobal: w.whoami?.isAdministrator ? globalChk.checked : false,
                asNew: !updating,
            });
        },
    });
}
