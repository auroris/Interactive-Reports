import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";
import { safeRendererUrl } from "../../src/client/report/render/column-renderers.js";

const window = new Window({ url: "https://host.example/reports/orders" });
globalThis.document = window.document;

test("column renderer URLs accept navigable URLs and reject active or embedded content", () => {
    assert.equal(safeRendererUrl("/orders/42"), "/orders/42");
    assert.equal(safeRendererUrl("mailto:reports@example.com"), "mailto:reports@example.com");
    assert.equal(safeRendererUrl("javascript:alert(1)"), null);
    assert.equal(safeRendererUrl("data:text/html,unsafe"), null);

    assert.equal(safeRendererUrl("https://images.example/42.png", "image"), "https://images.example/42.png");
    assert.equal(safeRendererUrl("mailto:images@example.com", "image"), null);
    assert.equal(safeRendererUrl("data:image/png;base64,AAAA", "image"), null);
});
