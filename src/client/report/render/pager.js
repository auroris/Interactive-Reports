// Pagination renderer for previous/next controls, the visible row range, and elapsed time. Page
// size lives in Actions → Pagination so the report document has one authoritative control. Page moves go
// through w.apply like every other state change; a page move is the one mutation that must not
// reset the page index back to 1.

import { el } from "../../core/dom.js";
import { translate } from "../../core/localization.js";
import { featureEnabled } from "../schema.js";
import { modeOf } from "../state.js";
import { formatInteger, parseReportNumber } from "./format.js";

/**
 * Updates the requested page and runs the resulting report query.
 *
 * @param {object} w - The report controller whose state and active query will be updated.
 * @param {number} index - The one-based page index to request.
 * @returns {Promise<void>} The controller's apply operation, which settles after the page query completes.
 *
 * Side effects: updates `doc.page.index` without resetting it and runs the report.
 */
const gotoPage = (w, index) => w.applyOrBanner(d => { d.page.index = index; }, { resetPage: false });

/**
 * Replaces the pager container with localized navigation, range, total, and elapsed-time controls.
 *
 * @param {object} w - The report controller providing the last result, state, and localized actions.
 * @param {Element} container - The pager region to replace.
 * @returns {void} No value.
 *
 * Side effects: replaces the container's children and wires page buttons to execute new queries.
 */
export function renderPager(w, container) {
    const result = w.lastResult;
    if (!result) { container.replaceChildren(); return; }

    const { index, size } = result.page;
    const all = size === 0;
    const total = result.totalRows;
    const totalCount = parseReportNumber(total) ?? parseReportNumber(0);
    const zero = totalCount.eq(0);
    const mode = modeOf(w.doc);
    const unit = mode === "groupBy" ? "Groups" : mode === "chart" ? "Points" : "Rows";
    const start = zero ? totalCount : all ? parseReportNumber(1) : parseReportNumber(index - 1).times(size).plus(1);
    const end = zero ? totalCount : start.plus(result.rows.length).minus(1);
    const hasNext = !all && parseReportNumber(index).times(size).lt(totalCount);
    const paginationEnabled = featureEnabled(w, "pagination");

    container.replaceChildren(
        el("div", { class: "ir-pager-left" },
            el("button", {
                type: "button", class: "ir-btn ir-page-btn", disabled: !paginationEnabled || index <= 1,
                "aria-label": translate(w, "pagination.previous"), onclick: () => gotoPage(w, index - 1),
            }, "‹"),
            el("span", { class: "ir-page-info" },
                zero
                    ? translate(w, `pagination.zero${unit}`, { count: 0 })
                    : translate(w, `pagination.range${unit}`, {
                        start: formatInteger(start.toString(), w),
                        end: formatInteger(end.toString(), w),
                        total: formatInteger(totalCount.toString(), w),
                    })),
            el("button", {
                type: "button", class: "ir-btn ir-page-btn", disabled: !paginationEnabled || !hasNext,
                "aria-label": translate(w, "pagination.next"), onclick: () => gotoPage(w, index + 1),
            }, "›")),
        el("div", { class: "ir-pager-right" }, translate(w, "pagination.elapsed", { milliseconds: result.elapsedMs })));
}
