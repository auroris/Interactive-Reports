import assert from "node:assert/strict";
import test from "node:test";
import { IntlMessageFormat } from "intl-messageformat";
import { ApiError, apiUrl, download, downloadFile, errorLines, errorText } from "../../src/client/core/api.js";
import { loadWhoami } from "../../src/client/core/identity.js";
import {
    localeCatalogs,
    localizedError,
    supportedErrorCodes,
    supportedLocales,
    supportedMessageKeys,
    translate,
} from "../../src/client/core/localization.js";

test("API URLs normalize the base and encode every path segment", () => {
    assert.equal(
        apiUrl("/api/reports/", "sales / west", "schema"),
        "/api/reports/sales%20%2F%20west/schema");
});

test("coded errors localize their core message and retain untranslated details", () => {
    const err = new ApiError({
        code: "IR-1201",
        title: "Server title is only a fallback",
        description: "Server description is only a fallback.",
        details: "tables.base.composables[0]: Unknown column X\ntables.base.composables[0]: Bad expression",
        traceId: "00-abc-01",
    }, 400);
    // The correlation reference is appended by each presenter in its own format.
    assert.equal(err.code, "IR-1201");
    assert.deepEqual(errorLines(err, "en"), [
        "Report state failed validation",
        "One or more report settings are invalid.",
        "tables.base.composables[0]: Unknown column X",
        "tables.base.composables[0]: Bad expression",
    ]);
    assert.deepEqual(errorLines(err, "fr-CA"), [
        "Échec de la validation de l’état du rapport",
        "Un ou plusieurs paramètres du rapport ne sont pas valides.",
        "tables.base.composables[0]: Unknown column X",
        "tables.base.composables[0]: Bad expression",
    ]);
    assert.deepEqual(errorLines(new ApiError({}, 502)), ["HTTP 502"]);
    assert.deepEqual(errorLines(new Error("plain failure")), ["plain failure"]);
    assert.deepEqual(errorLines("just text"), ["just text"]);
    assert.equal(errorText(err, null, "en"),
        "Report state failed validation — One or more report settings are invalid. — tables.base.composables[0]: Unknown column X — tables.base.composables[0]: Bad expression (ref 00-abc-01)");
    assert.equal(errorText(err, null, "fr-CA"),
        "Échec de la validation de l’état du rapport — Un ou plusieurs paramètres du rapport ne sont pas valides. — tables.base.composables[0]: Unknown column X — tables.base.composables[0]: Bad expression (réf. 00-abc-01)");

    const future = new ApiError({
        code: "IR-9999",
        title: "Future server title",
        description: "Future server description.",
    }, 400);
    assert.deepEqual(
        errorLines(future, "fr-CA"),
        ["Future server title", "Future server description."],
        "unknown codes fall back to the server while catalogs are out of step");
});

test("every client error code has English and Canadian French copy", () => {
    assert.deepEqual(supportedLocales, ["en", "fr-CA"]);
    assert.equal(new Set(supportedErrorCodes).size, supportedErrorCodes.length);
    for (const code of supportedErrorCodes) {
        assert.match(code, /^IR-\d{4}$/);
        for (const locale of supportedLocales) {
            const message = localizedError(code, locale);
            assert.ok(message?.title, `${code} has a ${locale} title`);
            assert.ok(message?.description, `${code} has a ${locale} description`);
        }
    }
});

test("the full UI catalogs have matching, valid ICU messages", () => {
    assert.equal(new Set(supportedMessageKeys).size, supportedMessageKeys.length);
    for (const locale of supportedLocales) {
        const keys = Object.keys(localeCatalogs[locale].messages);
        assert.deepEqual(keys, supportedMessageKeys, `${locale} follows the canonical key order`);
        for (const key of keys) {
            const message = localeCatalogs[locale].messages[key];
            assert.ok(message, `${key} has ${locale} copy`);
            assert.doesNotThrow(
                () => new IntlMessageFormat(message, locale),
                `${key} has valid ${locale} ICU syntax`);
        }
    }

    assert.equal(
        translate("fr-CA", "chart.description", {
            type: "bar",
            summary: "revenus par région",
            count: 2,
        }),
        "Graphique à barres de revenus par région. 2 points de données.");
});

test("whoami shares the optional-endpoint policy and coalesces concurrent requests", async () => {
    const originalFetch = globalThis.fetch;
    let status = 200;
    let body = { identity: "test-user" };
    let calls = 0;
    globalThis.fetch = async () => {
        calls++;
        await new Promise(resolve => setTimeout(resolve, 0));
        return new Response(body === null ? null : JSON.stringify(body), {
            status,
            headers: { "Content-Type": "application/json" },
        });
    };

    try {
        const [first, second] = await Promise.all([
            loadWhoami("/api/reports"),
            loadWhoami("/api/reports"),
        ]);
        assert.equal(calls, 1, "the admin shell and embedded report share one in-flight request");
        assert.deepEqual(first, second);
        assert.equal(first.whoami.identity, "test-user");
        assert.equal(first.error, null);

        status = 404;
        body = null;
        const absent = await loadWhoami("/api/reports");
        assert.equal(absent.whoami, null);
        assert.equal(absent.error, null);

        status = 500;
        body = {
            code: "IR-1202",
            description: "Identity lookup failed.",
            title: "Identity failed",
            traceId: "trace-1",
        };
        const failed = await loadWhoami("/api/reports");
        assert.equal(failed.whoami, null);
        assert.equal(failed.error.status, 500);
        assert.equal(failed.error.traceId, "trace-1");
    } finally {
        globalThis.fetch = originalFetch;
    }
});

test("file downloads preserve GET for admin JSON and POST for report exports", async () => {
    const originalFetch = globalThis.fetch;
    const calls = [];
    globalThis.fetch = async (url, options) => {
        calls.push({ url, options });
        return new Response("document", {
            headers: {
                "Content-Disposition": 'attachment; filename="orders.test.json"',
                "X-IR-Truncated": "true",
            },
        });
    };

    try {
        const file = await downloadFile("/api/reports/admin/saved/1/document");
        assert.equal(calls[0].options.method, "GET");
        assert.equal(calls[0].options.body, undefined);
        assert.equal(file.filename, "orders.test.json");
        assert.equal(await file.blob.text(), "document");

        const exported = await download("/api/reports/42/export", { search: "open" });
        assert.equal(calls[1].options.method, "POST");
        assert.equal(calls[1].options.body, '{"search":"open"}');
        assert.equal(exported.truncated, true);
    } finally {
        globalThis.fetch = originalFetch;
    }
});
