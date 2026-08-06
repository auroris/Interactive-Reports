// Workbench: a deliberately plain client for the report protocol. Everything it does —
// build a state document, POST it, render the response — is exactly what a real UI will do.

const els = Object.fromEntries(
    ["report", "search", "add-filter", "add-agg", "add-computed", "add-highlight", "break",
     "view", "groupdim-wrap", "group-dim", "pivotrow-wrap", "pivot-row", "pivotcol-wrap", "pivot-col", "export",
     "filters", "aggs", "computeds", "highlights", "thead", "tbody", "prev", "next",
     "pageinfo", "stats", "grand", "ignored", "error",
     "saved", "save-view", "global-wrap", "save-global", "delete-saved", "identity"]
        .map(id => [id.replace(/-/g, ""), document.getElementById(id)]));

let schema = null;          // {columns:[{name,label,type}], defaultState, limits}
let state = null;           // the report state document
let totalRows = 0;
let whoami = null;          // {identity, isAdministrator, ...}
let availableColumns = null;

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
    availableColumns = null;
    state = Object.assign(
        { v: schema.stateVersion, filters: [], sorts: [], page: { index: 1, size: schema.limits.defaultPageSize } },
        structuredClone(schema.defaultState ?? {}));
    state.v = schema.stateVersion;
    state.page ??= { index: 1, size: schema.limits.defaultPageSize };
    syncControlsFromState();
    await Promise.all([runQuery(), loadSaved()]);
    syncControlsFromState();
}

// Reflect the current state document (report default or loaded saved view) in the controls.
function syncControlsFromState() {
    els.search.value = state.search ?? "";
    els.filters.replaceChildren();
    for (const f of state.filters ?? []) addFilterRow(f);
    els.break.replaceChildren(
        new Option("— none —", ""),
        ...pickableColumns().map(c => new Option(c.label, c.name)));
    els.break.value = state.breaks?.[0] ?? "";
    els.aggs.replaceChildren();
    for (const a of (state.view?.values ?? state.aggregates ?? [])) addAggRow(a.col, a.fn);
    els.computeds.replaceChildren();
    for (const c of state.computed ?? []) addComputedRow(c);
    els.highlights.replaceChildren();
    for (const h of state.highlights ?? []) addHighlightRow(h);

    const cols = pickableColumns();
    for (const sel of [els.groupdim, els.pivotrow, els.pivotcol])
        sel.replaceChildren(...cols.map(c => new Option(c.label, c.name)));
    els.view.value = state.view?.mode ?? "grid";
    if (state.view?.groupBy?.[0]) els.groupdim.value = state.view.groupBy[0];
    if (state.view?.rows?.[0]) els.pivotrow.value = state.view.rows[0];
    if (state.view?.cols?.[0]) els.pivotcol.value = state.view.cols[0];
    refreshViewControls();
}

function refreshViewControls() {
    const mode = els.view.value;
    els.groupdimwrap.hidden = mode !== "groupBy";
    els.pivotrowwrap.hidden = mode !== "pivot";
    els.pivotcolwrap.hidden = mode !== "pivot";
}

function applyView() {
    refreshViewControls();
    const mode = els.view.value;
    const values = collectAggRows();
    if (mode === "grid") {
        state.view = { mode: "grid" };
        state.aggregates = values;
    } else if (mode === "groupBy") {
        state.view = { mode: "groupBy", groupBy: [els.groupdim.value], values };
        state.aggregates = [];
    } else {
        if (els.pivotrow.value === els.pivotcol.value)
            els.pivotcol.selectedIndex = (els.pivotcol.selectedIndex + 1) % els.pivotcol.options.length;
        state.view = { mode: "pivot", rows: [els.pivotrow.value], cols: [els.pivotcol.value], values };
        state.aggregates = [];
    }
    state.page.index = 1;
    runQuery();
}

// Columns available to highlight/aggregate pickers: base schema + current computed ids.
function pickableColumns() {
    return availableColumns ?? schema.columns;
}

