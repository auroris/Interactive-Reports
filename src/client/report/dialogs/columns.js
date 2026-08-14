// Column presentation dialogs: which columns display and in what order
// (Select Columns shuttle), what a column's heading says (Rename), and the
// per-column settings dialog (visibility, alignment, format mask, styling).
// All three follow the stage context: the source table in grid, the group
// stage's table under a group tail, the spread output's presentation maps
// under a pivot. Display As (link/image renderers) is a source-table
// affordance — an aggregate is never a link.

import { el, labeled, sel } from "../../core/dom.js";
import { openDialog } from "../../core/dialog.js";
import { featureEnabled, typeOf } from "../schema.js";
import { stageContext } from "../stage.js";
import { sameColumn, sourceLayer } from "../state.js";
import { colOptions } from "./parts.js";
import { masksFor } from "../render/format.js";
import { renderColumnValue } from "../render/column-renderers.js";
import { columnClasses } from "../classes.js";
import { presentationStyle } from "../render/presentation.js";

const visibleStageNames = (ctx, w) => {
    const explicit = ctx.columnsLayer?.(w.doc)?.columns;
    if (explicit?.length) return explicit.filter(n => ctx.columns.some(c => sameColumn(c.name, n)));
    return ctx.columns.map(c => c.name);
};

export function columnsDialog(w) {
    const ctx = stageContext(w);
    if (!ctx.caps.columns) return;
    // Group dims always display at T0 — hiding a dim makes rows look duplicated —
    // so the shuttle offers only the rest and dims are re-pinned in front on apply.
    const pinned = ctx.mode === "grid" ? [] : (ctx.dims ?? []);
    const universe = ctx.columns.filter(c => !pinned.some(p => sameColumn(p, c.name)));
    const byName = new Map(universe.map(c => [c.name, c]));
    const visible = visibleStageNames(ctx, w);
    const displayedNames = visible.filter(n => byName.has(n));
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
                el("label", { class: "ir-shuttle-col" }, el("span", { class: "ir-shuttle-head" }, "Do Not Display"), hidden),
                el("div", { class: "ir-shuttle-btns" },
                    btn("›", "Display selected", () => move(hidden, shown)),
                    btn("‹", "Hide selected", () => move(shown, hidden)),
                    btn("»", "Display all", () => move(hidden, shown, true)),
                    btn("«", "Hide all", () => move(shown, hidden, true))),
                el("label", { class: "ir-shuttle-col" }, el("span", { class: "ir-shuttle-head" }, "Display in Report"), shown),
                el("div", { class: "ir-shuttle-btns" },
                    btn("↑", "Move up", () => nudge(-1)),
                    btn("↓", "Move down", () => nudge(1)))),
            pinned.length
                ? el("p", { class: "ir-dialog-note" }, "Group columns always display.")
                : null),
        onApply: () => {
            const names = [...shown.options].map(o => o.value);
            if (!pinned.length && !names.length) throw new Error("Display at least one column");
            return w.apply(d => { ctx.columnsLayer(d).columns = [...pinned, ...names]; });
        },
    });
}

