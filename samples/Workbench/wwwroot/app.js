// Workbench: a deliberately plain client for the report protocol. Everything it does —
// build a state document, POST it, render the response — is exactly what a real UI will do.

const OPS_BY_TYPE = {
    text: ["contains", "ncontains", "starts", "ends", "eq", "ne", "in", "nin", "blank", "nblank"],
    number: ["eq", "ne", "lt", "le", "gt", "ge", "between", "in", "nin", "blank", "nblank"],
    date: ["eq", "ne", "lt", "le", "gt", "ge", "between", "blank", "nblank"],
    bool: ["eq", "ne", "blank", "nblank"],
    other: ["eq", "ne", "blank", "nblank"],
};
const NO_VALUE_OPS = ["blank", "nblank"];
const LIST_OPS = ["in", "nin"];
const AGG_FNS_BY_TYPE = {
    number: ["sum", "avg", "min", "max", "count", "countDistinct"],
    text: ["min", "max", "count", "countDistinct"],
    date: ["min", "max", "count", "countDistinct"],
    bool: ["count", "countDistinct"],
    other: ["count", "countDistinct"],
};

const els = Object.fromEntries(
    ["report", "search", "add-filter", "add-agg", "add-computed", "add-highlight", "break",
     "filters", "aggs", "computeds", "highlights", "thead", "tbody", "prev", "next",
     "pageinfo", "stats", "grand", "ignored", "error",
     "saved", "save-view", "global-wrap", "save-global", "delete-saved", "identity"]
        .map(id => [id.replace(/-/g, ""), document.getElementById(id)]));

let schema = null;          // {columns:[{name,label,type}], defaultState, limits}
let state = null;           // the report state document
let totalRows = 0;
let whoami = null;          // {identity, isAdministrator, ...}

async function api(path, options) {
    const res = await fetch(path, options);
    if (!res.ok) {
        const problem = await res.json().catch(() => ({}));
        throw new Error(problem.title
            ? `${problem.title}${problem.detail ? ": " + problem.detail : ""}${problem.errors ? "\n" + JSON.stringify(problem.errors, null, 2) : ""}`
            : `HTTP ${res.status}`);
    }
    return res.json();
}

async function loadReports() {
    const reports = await api("/api/reports");
    els.report.replaceChildren(...reports.map(r => new Option(r.title, r.name)));
    if (reports.length) await selectReport(reports[0].name);
}

async function selectReport(name) {
    schema = await api(`/api/reports/${name}/schema`);
    state = Object.assign(
        { filters: [], sorts: [], page: { index: 1, size: 25 } },
        structuredClone(schema.defaultState ?? {}));
    state.page ??= { index: 1, size: 25 };
    syncControlsFromState();
    await Promise.all([runQuery(), loadSaved()]);
}

// Reflect the current state document (report default or loaded saved view) in the controls.
function syncControlsFromState() {
    els.search.value = state.search ?? "";
    els.filters.replaceChildren();   // active filters stay in state; builder rows reset
    els.break.replaceChildren(
        new Option("— none —", ""),
        ...schema.columns.map(c => new Option(c.label, c.name)));
    els.break.value = state.breaks?.[0] ?? "";
    els.aggs.replaceChildren();
    for (const a of state.aggregates ?? []) addAggRow(a.col, a.fn);
    els.computeds.replaceChildren();
    for (const c of state.computed ?? []) addComputedRow(c.label, c.expr);
    els.highlights.replaceChildren();
    for (const h of state.highlights ?? []) addHighlightRow(h);
}

// Columns available to highlight/aggregate pickers: base schema + current computed ids.
function pickableColumns() {
    const computed = (state.computed ?? []).map(c => ({ name: c.id, label: c.label ?? c.id, type: "number" }));
    return [...schema.columns, ...computed];
}

// --- identity & saved views -------------------------------------------------

async function loadWhoami() {
    try {
        whoami = await api("/api/reports/whoami");
        els.identity.textContent = (whoami.identity ?? "anonymous") + (whoami.isAdministrator ? " · admin" : "");
        els.globalwrap.hidden = !whoami.isAdministrator;
    } catch {
        els.identity.textContent = "whoami disabled";
    }
}

