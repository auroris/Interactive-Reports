// The Actions-menu dialogs. Every apply goes through w.apply(mutate): the widget
// clones the state doc, mutates, re-queries, and rolls back on failure — so a
// validation problem surfaces inside the dialog and the grid never breaks.

import { el, labeled, sel, openDialog } from "./ir-ui.js";
import { FN_LABELS } from "./ir-render.js";

const DIR_OPTIONS = [{ value: "asc", label: "Ascending" }, { value: "desc", label: "Descending" }];
const NO_VALUE_OPS = ["blank", "nblank"];
const LIST_OPS = ["in", "nin"];

const OP_LABELS = {
    eq: "=", ne: "≠", lt: "<", le: "≤", gt: ">", ge: "≥",
    between: "between", in: "in", nin: "not in",
    contains: "contains", ncontains: "does not contain",
    starts: "starts with", ends: "ends with",
    blank: "is blank", nblank: "is not blank",
};

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

// --- shared: typed value editor for filter-like conditions -------------------

function valueEditor(w, colName, op, initial) {
    const type = w.typeOf(colName);
    const wrap = el("span", { class: "ir-value-editor" });

    const input = value => {
        if (type === "bool") {
            const s = sel([{ value: "true", label: "true" }, { value: "false", label: "false" }],
                value === undefined ? "true" : String(value));
            return s;
        }
        const inp = el("input", {
            class: "ir-input",
            type: type === "number" ? "number" : type === "date" ? "date" : "text",
        });
        if (type === "number") inp.step = "any";
        if (value !== undefined && value !== null) inp.value = String(value);
        return inp;
    };
    const coerce = raw => {
        const s = String(raw).trim();
        if (s === "") return null;
        if (type === "number") {
            const n = Number(s);
            if (Number.isNaN(n)) throw new Error(`'${s}' is not a number`);
            return n;
        }
        if (type === "bool") return s === "true";
        return s;
    };
    const readOne = node => coerce(node.value);

    if (NO_VALUE_OPS.includes(op)) {
        wrap._read = () => undefined;
    } else if (op === "between") {
        const arr = Array.isArray(initial) ? initial : [undefined, undefined];
        const lo = input(arr[0]), hi = input(arr[1]);
        wrap.append(lo, el("span", { class: "ir-value-and" }, "and"), hi);
        wrap._read = () => {
            const a = readOne(lo), b = readOne(hi);
            if (a === null || b === null) throw new Error("'between' needs both values");
            return [a, b];
        };
    } else if (LIST_OPS.includes(op)) {
        const inp = el("input", { class: "ir-input ir-input-wide", type: "text", placeholder: "comma, separated, values" });
        if (Array.isArray(initial)) inp.value = initial.join(", ");
        wrap.append(inp);
        wrap._read = () => {
            const list = inp.value.split(",").map(s => s.trim()).filter(s => s !== "").map(coerce);
            if (!list.length) throw new Error("Enter at least one value");
            return list;
        };
    } else {
        const inp = input(Array.isArray(initial) ? initial[0] : initial);
        wrap.append(inp);
        wrap._read = () => {
            const v = readOne(inp);
            if (v === null) throw new Error("Enter a value");
            return v;
        };
    }
    return wrap;
}

