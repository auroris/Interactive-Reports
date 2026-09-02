import assert from "node:assert/strict";
import test from "node:test";
import { parseExpression, ExprError, tokenize } from "../../src/browser-server/expressions/parser.js";

test("lexer tokenizes numbers, strings, identifiers, and backticked identifiers", () => {
    const tokens = tokenize("AMOUNT > 100.5 AND `Order Date` = '2025-01-01'");
    assert.equal(tokens.length, 7);
    assert.equal(tokens[0].kind, "ident");
    assert.equal(tokens[0].text, "AMOUNT");
    assert.equal(tokens[1].kind, "op");
    assert.equal(tokens[1].text, ">");
    assert.equal(tokens[2].kind, "number");
    assert.equal(tokens[2].text, "100.5");
    assert.equal(tokens[3].kind, "ident");
    assert.equal(tokens[3].text, "AND");
    assert.equal(tokens[4].kind, "quoted_ident");
    assert.equal(tokens[4].text, "Order Date");
    assert.equal(tokens[5].kind, "op");
    assert.equal(tokens[5].text, "=");
    assert.equal(tokens[6].kind, "string");
    assert.equal(tokens[6].text, "2025-01-01");
});

test("parser handles simple arithmetic and comparisons with precedence", () => {
    const ast = parseExpression("A + B * 2 > 10");
    assert.equal(ast.type, "binary");
    assert.equal(ast.op, ">");
    assert.equal(ast.left.type, "binary");
    assert.equal(ast.left.op, "+");
    assert.equal(ast.left.right.type, "binary");
    assert.equal(ast.left.right.op, "*");
    assert.equal(ast.right.type, "number");
    assert.equal(ast.right.value, 10);
});

test("parser handles IS NULL and IS NOT NULL", () => {
    const isNull = parseExpression("NOTES IS NULL");
    assert.equal(isNull.type, "null_test");
    assert.equal(isNull.negated, false);
    assert.equal(isNull.operand.name, "NOTES");

    const isNotNull = parseExpression("NOTES IS NOT NULL");
    assert.equal(isNotNull.type, "null_test");
    assert.equal(isNotNull.negated, true);
    assert.equal(isNotNull.operand.name, "NOTES");
});

test("parser handles BETWEEN", () => {
    const ast = parseExpression("AMOUNT BETWEEN 10 AND 50");
    assert.equal(ast.type, "between");
    assert.equal(ast.operand.name, "AMOUNT");
    assert.equal(ast.lower.value, 10);
    assert.equal(ast.upper.value, 50);
});

test("parser handles CASE expressions", () => {
    const searchedCase = parseExpression("CASE WHEN AMOUNT > 1000 THEN 'HIGH' ELSE 'LOW' END");
    assert.equal(searchedCase.type, "case");
    assert.equal(searchedCase.operand, null);
    assert.equal(searchedCase.whens.length, 1);
    assert.equal(searchedCase.whens[0].when.op, ">");
    assert.equal(searchedCase.whens[0].then.value, "HIGH");
    assert.equal(searchedCase.else.value, "LOW");

    const simpleCase = parseExpression("CASE STATUS WHEN 'NEW' THEN 1 WHEN 'SHIPPED' THEN 2 END");
    assert.equal(simpleCase.type, "case");
    assert.equal(simpleCase.operand.name, "STATUS");
    assert.equal(simpleCase.whens.length, 2);
    assert.equal(simpleCase.else, null);
});

test("parser handles function calls and nested arguments", () => {
    const ast = parseExpression("CONTAINS(CUSTOMER, 'Corp') AND ROUND(AMOUNT, 2) > 0");
    assert.equal(ast.type, "binary");
    assert.equal(ast.op, "AND");
    assert.equal(ast.left.type, "call");
    assert.equal(ast.left.name, "CONTAINS");
    assert.equal(ast.left.args.length, 2);
    assert.equal(ast.right.type, "binary");
    assert.equal(ast.right.left.name, "ROUND");
    assert.equal(ast.right.left.args.length, 2);
});

test("parser throws ExprError on invalid syntax", () => {
    assert.throws(() => parseExpression(""), ExprError);
    assert.throws(() => parseExpression("AMOUNT > "), ExprError);
    assert.throws(() => parseExpression("'unterminated"), ExprError);
    assert.throws(() => parseExpression("CASE WHEN A THEN 1"), ExprError);
});