async function loadSaved(selectId) {
    const saved = await api(`/api/reports/${els.report.value}/saved`);
    const options = [new Option("— unsaved view —", "")];
    for (const s of saved) {
        const label = `${s.isGlobal ? "🌐 " : ""}${s.title}${s.mine ? "" : ` (${s.owner})`}`;
        const opt = new Option(label, s.id);
        opt.dataset.mine = s.mine;
        opt.dataset.global = s.isGlobal;
        options.push(opt);
    }
    els.saved.replaceChildren(...options);
    els.saved.value = selectId ?? "";
    refreshSavedButtons();
}

function refreshSavedButtons() {
    const opt = els.saved.selectedOptions[0];
    const deletable = opt && opt.value !== ""
        && (whoami?.isAdministrator || (opt.dataset.mine === "true" && opt.dataset.global !== "true"));
    els.deletesaved.disabled = !deletable;
}

async function loadSavedState(id) {
    const doc = await api(`/api/reports/saved/${id}`);
    state = doc.state ?? {};
    state.filters ??= [];
    state.sorts ??= [];
    state.page ??= { index: 1, size: 25 };
    syncControlsFromState();
    await runQuery();
}

async function saveCurrentView() {
    const title = prompt("Save view as:");
    if (!title) return;
    const isGlobal = whoami?.isAdministrator && els.saveglobal.checked;
    const created = await api(`/api/reports/${els.report.value}/saved`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ title, state, isGlobal }),
    }).catch(e => { els.error.textContent = e.message; return null; });
    if (created) await loadSaved(created.id);
}

async function deleteSavedView() {
    const id = els.saved.value;
    if (!id) return;
    const res = await fetch(`/api/reports/saved/${id}`, { method: "DELETE" });
    if (!res.ok && res.status !== 204) {
        els.error.textContent = `delete failed: HTTP ${res.status}`;
        return;
    }
    await loadSaved();
}

async function runQuery() {
    els.error.textContent = "";
    try {
        const result = await api(`/api/reports/${els.report.value}/query`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(state),
        });
        totalRows = result.totalRows;
        render(result);
    } catch (e) {
        els.error.textContent = e.message;
    }
}

function render(result) {
    const sortsByCol = new Map((state.sorts ?? []).map(s => [s.col, s.dir]));

    const headRow = document.createElement("tr");
    for (const col of result.columns) {
        const th = document.createElement("th");
        th.textContent = col.label;
        const dir = sortsByCol.get(col.name);
        if (dir) th.insertAdjacentHTML("beforeend", ` <span class="dir">${dir === "desc" ? "▼" : "▲"}</span>`);
        th.onclick = () => toggleSort(col.name);
        headRow.append(th);
    }
    els.thead.replaceChildren(headRow);

    const breaks = state.breaks ?? [];
    const breakKeyOf = row => breaks.map(b => String(row[b] ?? "")).join("");
    const totalsByKey = new Map((result.breakTotals ?? []).map(bt =>
        [breaks.map(b => String(bt.key[b] ?? "")).join(""), bt]));

    const bodyRows = [];
    let currentKey = null;
    const emitTotals = key => {
        const bt = totalsByKey.get(key);
        if (!bt) return;
        const tr = document.createElement("tr");
        tr.className = "break-total";
        const td = document.createElement("td");
        td.colSpan = result.columns.length;
        td.textContent = `Σ ${bt.rows} rows` + formatAggs(bt.aggregates, " · ");
        tr.append(td);
        bodyRows.push(tr);
    };

    const styleById = new Map((state.highlights ?? []).map(h => [h.id, h.style ?? {}]));
    const hitsByRow = new Map();
    for (const h of result.highlights ?? []) {
        if (!hitsByRow.has(h.row)) hitsByRow.set(h.row, []);
        hitsByRow.get(h.row).push(h);
    }

    for (const [r, row] of result.rows.entries()) {
        if (breaks.length) {
            const key = breakKeyOf(row);
            if (key !== currentKey) {
                if (currentKey !== null) emitTotals(currentKey);
                const tr = document.createElement("tr");
                tr.className = "break-header";
                const td = document.createElement("td");
                td.colSpan = result.columns.length;
                td.textContent = breaks.map(b => `${b}: ${row[b] ?? "(blank)"}`).join("  ·  ");
                tr.append(td);
                bodyRows.push(tr);
                currentKey = key;
            }
        }
        const tr = document.createElement("tr");
        for (const col of result.columns) {
            const td = document.createElement("td");
            const v = row[col.name];
            td.textContent = v == null ? "" : String(v);
            if (col.type === "number") td.className = "num";
            tr.append(td);
        }
        for (const hit of hitsByRow.get(r) ?? []) {
            const style = styleById.get(hit.id) ?? {};
            if (!hit.col) {
                if (style.bg) tr.style.background = style.bg;
                if (style.fg) tr.style.color = style.fg;
            } else {
                const idx = result.columns.findIndex(c => c.name === hit.col);
                if (idx >= 0) {
                    if (style.bg) tr.children[idx].style.background = style.bg;
                    if (style.fg) tr.children[idx].style.color = style.fg;
                }
            }
        }
        bodyRows.push(tr);
    }
    if (currentKey !== null) emitTotals(currentKey);
    els.tbody.replaceChildren(...bodyRows);

    const pages = Math.max(1, Math.ceil(totalRows / state.page.size));
    els.pageinfo.textContent = `page ${state.page.index} / ${pages}`;
    els.prev.disabled = state.page.index <= 1;
    els.next.disabled = state.page.index >= pages;
    els.stats.textContent = `${totalRows} rows · ${result.elapsedMs} ms`;
    els.grand.textContent = formatAggs(result.aggregates, "  ");
    els.ignored.textContent = result.ignored?.length
        ? "ignored: " + result.ignored.map(i => `${i.kind} (${i.detail})`).join(", ")
        : "";
}

