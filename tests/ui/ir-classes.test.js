import assert from "node:assert/strict";
import test from "node:test";
import { columnClasses } from "../../src/client/report/classes.js";

test("column class parsing keeps safe unique tokens and filters reserved state", () => {
    assert.deepEqual(
        columnClasses(["amount-column", "amount-column", "emphasized", "ir-empty", "bad.token"]),
        ["amount-column", "emphasized"]);
    assert.throws(
        () => columnClasses("amount-column ir-row", { strict: true }),
        /invalid or reserved/i);
});