/// Per-column settings: visibility, alignment, format mask, and constrained inline
/// styling. Nothing here is a second source of truth — the Visible checkbox writes
/// the same layer columns list the shuttle owns (re-shown columns append to the
/// end), and everything else lives in the stage's formats map, one compact entry
/// per column. Edits stage per column, so several columns can be configured in
/// one visit.
export function columnSettingsDialog(w, initialCol) {
    const ctx = stageContext(w);
    if (!ctx.caps.columnSettings) return;
    const universe = ctx.columns;
    const byName = new Map(universe.map(c => [c.name, c]));
    const columnType = name => byName.get(name)?.type ?? typeOf(w, name);
    const originallyVisible = visibleStageNames(ctx, w);
    const canHide = ctx.caps.visibility && featureEnabled(w, "columns");
    const withDisplayAs = ctx.caps.displayAs;
    const isPinned = name => ctx.mode !== "grid" && (ctx.dims ?? []).some(d => sameColumn(d, name));
    const formatsOf = d => {
        const layer = ctx.formatsLayer(d);
        return layer.formats ??= {};
    };
    const staged = new Map();

    const colSel = sel(colOptions(w, { columns: universe }), initialCol ?? originallyVisible[0] ?? universe[0]?.name);
    const visChk = el("input", { type: "checkbox" });
    const visLine = canHide ? el("label", { class: "ir-checkline" }, visChk, "Visible") : null;
    const displayAsSel = sel([
        { value: "", label: "Text (Default)" },
        { value: "link", label: "Link" },
        { value: "image", label: "Image" },
    ]);
    const urlColumnSel = sel(colOptions(w));
    const textColumnSel = sel(colOptions(w));
    const urlColumnField = labeled("URL Column", urlColumnSel);
    const textColumnField = labeled("Link Text Column", textColumnSel);
    const alignSel = sel([
        { value: "", label: "Default" },
        { value: "left", label: "Left" },
        { value: "center", label: "Center" },
        { value: "right", label: "Right" },
    ]);
    const maskSel = sel([{ value: "", label: "Default" }]);
    const maskField = labeled("Format Mask", maskSel);
    const boldChk = el("input", { type: "checkbox" });
    const italicChk = el("input", { type: "checkbox" });
    const fgOn = el("input", { type: "checkbox" });
    const fgInp = el("input", {
        type: "color", class: "ir-color", value: "#9f1239", "aria-label": "Text color",
    });
    const bgOn = el("input", { type: "checkbox" });
    const bgInp = el("input", {
        type: "color", class: "ir-color", value: "#fff3cd", "aria-label": "Background color",
    });
    const classesInp = el("input", {
        type: "text", class: "ir-input", maxLength: 500,
        placeholder: "e.g. amount-column emphasized",
    });
    const preview = el("div", { class: "ir-format-preview" });

    for (const [control, label] of [
        [colSel, "Column"],
        [displayAsSel, "Display As"],
        [urlColumnSel, "URL Column"],
        [textColumnSel, "Link Text Column"],
        [alignSel, "Alignment"],
        [maskSel, "Format Mask"],
    ]) control.setAttribute("aria-label", label);

    const read = () => ({
        visible: canHide ? visChk.checked : undefined,
        displayAs: withDisplayAs ? (displayAsSel.value || null) : null,
        urlColumn: urlColumnSel.value || colSel.value,
        textColumn: textColumnSel.value || colSel.value,
        mask: maskSel.value || null,
        align: alignSel.value || null,
        bold: boldChk.checked,
        italic: italicChk.checked,
        fg: fgOn.checked ? fgInp.value : null,
        bg: bgOn.checked ? bgInp.value : null,
        classes: classesInp.value.trim(),
    });

    const settingsFor = name => {
        if (staged.has(name)) return staged.get(name);
        const fmt = formatsOf(w.doc)[name] ?? {};
        const displayAs = typeof fmt.displayAs === "string" ? fmt.displayAs.toLowerCase() : "";
        return {
            visible: originallyVisible.includes(name),
            displayAs: withDisplayAs && ["link", "image"].includes(displayAs) ? displayAs : null,
            urlColumn: byName.has(fmt.urlColumn) ? fmt.urlColumn : name,
            textColumn: byName.has(fmt.textColumn) ? fmt.textColumn : name,
            mask: fmt.mask ?? null,
            align: fmt.align ?? null,
            bold: !!fmt.bold,
            italic: !!fmt.italic,
            fg: fmt.fg ?? null,
            bg: fmt.bg ?? null,
            classes: Array.isArray(fmt.classes) ? fmt.classes.join(" ") : "",
        };
    };

    const sampleFor = (name, type) => {
        const fromData = w.lastResult?.rows?.map(r => r[name]).find(v => v !== null && v !== undefined);
        if (fromData !== undefined) return fromData;
        return type === "number" ? 1234567.891
            : type === "date" ? "2026-08-07T14:30:00"
            : type === "bool" ? true
            : "Sample text";
    };

    const updatePreview = () => {
        const name = colSel.value;
        const type = columnType(name);
        const s = read();
        const sampleRow = { ...(w.lastResult?.rows?.[0] ?? {}) };
        if (sampleRow[s.urlColumn] === null || sampleRow[s.urlColumn] === undefined)
            sampleRow[s.urlColumn] = "https://example.com/example";
        if (sampleRow[s.textColumn] === null || sampleRow[s.textColumn] === undefined)
            sampleRow[s.textColumn] = sampleFor(s.textColumn, columnType(s.textColumn));
        if (sampleRow[name] === null || sampleRow[name] === undefined)
            sampleRow[name] = sampleFor(name, type);
        preview.replaceChildren(renderColumnValue(w, sampleRow, { name, type }, true, s));
        Object.assign(preview.style, presentationStyle(s, {
            defaultAlign: type === "number" ? "right" : "",
        }));
        preview.className = "ir-format-preview";
        preview.classList.add(...columnClasses(s.classes));
        urlColumnField.hidden = !s.displayAs;
        textColumnField.hidden = s.displayAs !== "link";
        maskField.hidden = masksFor(type).length === 0
            || s.displayAs === "image"
            || (s.displayAs === "link" && s.textColumn !== name);
    };

    let active = colSel.value;
    const load = name => {
        const s = settingsFor(name);
        visChk.checked = s.visible !== false;
        visChk.disabled = isPinned(name);
        displayAsSel.value = s.displayAs ?? "";
        urlColumnSel.value = s.urlColumn;
        textColumnSel.value = s.textColumn;
        alignSel.value = s.align ?? "";
        const masks = masksFor(columnType(name));
        maskSel.replaceChildren(new Option("Default", ""), ...masks.map(m => new Option(m.label, m.value)));
        maskSel.value = masks.some(m => m.value === s.mask) ? s.mask : "";
        boldChk.checked = s.bold;
        italicChk.checked = s.italic;
        fgOn.checked = !!s.fg;
        if (s.fg) fgInp.value = s.fg;
        bgOn.checked = !!s.bg;
        if (s.bg) bgInp.value = s.bg;
        classesInp.value = s.classes;
        updatePreview();
    };
    colSel.onchange = () => {
        staged.set(active, read());
        active = colSel.value;
        load(active);
    };
    for (const control of [visChk, displayAsSel, urlColumnSel, textColumnSel, alignSel, maskSel,
        boldChk, italicChk, fgOn, fgInp, bgOn, bgInp, classesInp])
        control.addEventListener("input", updatePreview);
    load(active);

    openDialog({
        owner: w,
        title: "Column Settings",
        width: "26rem",
        build: body => body.append(
            labeled("Column", colSel),
            visLine,
            withDisplayAs ? labeled("Display As", displayAsSel) : null,
            withDisplayAs ? urlColumnField : null,
            withDisplayAs ? textColumnField : null,
            labeled("Alignment", alignSel),
            maskField,
            el("div", { class: "ir-checklines" },
                el("label", { class: "ir-checkline" }, boldChk, "Bold"),
                el("label", { class: "ir-checkline" }, italicChk, "Italic")),
            el("div", { class: "ir-colors" },
                el("div", { class: "ir-color-pick" },
                    el("label", { class: "ir-checkline" }, fgOn, "Text"), fgInp),
                el("div", { class: "ir-color-pick" },
                    el("label", { class: "ir-checkline" }, bgOn, "Background"), bgInp)),
            labeled("CSS Classes", classesInp),
            el("p", { class: "ir-dialog-note" },
                "Space-separated classes from the report's configured stylesheet. The ir- prefix is reserved."),
            labeled("Preview", preview)),
        onApply: () => {
            staged.set(active, read());

            let columns = [...originallyVisible];
            let visibilityChanged = false;
            if (canHide) {
                for (const [name, s] of staged) {
                    if (isPinned(name)) continue;
                    const visible = columns.includes(name);
                    if (s.visible && !visible) { columns.push(name); visibilityChanged = true; }
                    else if (!s.visible && visible) { columns = columns.filter(n => n !== name); visibilityChanged = true; }
                }
                if (!columns.length) throw new Error("Display at least one column");
            }

            return w.apply(d => {
                const formats = formatsOf(d);
                for (const [name, s] of staged) {
                    const entry = {};
                    if (s.mask) entry.mask = s.mask;
                    if (s.align) entry.align = s.align;
                    if (s.bold) entry.bold = true;
                    if (s.italic) entry.italic = true;
                    if (s.fg) entry.fg = s.fg;
                    if (s.bg) entry.bg = s.bg;
                    if (s.displayAs) {
                        entry.displayAs = s.displayAs;
                        entry.urlColumn = s.urlColumn || name;
                        if (s.displayAs === "link") entry.textColumn = s.textColumn || name;
                    }
                    const classes = columnClasses(s.classes, { strict: true });
                    if (classes.length) entry.classes = classes;
                    if (Object.keys(entry).length) formats[name] = entry;
                    else delete formats[name];
                }
                if (visibilityChanged) ctx.columnsLayer(d).columns = columns;
            });
        },
    });
}

