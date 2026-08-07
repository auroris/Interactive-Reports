// The pagination bar: prev/next, the row range, and elapsed time. Page size lives
// in Actions → Pagination so the report document has one authoritative control.
// Page moves go through w.apply like every other state change; a page move is
// the one mutation that must not reset the page index back to 1.

import { el } from "../../core/dom.js";
import { formatInteger, parseReportNumber } from "./format.js";

const gotoPage = (w, index) => w.applyOrBanner(d => { d.page.index = index; }, { resetPage: false });

export function renderPager(w, container) {
    const result = w.lastResult;
    if (!result) { container.replaceChildren(); return; }

    const { index, size } = result.page;
    const all = size === 0;
    const total = result.totalRows;
    const totalCount = parseReportNumber(total) ?? parseReportNumber(0);
    const zero = totalCount.eq(0);
    const mode = w.doc.view?.mode ?? "grid";
    const unit = mode === "groupBy" ? "groups" : mode === "chart" ? "points" : "rows";
    const start = zero ? totalCount : all ? parseReportNumber(1) : parseReportNumber(index - 1).times(size).plus(1);
    const end = zero ? totalCount : start.plus(result.rows.length).minus(1);
    const hasNext = !all && parseReportNumber(index).times(size).lt(totalCount);

    container.replaceChildren(
        el("div", { class: "ir-pager-left" },
            el("button", {
                type: "button", class: "ir-btn ir-page-btn", disabled: index <= 1,
                "aria-label": "Previous page", onclick: () => gotoPage(w, index - 1),
            }, "‹"),
            el("span", { class: "ir-page-info" },
                zero ? `0 ${unit}`
                    : `${formatInteger(start.toString())} – ${formatInteger(end.toString())} of ${formatInteger(totalCount.toString())} ${unit}`),
            el("button", {
                type: "button", class: "ir-btn ir-page-btn", disabled: !hasNext,
                "aria-label": "Next page", onclick: () => gotoPage(w, index + 1),
            }, "›")),
        el("div", { class: "ir-pager-right" }, `${result.elapsedMs} ms`));
}
