// Column presentation dialogs: which columns display and in what order
// (Select Columns shuttle), and what a column's heading says (Rename).

import { el, labeled } from "../../core/dom.js";
import { openDialog } from "../../core/dialog.js";
import { pickable, visibleColumnNames } from "../schema.js";

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