/// Column headings only. In grid, a base column writes the source layer's labels
/// (kept as an explicit map so clearing an inherited default sticks) and a
/// computed column edits its rule's label. Under a group or pivot, the rename is
/// view-scoped: it writes the terminal stage's labels map — the grid heading is
/// untouched — except for that stage's own computed columns, which edit their
/// rule. The expression name never changes; that is always the real name or id.
export function renameDialog(w, col) {
    const ctx = stageContext(w);
    const stageRule = ctx.computeLayer
        ? (ctx.computeLayer(w.doc).computed ?? []).find(c => sameColumn(c.id, col))
        : null;
    const gridSourceRule = ctx.mode === "grid" ? stageRule : null;
    const column = ctx.columns.find(c => sameColumn(c.name, col));
    const schemaDefault = stageRule && (ctx.mode === "grid" || !column?.dim)
        ? stageRule.id
        : w.schema?.columns?.find(c => sameColumn(c.name, col))?.label ?? column?.label ?? col;

    const currentOverride = ctx.mode === "grid"
        ? (gridSourceRule ? gridSourceRule.label ?? "" : sourceLayer(w.doc).labels?.[col] ?? "")
        : stageRule
            ? stageRule.label ?? ""
            : ctx.labelsLayer(w.doc).labels?.[col] ?? "";

    const input = el("input", {
        class: "ir-input", type: "text",
        value: currentOverride,
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
                const rule = ctx.computeLayer
                    ? (ctx.computeLayer(d).computed ?? []).find(c => sameColumn(c.id, col))
                    : null;
                if (rule && (ctx.mode === "grid" || !column?.dim)) {
                    rule.label = label || rule.id;
                    return;
                }
                const layer = ctx.mode === "grid" ? sourceLayer(d) : ctx.labelsLayer(d);
                if (!label || label === schemaDefault) {
                    if (layer.labels) delete layer.labels[col];
                } else {
                    (layer.labels ??= {})[col] = label;
                }
            });
        },
    });
}