function formatAggs(aggregates, lead) {
    const parts = [];
    for (const [col, fns] of Object.entries(aggregates ?? {})) {
        for (const [fn, v] of Object.entries(fns)) {
            const num = typeof v === "number" ? v.toLocaleString(undefined, { maximumFractionDigits: 2 }) : v;
            parts.push(`${fn}(${col}) = ${num ?? "—"}`);
        }
    }
    return parts.length ? lead + parts.join(" · ") : "";
}

function toggleSort(col) {
    const current = (state.sorts ?? []).find(s => s.col === col);
    state.sorts = current?.dir === "asc" ? [{ col, dir: "desc" }]
        : current?.dir === "desc" ? []
        : [{ col, dir: "asc" }];
    state.page.index = 1;
    runQuery();
}

// --- filter builder ---------------------------------------------------------

function addFilterRow() {
    const row = document.createElement("div");
    row.className = "filter-row";

    const colSel = document.createElement("select");
    colSel.replaceChildren(...schema.columns.map(c => new Option(c.label, c.name)));

    const opSel = document.createElement("select");
    const values = document.createElement("span");

    const refreshOps = () => {
        const type = schema.columns.find(c => c.name === colSel.value)?.type ?? "other";
        opSel.replaceChildren(...OPS_BY_TYPE[type].map(o => new Option(o, o)));
        refreshInputs();
    };
    const refreshInputs = () => {
        const type = schema.columns.find(c => c.name === colSel.value)?.type ?? "other";
        const inputType = type === "number" ? "number" : type === "date" ? "date" : "text";
        if (NO_VALUE_OPS.includes(opSel.value)) { values.replaceChildren(); }
        else if (opSel.value === "between") {
            values.replaceChildren(makeInput(inputType), document.createTextNode(" and "), makeInput(inputType));
        } else if (LIST_OPS.includes(opSel.value)) {
            const inp = makeInput("text");
            inp.placeholder = "comma,separated,values";
            inp.size = 28;
            values.replaceChildren(inp);
        } else {
            values.replaceChildren(makeInput(inputType));
        }
    };

    colSel.onchange = refreshOps;
    opSel.onchange = refreshInputs;

    const del = document.createElement("button");
    del.textContent = "×";
    del.onclick = () => { row.remove(); applyFilters(); };

    const apply = document.createElement("button");
    apply.textContent = "Apply";
    apply.onclick = applyFilters;

    row.append(colSel, opSel, values, apply, del);
    els.filters.append(row);
    refreshOps();
}

