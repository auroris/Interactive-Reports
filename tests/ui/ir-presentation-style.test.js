import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";
import { presentationStyle } from "../../src/client/report/render/presentation.js";

test("background color fields cannot create images and omitted styles clear earlier colors", () => {
    const window = new Window();
    const cell = window.document.createElement("td");
    Object.assign(cell.style, presentationStyle({ bg: 'url("https://images.example/pixel")', fg: "white" }));
    assert.equal(cell.style.backgroundImage, "");
    assert.equal(cell.style.backgroundColor, "");
    assert.equal(cell.style.color, "white");

    Object.assign(cell.style, presentationStyle({ bg: "red" }));
    assert.equal(cell.style.backgroundColor, "red");
    Object.assign(cell.style, presentationStyle());
    assert.equal(cell.style.backgroundColor, "");
    assert.equal(cell.style.color, "");
});
