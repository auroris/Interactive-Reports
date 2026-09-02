import assert from "node:assert/strict";
import test from "node:test";
import { parseExpression } from "../../src/browser-server/expressions/parser.js";
import { emitSqlite } from "../../src/browser-server/expressions/emitter.js";

test("emitter produces parameterized SQL for comparisons and arithmetic", () => {
    const ast = parseExpression("AMOUNT + 5 > 100");
    const { sql, bindings } = emitSqlite(ast);
    assert.equal(sql, `(("AMOUNT" + ?) > ?)`);
    assert.deepEqual(bindings, [5, 100]);
});

test("emitter maps physical column aliases when provided", () => {
    const ast = parseExpression("AMOUNT > 50");
    const { sql, bindings } = emitSqlite(ast, { AMOUNT: "__irc0" });
    assert.equal(sql, `("__irc0" > ?)`);
    assert.deepEqual(bindings, [50]);
});

test("emitter produces SQLite CONTAINS and STARTS_WITH syntax", () => {
    const containsAst = parseExpression("CONTAINS(CUSTOMER, 'Acme')");
    const containsRes = emitSqlite(containsAst);
    assert.equal(containsRes.sql, `(LOWER("CUSTOMER") LIKE ? ESCAPE '\\')`);
    assert.deepEqual(containsRes.bindings, ["%acme%"]);

    const startsWithAst = parseExpression("STARTS_WITH(CUSTOMER, 'Stark')");
    const startsWithRes = emitSqlite(startsWithAst);
    assert.equal(startsWithRes.sql, `(LOWER("CUSTOMER") LIKE ? ESCAPE '\\')`);
    assert.deepEqual(startsWithRes.bindings, ["stark%"]);
});

test("emitter produces SQLite date functions", () => {
    const yearAst = parseExpression("YEAR(ORDER_DATE) = 2025");
    const yearRes = emitSqlite(yearAst);
    assert.equal(yearRes.sql, `(CAST(strftime('%Y', "ORDER_DATE") AS INTEGER) = ?)`);
    assert.deepEqual(yearRes.bindings, [2025]);

    const truncAst = parseExpression("DATE_TRUNC('month', ORDER_DATE)");
    const truncRes = emitSqlite(truncAst);
    assert.equal(truncRes.sql, `strftime('%Y-%m-01', "ORDER_DATE")`);
});

test("emitter produces SQLite IN_LIST syntax", () => {
    const inAst = parseExpression("IN_LIST(STATUS, 'NEW', 'PENDING')");
    const inRes = emitSqlite(inAst);
    assert.equal(inRes.sql, `("STATUS" IN (?, ?))`);
    assert.deepEqual(inRes.bindings, ["NEW", "PENDING"]);
});
