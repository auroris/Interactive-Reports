// Column presentation dialogs: which columns display and in what order (Select Columns
// shuttle), what a column's heading says (Rename), and the per-column settings dialog
// (visibility, alignment, format mask, styling). All three follow the active table's terminal
// context. A shape composable does not select a different presentation implementation.

import { el, labeled, sel } from "../../core/dom.js";
import { openDialog } from "../../core/dialog.js";
import { featureEnabled, typeOf } from "../schema.js";
import { structuralTableColumns, tableContext, visibleTableColumnNames } from "../table.js";
import { lookupValue, sameColumn, setMapEntry } from "../state.js";
import { colorPick, colOptions } from "./parts.js";
import { MAX_MASK_LENGTH, maskIsValid, masksFor } from "../render/format.js";
import { formatForColumn, renderColumnValue } from "../render/column-renderers.js";
import { columnClasses } from "../classes.js";
import { presentationStyle } from "../render/presentation.js";

/**
 * Opens the active table's two-list visibility and display-order editor.
 *
 * @param {object} w - The report controller whose active table supplies terminal columns and selection state.
 * @returns {void} No value.
 *
 * Side effects: opens a dialog; its shuttle controls move option nodes, and applying stores the ordered visible names and runs the report.
 */
export function columnsDialog(w) {
    const ctx = tableContext(w);
    if (!ctx.caps.columns) return;
    const universe = ctx.columns;
    const byName = new Map(universe.map(c => [c.name, c]));
    const visible = visibleTableColumnNames(ctx, w);
    const displayedNames = visible.filter(n => byName.has(n));
    const hiddenNames = universe.map(c => c.name).filter(n => !displayedNames.includes(n));

    // Builds one side of the shuttle from canonical column names.
    const listbox = names => el("select", { multiple: true, size: 12, class: "ir-shuttle-list" },
        ...names.map(n => new Option(byName.get(n).computed ? `ƒ ${byName.get(n).label}` : byName.get(n).label, n)));
    const hidden = listbox(hiddenNames);
    const shown = listbox(displayedNames);

    // Moves selected or all option nodes between shuttle lists without recreating them.
    const move = (from, to, all) => {
        const picked = all ? [...from.options] : [...from.selectedOptions];
        for (const o of picked) to.append(o);
    };
    // Moves selected visible options one slot while preserving their relative order.
    const nudge = delta => {
        const opts = [...shown.selectedOptions];
        if (delta > 0) opts.reverse();
        for (const o of opts) {
            const sibling = delta < 0 ? o.previousElementSibling : o.nextElementSibling;
            if (!sibling || sibling.selected) continue;
            delta < 0 ? sibling.before(o) : sibling.after(o);
        }
    };
    // Creates one shuttle command button with visible shorthand and an explanatory title.
    const btn = (label, title, onclick) =>
        el("button", { type: "button", class: "ir-btn", title, onclick }, label);

    openDialog({
        owner: w,
        title: w.t("columns.selectTitle"),
        width: "34rem",
        build: body => body.append(
            el("div", { class: "ir-shuttle" },
                el("label", { class: "ir-shuttle-col" }, el("span", { class: "ir-shuttle-head" }, w.t("columns.doNotDisplay")), hidden),
                el("div", { class: "ir-shuttle-btns" },
                    btn("›", w.t("columns.displaySelected"), () => move(hidden, shown)),
                    btn("‹", w.t("columns.hideSelected"), () => move(shown, hidden)),
                    btn("»", w.t("columns.displayAll"), () => move(hidden, shown, true)),
                    btn("«", w.t("columns.hideAll"), () => move(shown, hidden, true))),
                el("label", { class: "ir-shuttle-col" }, el("span", { class: "ir-shuttle-head" }, w.t("columns.displayInReport")), shown),
                el("div", { class: "ir-shuttle-btns" },
                    btn("↑", w.t("columns.moveUp"), () => nudge(-1)),
                    btn("↓", w.t("columns.moveDown"), () => nudge(1))))),
        onApply: () => {
            const names = [...shown.options].map(o => o.value);
            if (!names.length) throw new Error(w.t("columns.displayAtLeastOne"));
            return w.apply(d => ctx.edit(d, "select", node => { node.columns = names; }));
        },
    });
}

