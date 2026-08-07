import assert from "node:assert/strict";
import test from "node:test";
import { apiUrl } from "../../src/InteractiveReport.AspNetCore/Ui/src/core/api.js";

test("API URLs normalize the base and encode every path segment", () => {
    assert.equal(
        apiUrl("/api/reports/", "sales / west", "schema"),
        "/api/reports/sales%20%2F%20west/schema");
});
