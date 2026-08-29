import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";
import { resolveLocale, translate } from "../../src/client/core/localization.js";

test("locale resolution crosses a component shadow root and normalizes French variants", () => {
    const window = new Window({ url: "https://host.example/reports" });
    const host = window.document.createElement("interactive-report");
    host.setAttribute("lang", "fr");
    window.document.body.append(host);
    const child = host.attachShadow({ mode: "open" }).appendChild(window.document.createElement("button"));

    assert.equal(resolveLocale(child), "fr-CA");
    assert.equal(translate(child, "toolbar.search"), "Rechercher");
    assert.equal(resolveLocale("fr-FR"), "fr-CA");
    assert.equal(resolveLocale("de-DE"), "en", "unsupported explicit locales use the product fallback");
});

test("the nearest lang attribute wins over the page language", () => {
    const window = new Window({ url: "https://host.example/reports" });
    window.document.documentElement.lang = "en";
    const host = window.document.createElement("interactive-report");
    host.lang = "fr-CA";
    window.document.body.append(host);

    assert.equal(resolveLocale(host), "fr-CA");
    assert.equal(translate(host, "common.cancel"), "Annuler");
});