/// Column + operator + value trio that rebuilds the value editor when either changes.
function conditionEditor(w, initial, { ops } = {}) {
    const wrap = el("div", { class: "ir-condition" });
    const colSel = sel(colOptions(w), initial?.col ?? w.pickable()[0]?.name);
    const opSel = el("select", { class: "ir-select" });
    const valueSlot = el("span", { class: "ir-value-slot" });

    const opsFor = () => {
        const all = w.opsFor(w.typeOf(colSel.value));
        return ops ? all.filter(o => ops.includes(o)) : all;
    };
    const refreshOps = keep => {
        const list = opsFor();
        opSel.replaceChildren(...list.map(o => new Option(OP_LABELS[o] ?? o, o)));
        opSel.value = keep && list.includes(keep) ? keep : list[0];
    };
    const refreshValue = initialValue => {
        valueSlot.replaceChildren(valueEditor(w, colSel.value, opSel.value, initialValue));
    };
    colSel.onchange = () => { refreshOps(opSel.value); refreshValue(); };
    opSel.onchange = () => refreshValue();

    refreshOps(initial?.op);
    refreshValue(initial?.value);

    wrap.append(labeled("Column", colSel), labeled("Operator", opSel), labeled("Value", valueSlot));
    wrap._read = () => {
        const rule = { col: colSel.value, op: opSel.value };
        const v = valueSlot.firstChild._read();
        if (v !== undefined) rule.value = v;
        return rule;
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

// --- Filter ------------------------------------------------------------------

export function filterDialog(w, { editIndex, col } = {}) {
    const existing = editIndex !== undefined ? w.doc.filters?.[editIndex] : undefined;
    const condition = conditionEditor(w, existing ?? (col ? { col } : undefined));

    openDialog({
        title: editIndex !== undefined ? "Edit Filter" : "Add Filter",
        width: "30rem",
        build: body => body.append(condition),
        onApply: () => {
            const rule = condition._read();
            return w.apply(d => {
                d.filters ??= [];
                if (existing) rule._off = existing._off;
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
        title: "Control Break",
        width: "24rem",
        build: body => body.append(container, list.addButton,
            el("p", { class: "ir-dialog-note" }, "Rows group under a heading per break value; aggregates subtotal per group.")),
        onApply: () => w.apply(d => {
            d.breaks = [...new Set(list.read())];
            d._offBreaks = (d._offBreaks ?? []).filter(b => d.breaks.includes(b));
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
        row._read = () => colSel.value && fnSel.value ? { col: colSel.value, fn: fnSel.value, _off: item?._off } : null;
    }, { addLabel: "Aggregate" });

    openDialog({
        title: "Aggregate",
        width: "28rem",
        build: body => body.append(container, list.addButton,
            el("p", { class: "ir-dialog-note" }, "Computed over the whole filtered set — grand total and per-break subtotals.")),
        onApply: () => w.apply(d => {
            d.aggregates = list.read().map(a => { if (!a._off) delete a._off; return a; });
        }),
    });
}

// --- Compute -----------------------------------------------------------------

const EXPR_FUNCTIONS = "UPPER LOWER TRIM LENGTH SUBSTR CONCAT ROUND ABS COALESCE YEAR MONTH DAY";

export function computeDialog(w, editIndex) {
    const existing = editIndex !== undefined ? w.doc.computed?.[editIndex] : undefined;
    const labelInp = el("input", { class: "ir-input", type: "text", value: existing?.label ?? "", placeholder: "Column heading" });
    const exprInp = el("textarea", {
        class: "ir-textarea", rows: 3, spellcheck: false,
        placeholder: "e.g. ROUND(AMOUNT * 1.0825, 2)",
    });
    exprInp.value = existing?.expr ?? "";

    const insert = token => {
        const at = exprInp.selectionStart ?? exprInp.value.length;
        exprInp.setRangeText(token, at, exprInp.selectionEnd ?? at, "end");
        exprInp.focus();
    };
    const tokenBtn = (label, token) =>
        el("button", { type: "button", class: "ir-token", onclick: () => insert(token) }, label);

    openDialog({
        title: editIndex !== undefined ? "Edit Computed Column" : "Compute Column",
        width: "36rem",
        build: body => body.append(
            labeled("Column Heading", labelInp),
            labeled("Expression", exprInp),
            el("div", { class: "ir-token-group" },
                el("span", { class: "ir-field-label" }, "Columns"),
                el("div", {}, ...w.schema.columns.map(c => tokenBtn(c.label, c.name)))),
            el("div", { class: "ir-token-group" },
                el("span", { class: "ir-field-label" }, "Functions"),
                el("div", {}, ...EXPR_FUNCTIONS.split(" ").map(f => tokenBtn(f, `${f}(`)))),
            el("p", { class: "ir-dialog-note" },
                "Operators: +  −  *  /  ||   ·   Example: SUBSTR(CUSTOMER, 1, 3) || '…'")),
        onApply: () => {
            const expr = exprInp.value.trim();
            if (!expr) throw new Error("Enter an expression");
            const ids = (w.doc.computed ?? []).map(c => c.id);
            let n = 1;
            while (ids.includes(`c${n}`)) n++;
            const rule = {
                id: existing?.id ?? `c${n}`,
                label: labelInp.value.trim() || (existing?.id ?? `c${n}`),
                expr,
            };
            return w.apply(d => {
                d.computed ??= [];
                if (existing) rule._off = existing._off;
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
    const targetSel = sel(colOptions(w), existing?.col ?? existing?.condition?.col);
    const targetField = labeled("Highlight Column", targetSel);
    const condition = conditionEditor(w, existing?.condition, {
        ops: ["eq", "ne", "lt", "le", "gt", "ge", "contains", "ncontains", "starts", "ends", "blank", "nblank"],
    });

    const bgInp = el("input", { type: "color", class: "ir-color", value: existing?.style?.bg ?? "#fff3cd" });
    const bgOn = el("input", { type: "checkbox", checked: existing ? !!existing.style?.bg : true });
    const fgInp = el("input", { type: "color", class: "ir-color", value: existing?.style?.fg ?? "#9f1239" });
    const fgOn = el("input", { type: "checkbox", checked: !!existing?.style?.fg });

    const syncScope = () => { targetField.hidden = scopeSel.value !== "cell"; };
    scopeSel.onchange = syncScope;
    syncScope();

    openDialog({
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
            const cond = condition._read();
            if (!bgOn.checked && !fgOn.checked) throw new Error("Pick a background or text color");
            const ids = (w.doc.highlights ?? []).map(h => h.id);
            let n = 1;
            while (ids.includes(`h${n}`)) n++;
            const rule = { id: existing?.id ?? `h${n}`, scope: scopeSel.value, condition: cond };
            if (scopeSel.value === "cell") rule.col = targetSel.value;
            rule.style = {};
            if (bgOn.checked) rule.style.bg = bgInp.value;
            if (fgOn.checked) rule.style.fg = fgInp.value;
            return w.apply(d => {
                d.highlights ??= [];
                if (existing) rule._off = existing._off;
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