function makeInput(type) {
    const inp = document.createElement("input");
    inp.type = type;
    inp.onkeydown = e => { if (e.key === "Enter") applyFilters(); };
    return inp;
}

function applyFilters() {
    state.filters = [...els.filters.querySelectorAll(".filter-row")].flatMap(row => {
        const [colSel, opSel] = row.querySelectorAll("select");
        const inputs = [...row.querySelectorAll("input")];
        const type = schema.columns.find(c => c.name === colSel.value)?.type;
        const op = opSel.value;

        const coerce = raw => {
            if (raw === "") return null;
            return type === "number" ? Number(raw) : raw;
        };

        if (NO_VALUE_OPS.includes(op)) return [{ col: colSel.value, op }];
        if (op === "between") {
            const [lo, hi] = inputs.map(i => coerce(i.value));
            return lo != null && hi != null ? [{ col: colSel.value, op, value: [lo, hi] }] : [];
        }
        if (LIST_OPS.includes(op)) {
            const list = inputs[0].value.split(",").map(s => coerce(s.trim())).filter(v => v != null);
            return list.length ? [{ col: colSel.value, op, value: list }] : [];
        }
        const v = coerce(inputs[0].value);
        return v != null ? [{ col: colSel.value, op, value: v }] : [];
    });
    state.page.index = 1;
    runQuery();
}

// --- aggregate builder ------------------------------------------------------

function addAggRow(col, fn) {
    const row = document.createElement("div");
    row.className = "filter-row";

    const colSel = document.createElement("select");
    colSel.replaceChildren(...schema.columns.map(c => new Option(c.label, c.name)));
    if (col) colSel.value = col;

    const fnSel = document.createElement("select");
    const refreshFns = () => {
        const type = schema.columns.find(c => c.name === colSel.value)?.type ?? "other";
        fnSel.replaceChildren(...AGG_FNS_BY_TYPE[type].map(f => new Option(f, f)));
        if (fn && AGG_FNS_BY_TYPE[type].includes(fn)) fnSel.value = fn;
    };
    colSel.onchange = () => { refreshFns(); applyAggs(); };
    fnSel.onchange = applyAggs;

    const del = document.createElement("button");
    del.textContent = "×";
    del.onclick = () => { row.remove(); applyAggs(); };

    row.append(document.createTextNode("Σ "), colSel, fnSel, del);
    els.aggs.append(row);
    refreshFns();
}

function applyAggs() {
    state.aggregates = [...els.aggs.querySelectorAll(".filter-row")].map(row => {
        const [colSel, fnSel] = row.querySelectorAll("select");
        return { col: colSel.value, fn: fnSel.value };
    });
    runQuery();
}

// --- computed & highlight builders ------------------------------------------

function addComputedRow(label, expr) {
    const row = document.createElement("div");
    row.className = "filter-row";

    const labelInp = document.createElement("input");
    labelInp.placeholder = "Label";
    labelInp.size = 14;
    if (label) labelInp.value = label;

    const exprInp = document.createElement("input");
    exprInp.placeholder = "e.g. ROUND(AMOUNT * 1.0825, 2)";
    exprInp.size = 44;
    if (expr) exprInp.value = expr;
    exprInp.onkeydown = e => { if (e.key === "Enter") applyComputed(); };

    const apply = document.createElement("button");
    apply.textContent = "Apply";
    apply.onclick = applyComputed;

    const del = document.createElement("button");
    del.textContent = "×";
    del.onclick = () => { row.remove(); applyComputed(); };

    row.append(document.createTextNode("ƒ "), labelInp, exprInp, apply, del);
    els.computeds.append(row);
}

function applyComputed() {
    state.computed = [...els.computeds.querySelectorAll(".filter-row")].flatMap((row, i) => {
        const [labelInp, exprInp] = row.querySelectorAll("input");
        return exprInp.value.trim()
            ? [{ id: `c${i + 1}`, label: labelInp.value.trim() || `c${i + 1}`, expr: exprInp.value.trim() }]
            : [];
    });
    state.page.index = 1;
    runQuery();
}

