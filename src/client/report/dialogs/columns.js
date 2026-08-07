// Column presentation dialogs: which columns display and in what order
// (Select Columns shuttle), what a column's heading says (Rename), and the
// per-column settings dialog (visibility, alignment, format mask, styling).

import { el, labeled, sel } from "../../core/dom.js";
import { openDialog } from "../../core/dialog.js";
import { featureEnabled, pickable, typeOf, visibleColumnNames } from "../schema.js";
import { colOptions } from "./parts.js";
import { formatValue, masksFor } from "../render/format.js";
import { columnClasses } from "../classes.js";

export function columnsDialog(w) {
    const universe = pickable(w);
    const byName = new Map(universe.map(c => [c.name, c]));
    const displayedNames = visibleColumnNames(w).filter(n => byName.has(n));
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

/// Per-column settings: visibility, alignment, format mask, and constrained inline
/// styling. Nothing here is a second source of truth — the Visible checkbox writes
/// the same doc.columns list the shuttle owns (re-shown columns append to the end),
/// and everything else lives in doc.formats, one compact entry per column. Edits
/// stage per column, so several columns can be configured in one visit.
export function columnSettingsDialog(w, initialCol) {
    const universe = pickable(w);
    const byName = new Map(universe.map(c => [c.name, c]));
    const originallyVisible = visibleColumnNames(w).filter(n => byName.has(n));
    const canHide = featureEnabled(w, "columns");
    const staged = new Map();

    const colSel = sel(colOptions(w), initialCol ?? originallyVisible[0] ?? universe[0]?.name);
    const visChk = el("input", { type: "checkbox" });
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
    const fgInp = el("input", { type: "color", class: "ir-color", value: "#9f1239" });
    const bgOn = el("input", { type: "checkbox" });
    const bgInp = el("input", { type: "color", class: "ir-color", value: "#fff3cd" });
    const classesInp = el("input", {
        type: "text", class: "ir-input", maxLength: 500,
        placeholder: "e.g. amount-column emphasized",
    });
    const preview = el("div", { class: "ir-format-preview" });

    const read = () => ({
        visible: canHide ? visChk.checked : undefined,
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
        const fmt = w.doc.formats?.[name] ?? {};
        return {
            visible: originallyVisible.includes(name),
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
        const type = typeOf(w, name);
        const s = read();
        preview.textContent = formatValue(sampleFor(name, type), type, true, s.mask);
        preview.style.textAlign = s.align ?? (type === "number" ? "right" : "");
        preview.style.fontWeight = s.bold ? "600" : "";
        preview.style.fontStyle = s.italic ? "italic" : "";
        preview.style.color = s.fg ?? "";
        preview.style.background = s.bg ?? "";
        preview.className = "ir-format-preview";
        preview.classList.add(...columnClasses(s.classes));
    };

    let active = colSel.value;
    const load = name => {
        const s = settingsFor(name);
        visChk.checked = s.visible !== false;
        alignSel.value = s.align ?? "";
        const masks = masksFor(typeOf(w, name));
        maskSel.replaceChildren(new Option("Default", ""), ...masks.map(m => new Option(m.label, m.value)));
        maskSel.value = masks.some(m => m.value === s.mask) ? s.mask : "";
        maskField.hidden = masks.length === 0;
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
    for (const control of [visChk, alignSel, maskSel, boldChk, italicChk, fgOn, fgInp, bgOn, bgInp, classesInp])
        control.addEventListener("input", updatePreview);
    load(active);

    openDialog({
        owner: w,
        title: "Column Settings",
        width: "26rem",
        build: body => body.append(
            labeled("Column", colSel),
            canHide ? el("label", { class: "ir-checkline" }, visChk, "Visible") : null,
            labeled("Alignment", alignSel),
            maskField,
            el("div", { class: "ir-checklines" },
                el("label", { class: "ir-checkline" }, boldChk, "Bold"),
                el("label", { class: "ir-checkline" }, italicChk, "Italic")),
            el("div", { class: "ir-colors" },
                el("label", { class: "ir-color-pick" }, fgOn, "Text", fgInp),
                el("label", { class: "ir-color-pick" }, bgOn, "Background", bgInp)),
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
                    const visible = columns.includes(name);
                    if (s.visible && !visible) { columns.push(name); visibilityChanged = true; }
                    else if (!s.visible && visible) { columns = columns.filter(n => n !== name); visibilityChanged = true; }
                }
                if (!columns.length) throw new Error("Display at least one column");
            }

            return w.apply(d => {
                for (const [name, s] of staged) {
                    const entry = {};
                    if (s.mask) entry.mask = s.mask;
                    if (s.align) entry.align = s.align;
                    if (s.bold) entry.bold = true;
                    if (s.italic) entry.italic = true;
                    if (s.fg) entry.fg = s.fg;
                    if (s.bg) entry.bg = s.bg;
                    const classes = columnClasses(s.classes, { strict: true });
                    if (classes.length) entry.classes = classes;
                    if (Object.keys(entry).length) (d.formats ??= {})[name] = entry;
                    else if (d.formats) delete d.formats[name];
                }
                if (visibilityChanged) d.columns = columns;
            });
        },
    });
}

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
