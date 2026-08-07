import assert from "node:assert/strict";
import test from "node:test";
import { apiUrl, download, downloadFile } from "../../src/client/core/api.js";

test("API URLs normalize the base and encode every path segment", () => {
    assert.equal(
        apiUrl("/api/reports/", "sales / west", "schema"),
        "/api/reports/sales%20%2F%20west/schema");
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