function aggregateFunctions(type) {
    const catalog = schema.capabilities?.aggregateFunctions ?? {};
    return catalog[type] ?? catalog.other ?? [];
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
    state = Object.assign(
        {},
        structuredClone(schema.defaultState ?? {}),
        Object.fromEntries(Object.entries(structuredClone(doc.state ?? {}))
            .filter(([, value]) => value !== null && value !== undefined)));
    availableColumns = null;
    state.v = schema.stateVersion;
    state.filters ??= [];
    state.sorts ??= [];
    state.page ??= { index: 1, size: schema.limits.defaultPageSize };
    syncControlsFromState();
    await runQuery();
    syncControlsFromState();
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
        availableColumns = result.availableColumns;
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

    const breaks = state.view && state.view.mode !== "grid" ? [] : (state.breaks ?? []);
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
        const rowHits = (hitsByRow.get(r) ?? []).filter(hit => !hit.col);
        const cellHits = (hitsByRow.get(r) ?? []).filter(hit => !!hit.col);
        for (const hit of [...rowHits, ...cellHits]) {
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

function addFilterRow(existing) {
    const row = document.createElement("div");
    row.className = "filter-row";

    const enabled = document.createElement("input");
    enabled.type = "checkbox";
    enabled.checked = existing?.enabled !== false;

    const expr = document.createElement("input");
    expr.type = "text";
    expr.placeholder = "e.g. AMOUNT > 1000 AND STATUS <> 'CANCELLED'";
    expr.size = 52;
    expr.value = existing?.expr ?? "";
    expr.onkeydown = e => { if (e.key === "Enter") applyFilters(); };

    const del = document.createElement("button");
    del.textContent = "×";
    del.onclick = () => { row.remove(); applyFilters(); };

    const apply = document.createElement("button");
    apply.textContent = "Apply";
    apply.onclick = applyFilters;

    row.append(enabled, document.createTextNode(" on "), expr, apply, del);
    els.filters.append(row);
}

function applyFilters() {
    state.filters = [...els.filters.querySelectorAll(".filter-row")].flatMap(row => {
        const [enabled, expr] = row.querySelectorAll("input");
        return expr.value.trim() ? [{ enabled: enabled.checked, expr: expr.value.trim() }] : [];
    });
    state.page.index = 1;
    runQuery();
}

// --- aggregate builder ------------------------------------------------------

function addAggRow(col, fn) {
    const row = document.createElement("div");
    row.className = "filter-row";

    const colSel = document.createElement("select");
    colSel.replaceChildren(...pickableColumns().map(c => new Option(c.label, c.name)));
    if (col) colSel.value = col;

    const fnSel = document.createElement("select");
    const refreshFns = () => {
        const type = pickableColumns().find(c => c.name === colSel.value)?.type ?? "other";
        const functions = aggregateFunctions(type);
        fnSel.replaceChildren(...functions.map(f => new Option(f, f)));
        if (fn && functions.includes(fn)) fnSel.value = fn;
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

function collectAggRows() {
    return [...els.aggs.querySelectorAll(".filter-row")].map(row => {
        const [colSel, fnSel] = row.querySelectorAll("select");
        return { col: colSel.value, fn: fnSel.value };
    });
}

function applyAggs() {
    if (els.view.value !== "grid") { applyView(); return; }   // Σ rows feed view.values in alternate views
    state.aggregates = collectAggRows();
    runQuery();
}

// --- computed & highlight builders ------------------------------------------

function addComputedRow(existing = {}) {
    const row = document.createElement("div");
    row.className = "filter-row";

    const enabled = document.createElement("input");
    enabled.type = "checkbox";
    enabled.checked = existing.enabled !== false;

    const labelInp = document.createElement("input");
    labelInp.placeholder = "Label";
    labelInp.size = 14;
    if (existing.label) labelInp.value = existing.label;

    const exprInp = document.createElement("input");
    exprInp.placeholder = "e.g. ROUND(AMOUNT * 1.0825, 2)";
    exprInp.size = 44;
    if (existing.expr) exprInp.value = existing.expr;
    exprInp.onkeydown = e => { if (e.key === "Enter") applyComputed(); };

    const apply = document.createElement("button");
    apply.textContent = "Apply";
    apply.onclick = applyComputed;

    const del = document.createElement("button");
    del.textContent = "×";
    del.onclick = () => { row.remove(); applyComputed(); };

    row.append(document.createTextNode("ƒ "), enabled, document.createTextNode(" on "), labelInp, exprInp, apply, del);
    els.computeds.append(row);
}

function applyComputed() {
    state.computed = [...els.computeds.querySelectorAll(".filter-row")].flatMap((row, i) => {
        const [enabled, labelInp, exprInp] = row.querySelectorAll("input");
        return exprInp.value.trim()
            ? [{
                id: `c${i + 1}`,
                enabled: enabled.checked,
                label: labelInp.value.trim() || `c${i + 1}`,
                expr: exprInp.value.trim(),
            }]
            : [];
    });
    state.page.index = 1;
    runQuery();
}

function addHighlightRow(existing) {
    const row = document.createElement("div");
    row.className = "filter-row";

    const enabled = document.createElement("input");
    enabled.type = "checkbox";
    enabled.checked = existing?.enabled !== false;

    const scopeSel = document.createElement("select");
    scopeSel.replaceChildren(new Option("row", "row"), new Option("cell", "cell"));
    if (existing?.scope) scopeSel.value = existing.scope;

    const colSel = document.createElement("select");
    colSel.replaceChildren(...pickableColumns().map(c => new Option(c.label, c.name)));
    if (existing?.col) colSel.value = existing.col;

    const exprInp = document.createElement("input");
    exprInp.type = "text";
    exprInp.size = 42;
    exprInp.placeholder = "e.g. ROUND(AMOUNT, 2) > 1000";
    exprInp.value = existing?.expr ?? "";
    exprInp.onkeydown = e => { if (e.key === "Enter") applyHighlights(); };

    const bgInp = document.createElement("input");
    bgInp.type = "color";
    bgInp.value = existing?.style?.bg ?? "#fff3a0";

    const apply = document.createElement("button");
    apply.textContent = "Apply";
    apply.onclick = applyHighlights;

    const del = document.createElement("button");
    del.textContent = "×";
    del.onclick = () => { row.remove(); applyHighlights(); };

    row.append(document.createTextNode("🖍 "), enabled, scopeSel, colSel, exprInp, bgInp, apply, del);
    els.highlights.append(row);
}

function applyHighlights() {
    state.highlights = [...els.highlights.querySelectorAll(".filter-row")].flatMap((row, i) => {
        const [scopeSel, colSel] = row.querySelectorAll("select");
        const [enabled, exprInp, bgInp] = row.querySelectorAll("input");
        if (!exprInp.value.trim()) return [];
        const rule = {
            id: `h${i + 1}`,
            enabled: enabled.checked,
            scope: scopeSel.value,
            expr: exprInp.value.trim(),
            style: { bg: bgInp.value },
        };
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
els.view.onchange = applyView;
els.groupdim.onchange = applyView;
els.pivotrow.onchange = applyView;
els.pivotcol.onchange = applyView;
els.export.onclick = async () => {
    const res = await fetch(`/api/reports/${els.report.value}/export`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(state),
    });
    if (!res.ok) { els.error.textContent = `export failed: HTTP ${res.status}`; return; }
    const a = document.createElement("a");
    a.href = URL.createObjectURL(await res.blob());
    a.download = `${els.report.value}.csv`;
    a.click();
    URL.revokeObjectURL(a.href);
    if (res.headers.get("X-IR-Truncated") === "true")
        els.ignored.textContent = "export truncated at the report's row cap";
};
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
        state.search = els.search.value;
        state.page.index = 1;
        runQuery();
    }, 300);
};

loadWhoami().then(loadReports).catch(e => { els.error.textContent = e.message; });