const HL_OPS_BY_TYPE = Object.fromEntries(Object.entries(OPS_BY_TYPE)
    .map(([t, ops]) => [t, ops.filter(o => !LIST_OPS.includes(o) && o !== "between")]));

function addHighlightRow(existing) {
    const row = document.createElement("div");
    row.className = "filter-row";

    const scopeSel = document.createElement("select");
    scopeSel.replaceChildren(new Option("row", "row"), new Option("cell", "cell"));
    if (existing?.scope) scopeSel.value = existing.scope;

    const colSel = document.createElement("select");
    colSel.replaceChildren(...pickableColumns().map(c => new Option(c.label, c.name)));
    if (existing?.condition?.col) colSel.value = existing.condition.col;

    const opSel = document.createElement("select");
    const valInp = document.createElement("input");
    valInp.size = 12;
    valInp.onkeydown = e => { if (e.key === "Enter") applyHighlights(); };

    const refreshOps = () => {
        const type = pickableColumns().find(c => c.name === colSel.value)?.type ?? "other";
        opSel.replaceChildren(...HL_OPS_BY_TYPE[type].map(o => new Option(o, o)));
        if (existing?.condition?.op) opSel.value = existing.condition.op;
        valInp.hidden = NO_VALUE_OPS.includes(opSel.value);
    };
    colSel.onchange = refreshOps;
    opSel.onchange = () => { valInp.hidden = NO_VALUE_OPS.includes(opSel.value); };
    if (existing?.condition?.value !== undefined) valInp.value = existing.condition.value;

    const bgInp = document.createElement("input");
    bgInp.type = "color";
    bgInp.value = existing?.style?.bg ?? "#fff3a0";

    const apply = document.createElement("button");
    apply.textContent = "Apply";
    apply.onclick = applyHighlights;

    const del = document.createElement("button");
    del.textContent = "×";
    del.onclick = () => { row.remove(); applyHighlights(); };

    row.append(document.createTextNode("🖍 "), scopeSel, colSel, opSel, valInp, bgInp, apply, del);
    els.highlights.append(row);
    refreshOps();
}

function applyHighlights() {
    state.highlights = [...els.highlights.querySelectorAll(".filter-row")].flatMap((row, i) => {
        const [scopeSel, colSel, opSel] = row.querySelectorAll("select");
        const [valInp, bgInp] = row.querySelectorAll("input");
        const type = pickableColumns().find(c => c.name === colSel.value)?.type;
        const op = opSel.value;
        const condition = { col: colSel.value, op };
        if (!NO_VALUE_OPS.includes(op)) {
            if (valInp.value === "") return [];
            condition.value = type === "number" ? Number(valInp.value) : valInp.value;
        }
        const rule = { id: `h${i + 1}`, scope: scopeSel.value, condition, style: { bg: bgInp.value } };
        if (scopeSel.value === "cell") rule.col = colSel.value;
        return [rule];
    });
    runQuery();
}

// --- wiring -----------------------------------------------------------------

els.report.onchange = () => selectReport(els.report.value);
els.addfilter.onclick = addFilterRow;
els.addagg.onclick = () => addAggRow();
els.addcomputed.onclick = () => addComputedRow();
els.addhighlight.onclick = () => addHighlightRow();
els.break.onchange = () => {
    state.breaks = els.break.value ? [els.break.value] : [];
    state.page.index = 1;
    runQuery();
};
els.prev.onclick = () => { state.page.index--; runQuery(); };
els.next.onclick = () => { state.page.index++; runQuery(); };
els.saved.onchange = () => { refreshSavedButtons(); if (els.saved.value) loadSavedState(els.saved.value); };
els.saveview.onclick = saveCurrentView;
els.deletesaved.onclick = deleteSavedView;

let searchTimer;
els.search.oninput = () => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => {
        state.search = els.search.value || undefined;
        state.page.index = 1;
        runQuery();
    }, 300);
};

loadWhoami().then(loadReports).catch(e => { els.error.textContent = e.message; });