/**
 * Per-column settings: visibility, alignment, format mask, and constrained inline styling. Nothing
 * here is a second source of truth. The Visible checkbox writes the same Select node the shuttle owns
 * (re-shown columns append to the end), and everything else lives in the table's terminal Formats
 * node, one compact entry per column. Edits buffer per column, so several columns can be configured in
 * one visit.
 *
 * @param {object} w - The report controller whose active columns, formats, selection, data samples, and apply pipeline are used.
 * @param {string|undefined} initialCol - The column to preselect, or the first visible/available column by default.
 * @returns {void} No value.
 *
 * Side effects: opens a dialog, registers staged-preview handlers, and on apply may replace format and selection entries and run the report.
 */
export function columnSettingsDialog(w, initialCol) {
    const ctx = tableContext(w);
    if (!ctx.caps.columnSettings) return;
    const universe = ctx.columns;
    const byName = new Map(universe.map(c => [c.name, c]));
    // Resolves terminal column types before falling back to the definition-input schema.
    const columnType = name => byName.get(name)?.type ?? typeOf(w, name);
    const originallyVisible = visibleTableColumnNames(ctx, w);
    const canHide = ctx.caps.visibility && featureEnabled(w, "columns");
    const withDisplayAs = ctx.caps.displayAs;
    // Reads the active table's owner-local format map from a particular document snapshot.
    const formatsOf = d => ctx.node(d, "formats")?.formats ?? {};
    const staged = new Map();

    const colSel = sel(colOptions(w, { columns: universe }), initialCol ?? originallyVisible[0] ?? universe[0]?.name);
    const visChk = el("input", { type: "checkbox" });
    const visLine = canHide ? el("label", { class: "ir-checkline" }, visChk, w.t("columns.visible")) : null;
    const displayAsSel = sel([
        { value: "", label: w.t("columns.textDefault") },
        { value: "link", label: w.t("columns.link") },
        { value: "image", label: w.t("columns.image") },
    ]);
    displayAsSel.classList.add("ir-display-as");
    const urlColumnSel = sel(colOptions(w, { columns: universe }));
    const textColumnSel = sel(colOptions(w, { columns: universe }));
    const urlColumnField = labeled(w.t("columns.urlColumn"), urlColumnSel);
    const textColumnField = labeled(w.t("columns.linkTextColumn"), textColumnSel);
    urlColumnField.classList.add("ir-url-only");
    textColumnField.classList.add("ir-text-only");
    const alignSel = sel([
        { value: "", label: w.t("common.default") },
        { value: "left", label: w.t("columns.left") },
        { value: "center", label: w.t("columns.center") },
        { value: "right", label: w.t("columns.right") },
    ]);
    // The mask is free text in Excel format-code syntax. The preset list is a typing aid: choosing
    // a preset copies its code into the text box, and the list shows "Custom" whenever the text
    // matches no preset, so hand-written masks survive a visit untouched.
    const maskInp = el("input", {
        type: "text", class: "ir-input ir-mask-input", maxLength: MAX_MASK_LENGTH,
        spellcheck: false, autocomplete: "off",
        placeholder: w.t("columns.maskPlaceholder"),
    });
    const maskPresetSel = sel([{ value: "", label: w.t("columns.maskCustom") }]);
    maskPresetSel.classList.add("ir-mask-preset");
    const maskHint = el("p", { class: "ir-dialog-note ir-mask-hint" });
    const maskField = el("div", { class: "ir-mask-field" },
        labeled(w.t("columns.formatMask"), maskInp),
        labeled(w.t("columns.maskPreset"), maskPresetSel),
        maskHint);
    // Reflects the text box in the preset list without touching the text.
    const syncPreset = () => {
        const value = maskInp.value.trim();
        maskPresetSel.value = value && [...maskPresetSel.options].some(o => o.value === value) ? value : "";
    };
    const boldChk = el("input", { type: "checkbox" });
    const italicChk = el("input", { type: "checkbox" });
    const fgPick = colorPick(w.t("columns.textColor"), null, "#9f1239", w);
    const bgPick = colorPick(w.t("common.background"), null, "#fff3cd", w);
    const classesInp = el("input", {
        type: "text", class: "ir-input", maxLength: 500,
        placeholder: w.t("columns.cssPlaceholder"),
    });
    const preview = el("div", { class: "ir-format-preview" });

    for (const [control, label] of [
        [colSel, w.t("common.column")],
        [displayAsSel, w.t("columns.displayAs")],
        [urlColumnSel, w.t("columns.urlColumn")],
        [textColumnSel, w.t("columns.linkTextColumn")],
        [alignSel, w.t("columns.alignment")],
        [maskInp, w.t("columns.formatMask")],
        [maskPresetSel, w.t("columns.maskPreset")],
    ]) control.setAttribute("aria-label", label);

    // Captures every control into one staged settings value for the currently active column.
    const read = () => ({
        visible: canHide ? visChk.checked : undefined,
        action: settingsFor(active).action,
        displayAs: withDisplayAs ? (displayAsSel.value || null) : null,
        urlColumn: urlColumnSel.value || colSel.value,
        textColumn: textColumnSel.value || colSel.value,
        mask: maskInp.value.trim() || null,
        align: alignSel.value || null,
        bold: boldChk.checked,
        italic: italicChk.checked,
        fg: fgPick.read(),
        bg: bgPick.read(),
        classes: classesInp.value.trim(),
    });

    // Saved documents may key formats or source columns under different casing than the live
    // schema; resolve everything to canonical names so the staged edits and the final write
    // land on one entry.
    const canonicalName = value => universe.find(c => sameColumn(c.name, value))?.name;
    // Returns a staged value or seeds one from owner-local format, safe inherited mask, and visibility.
    const settingsFor = name => {
        if (staged.has(name)) return staged.get(name);
        // Inheritance rule: seed a new local entry from the effective composed format. Otherwise
        // toggling one style on a synthetic metric would replace and silently discard its
        // inherited scalar mask. Renderer/style state is owner-local.
        const fmt = lookupValue(formatsOf(w.doc), name)
            ?? formatForColumn(w, byName.get(name) ?? { name })
            ?? {};
        const displayAs = typeof fmt.displayAs === "string" ? fmt.displayAs.toLowerCase() : "";
        return {
            visible: originallyVisible.includes(name),
            // Invariant: definition-authored action renderers are not editable here (the select
            // offers Text/Link/Image), but they must survive an unrelated restyle.
            action: displayAs === "action" ? { command: fmt.command, keyColumn: fmt.keyColumn } : null,
            displayAs: withDisplayAs && ["link", "image"].includes(displayAs) ? displayAs : null,
            urlColumn: canonicalName(fmt.urlColumn) ?? name,
            textColumn: canonicalName(fmt.textColumn) ?? name,
            mask: fmt.mask ?? null,
            align: fmt.align ?? null,
            bold: !!fmt.bold,
            italic: !!fmt.italic,
            fg: fmt.fg ?? null,
            bg: fmt.bg ?? null,
            classes: Array.isArray(fmt.classes) ? fmt.classes.join(" ") : "",
        };
    };

    // Chooses a non-null result value or a type-appropriate fallback for live preview.
    const sampleFor = (name, type) => {
        const fromData = w.lastResult?.rows?.map(r => r[name]).find(v => v !== null && v !== undefined);
        if (fromData !== undefined) return fromData;
        return type === "number" ? 1234567.891
            : type === "date" ? "2026-08-07T14:30:00"
            : type === "bool" ? true
            : w.t("columns.sampleText");
    };

    // Re-renders the sample cell and shows mask controls only when they can affect visible text.
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
        maskField.hidden = masksFor(type, w).length === 0
            || s.displayAs === "image"
            || (s.displayAs === "link" && s.textColumn !== name);
        const maskOk = maskIsValid(type, s.mask, w);
        maskHint.textContent = w.t(maskOk ? "columns.maskNote" : "columns.maskInvalid");
        maskHint.classList.toggle("ir-mask-invalid", !maskOk);
    };

    let active = colSel.value;
    // Loads one column's staged or effective settings into controls and refreshes the preview.
    const load = name => {
        const s = settingsFor(name);
        visChk.checked = s.visible !== false;
        displayAsSel.value = s.displayAs ?? "";
        urlColumnSel.value = s.urlColumn;
        textColumnSel.value = s.textColumn;
        alignSel.value = s.align ?? "";
        const presets = masksFor(columnType(name), w);
        maskPresetSel.replaceChildren(
            new Option(w.t("columns.maskCustom"), ""),
            ...presets.map(p => new Option(`${p.value} · ${p.example}`, p.value)));
        maskInp.value = s.mask ?? "";
        syncPreset();
        boldChk.checked = s.bold;
        italicChk.checked = s.italic;
        fgPick.write(s.fg);
        bgPick.write(s.bg);
        classesInp.value = s.classes;
        updatePreview();
    };
    colSel.onchange = () => {
        staged.set(active, read());
        active = colSel.value;
        load(active);
    };
    for (const control of [visChk, displayAsSel, urlColumnSel, textColumnSel, alignSel, maskInp,
        boldChk, italicChk, fgPick.node, bgPick.node, classesInp])
        control.addEventListener("input", updatePreview);
    maskInp.addEventListener("input", syncPreset);
    maskPresetSel.addEventListener("change", () => {
        if (!maskPresetSel.value) return;
        maskInp.value = maskPresetSel.value;
        updatePreview();
    });
    load(active);

    openDialog({
        owner: w,
        title: w.t("columns.settingsTitle"),
        width: "26rem",
        build: body => body.append(
            labeled(w.t("common.column"), colSel),
            visLine,
            withDisplayAs ? labeled(w.t("columns.displayAs"), displayAsSel) : null,
            withDisplayAs ? urlColumnField : null,
            withDisplayAs ? textColumnField : null,
            labeled(w.t("columns.alignment"), alignSel),
            maskField,
            el("div", { class: "ir-checklines" },
                el("label", { class: "ir-checkline" }, boldChk, w.t("common.bold")),
                el("label", { class: "ir-checkline" }, italicChk, w.t("common.italic"))),
            el("div", { class: "ir-colors" },
                fgPick.node,
                bgPick.node),
            labeled(w.t("columns.cssClasses"), classesInp),
            el("p", { class: "ir-dialog-note" },
                w.t("columns.cssNote")),
            labeled(w.t("columns.preview"), preview)),
        onApply: () => {
            staged.set(active, read());

            let columns = [...originallyVisible];
            let visibilityChanged = false;
            if (canHide) {
                for (const [name, s] of staged) {
                    const visible = columns.includes(name);
                    if (s.visible && !visible) { columns.push(name); visibilityChanged = true; }
                    else if (!s.visible && visible) { columns = columns.filter(n => n !== name); visibilityChanged = true; }
                }
                if (!columns.length) throw new Error(w.t("columns.displayAtLeastOne"));
            }

            return w.apply(d => {
                ctx.edit(d, "formats", node => {
                    const formats = node.formats ??= {};
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
                        } else if (s.action) {
                            entry.displayAs = "action";
                            if (s.action.command) entry.command = s.action.command;
                            if (s.action.keyColumn) entry.keyColumn = s.action.keyColumn;
                        }
                        const classes = columnClasses(s.classes, { strict: true, context: w });
                        if (classes.length) entry.classes = classes;
                        setMapEntry(formats, name, Object.keys(entry).length ? entry : undefined);
                    }
                });
                if (visibilityChanged)
                    ctx.edit(d, "select", node => { node.columns = columns; });
            });
        },
    });
}

