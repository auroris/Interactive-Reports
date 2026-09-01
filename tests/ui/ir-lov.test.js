import assert from "node:assert/strict";
import test from "node:test";
import { valueFilterExpression } from "../../src/client/report/lov.js";

test("LOV selections become portable complete filter expressions", () => {
    assert.equal(
        valueFilterExpression({ name: "STATUS", type: "text" }, "Director's Cut"),
        "LOWER(STATUS) = LOWER('Director''s Cut')");
    assert.equal(
        valueFilterExpression({ name: "TOTAL VALUE", type: "number" }, "9007199254740993.25"),
        "`TOTAL VALUE` = 9007199254740993.25");
    assert.equal(
        valueFilterExpression({ name: "ACTIVE", type: "bool" }, false),
        "NOT ACTIVE");
    assert.equal(
        valueFilterExpression({ name: "ORDER_DATE", type: "date" }, "2026-08-31T14:30:00"),
        "DATE_TRUNC('DAY', ORDER_DATE) = TO_DATE('2026-08-31')");
    assert.equal(
        valueFilterExpression({ name: "NOTES", type: "text" }, null),
        "NOTES IS NULL");
    assert.equal(
        valueFilterExpression({ name: "STATUS", type: "text" }, "not in the list", { typed: true }),
        "LOWER(STATUS) = LOWER('not in the list')");
    assert.equal(
        valueFilterExpression({ name: "CUSTOMER", type: "text" }, "Ac*Corp", { typed: true }),
        "WILDCARD_MATCH(CUSTOMER, 'Ac*Corp')");
    assert.equal(
        valueFilterExpression({ name: "CUSTOMER", type: "text" }, "Ac\\*Corp", { typed: true }),
        "LOWER(CUSTOMER) = LOWER('Ac*Corp')");
    assert.equal(
        valueFilterExpression({ name: "CUSTOMER", type: "text" }, "Ac*Corp"),
        "LOWER(CUSTOMER) = LOWER('Ac*Corp')");
    assert.equal(
        valueFilterExpression({ name: "AMOUNT", type: "number" }, "not a number", { typed: true }),
        "1 = 0");
});
