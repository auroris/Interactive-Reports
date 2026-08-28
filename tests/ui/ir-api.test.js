import assert from "node:assert/strict";
import test from "node:test";
import { ApiError, apiUrl, download, downloadFile, errorLines } from "../../src/client/core/api.js";

test("API URLs normalize the base and encode every path segment", () => {
    assert.equal(
        apiUrl("/api/reports/", "sales / west", "schema"),
        "/api/reports/sales%20%2F%20west/schema");
});

test("errorLines carries the server's title, detail, and every validation message in order", () => {
    const err = new ApiError({
        title: "Report state failed validation",
        detail: "The posted document is inconsistent.",
        errors: { "pipeline[0]": ["Unknown column X", "Bad expression"] },
        traceId: "00-abc-01",
    }, 400);
    // The correlation reference is appended by each presenter in its own format.
    assert.deepEqual(errorLines(err), [
        "Report state failed validation",
        "The posted document is inconsistent.",
        "Unknown column X",
        "Bad expression",
    ]);
    assert.deepEqual(errorLines(new ApiError({}, 502)), ["HTTP 502"]);
    assert.deepEqual(errorLines(new Error("plain failure")), ["plain failure"]);
    assert.deepEqual(errorLines("just text"), ["just text"]);
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

        const exported = await download("/api/reports/orders/export", { v: 2 });
        assert.equal(calls[1].options.method, "POST");
        assert.equal(calls[1].options.body, '{"v":2}');
        assert.equal(exported.truncated, true);
    } finally {
        globalThis.fetch = originalFetch;
    }
});
