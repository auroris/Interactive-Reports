// The pagination bar: prev/next, the row range, page-size select, elapsed time.
// Page moves go through w.apply like every other state change; a page move is
// the one mutation that must not reset the page index back to 1.

import { el } from "../../core/dom.js";

const gotoPage = (w, index) => w.applyOrBanner(d => { d.page.index = index; }, { resetPage: false });
const setPageSize = (w, size) => w.applyOrBanner(d => { d.page.size = size; });

export function renderPager(w, container) {
    const result = w.lastResult;
    if (!result) { container.replaceChildren(); return; }

    const { index, size } = result.page;
    const total = result.totalRows;
    const mode = w.doc.view?.mode ?? "grid";
    const unit = mode === "groupBy" ? "groups" : mode === "chart" ? "points" : "rows";
    const start = total === 0 ? 0 : (index - 1) * size + 1;
    const end = total === 0 ? 0 : start + result.rows.length - 1;
    const pages = Math.max(1, Math.ceil(total / size));

    const sizes = [...new Set([15, 25, 50, 100, size])]
        .filter(s => s <= (w.schema?.limits?.maxPageSize ?? Infinity))
        .sort((a, b) => a - b);
    const sizeSel = el("select", { class: "ir-select ir-pagesize", title: "Rows per page" },
        ...sizes.map(s => new Option(String(s), String(s))));
    sizeSel.value = String(size);
    sizeSel.onchange = () => setPageSize(w, Number(sizeSel.value));

    container.replaceChildren(
        el("div", { class: "ir-pager-left" },
            el("button", {
                type: "button", class: "ir-btn ir-page-btn", disabled: index <= 1,
                "aria-label": "Previous page", onclick: () => gotoPage(w, index - 1),
            }, "‹"),
            el("span", { class: "ir-page-info" },
                total === 0 ? `0 ${unit}`
                    : `${start.toLocaleString()} – ${end.toLocaleString()} of ${Number(total).toLocaleString()} ${unit}`),
            el("button", {
                type: "button", class: "ir-btn ir-page-btn", disabled: index >= pages,
                "aria-label": "Next page", onclick: () => gotoPage(w, index + 1),
            }, "›"),
            mode === "chart" ? null : el("span", { class: "ir-pagesize-wrap" }, "Rows ", sizeSel)),
        el("div", { class: "ir-pager-right" }, `${result.elapsedMs} ms`));
}