// Invariant: column headings only. A computed column owned by the active editor node keeps its
// heading on that rule; every other heading writes the same terminal labels composable,
// regardless of the shape that produced the table.
/**
 * Opens the editor that changes a column's report-specific display label.
 *
 * @param {object} w - The report controller whose active table and label channels will be edited.
 * @param {string} col - The terminal column identifier to rename.
 * @returns {void} No value.
 *
 * Side effects: opens a dialog; applying updates a computed rule label or terminal label-map entry and runs the report.
 */
export function renameDialog(w, col) {
    const ctx = tableContext(w);
    const computedRule = (ctx.node(w.doc, "compute")?.computed ?? [])
        .find(c => sameColumn(c.id, col));
    const column = ctx.columns.find(c => sameColumn(c.name, col));
    const structuralColumn = structuralTableColumns(w)
        .find(c => sameColumn(c.name, col));
    const schemaDefault = computedRule
        ? computedRule.id
        : structuralColumn?.label
            ?? w.schema?.columns?.find(c => sameColumn(c.name, col))?.label
            ?? column?.label
            ?? col;

    const currentOverride = computedRule
        ? computedRule.label ?? ""
        : lookupValue(ctx.node(w.doc, "labels")?.labels, col) ?? "";

    const input = el("input", {
        class: "ir-input", type: "text",
        value: currentOverride,
        placeholder: schemaDefault,
    });

    openDialog({
        owner: w,
        title: w.t("columns.renameTitle"),
        width: "24rem",
        build: body => body.append(
            labeled(w.t("columns.heading"), input),
            el("p", { class: "ir-dialog-note" },
                w.t("columns.renameNote", { column: col, defaultLabel: schemaDefault }))),
        onApply: () => {
            const label = input.value.trim();
            return w.apply(d => {
                const rule = (ctx.node(d, "compute")?.computed ?? [])
                    .find(c => sameColumn(c.id, col));
                if (rule) {
                    rule.label = label || rule.id;
                    return;
                }
                ctx.edit(d, "labels", node => {
                    if (!label || label === schemaDefault) {
                        if (node.labels) setMapEntry(node.labels, col, undefined);
                    } else {
                        setMapEntry(node.labels ??= {}, col, label);
                    }
                });
            });
        },
    });
}
