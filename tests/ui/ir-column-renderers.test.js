import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";
import { renderColumnValue, safeRendererUrl } from "../../src/client/report/render/column-renderers.js";

const window = new Window({ url: "https://host.example/reports/orders" });
globalThis.document = window.document;
globalThis.CustomEvent = window.CustomEvent;

test("column renderer URLs accept navigable URLs and reject active or embedded content", () => {
    assert.equal(safeRendererUrl("/orders/42"), "/orders/42");
    assert.equal(safeRendererUrl("mailto:reports@example.com"), "mailto:reports@example.com");
    assert.equal(safeRendererUrl("javascript:alert(1)"), null);
    assert.equal(safeRendererUrl("data:text/html,unsafe"), null);

    assert.equal(safeRendererUrl("https://images.example/42.png", "image"), "https://images.example/42.png");
    assert.equal(safeRendererUrl("mailto:images@example.com", "image"), null);
    assert.equal(safeRendererUrl("data:image/png;base64,AAAA", "image"), null);
});

test("action cells render buttons for labeled rows only and dispatch ir-action with the row copy", () => {
    const host = document.createElement("div");
    const events = [];
    host.addEventListener("ir-action", event => events.push(event.detail));
    const format = { displayAs: "action", command: "delete", keyColumn: "ID" };
    const col = { name: "ACTION_DELETE", type: "text" };
    const row = { ACTION_DELETE: "Delete", ID: "abc123", TITLE: "Mine" };

    const button = renderColumnValue(host, row, col, false, format, true);
    assert.equal(button.tagName, "BUTTON");
    assert.equal(button.textContent, "Delete");
    button.click();
    assert.equal(events.length, 1);
    assert.equal(events[0].command, "delete");
    assert.equal(events[0].column, "ACTION_DELETE");
    assert.equal(events[0].row.ID, "abc123", "the hidden key rides the event");
    assert.notEqual(events[0].row, row, "listeners get a copy, not render state");

    const blank = renderColumnValue(host, { ACTION_DELETE: null, ID: "x" }, col, false, format, true);
    assert.equal(blank, "", "a NULL label is no button — the read-only row convention");

    const nonGrid = renderColumnValue(host, row, col, false, format, false);
    assert.equal(nonGrid, "Delete", "display renderers stay disabled outside the grid");
});
