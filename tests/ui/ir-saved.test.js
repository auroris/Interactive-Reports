import assert from "node:assert/strict";
import test from "node:test";
import { sameTitle } from "../../src/client/report/saved.js";

test("saved-title equality is case-insensitive and Unicode-normalization aware", () => {
    assert.equal(sameTitle("Quarterly Café", "quarterly cafe\u0301"), true);
    assert.equal(sameTitle("Quarterly Cafe", "Quarterly Café"), false);
    assert.equal(sameTitle(null, "Quarterly Café"), false);
});
