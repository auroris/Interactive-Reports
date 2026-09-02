import assert from "node:assert/strict";
import test from "node:test";
import { Window } from "happy-dom";
import {
    DATE_MASK_PRESETS,
    MAX_MASK_LENGTH,
    NUMBER_MASK_PRESETS,
    applyMask,
    formatAgg,
    formatValue,
    maskIsValid,
    masksFor,
} from "../../src/client/report/render/format.js";

const window = new Window({ url: "https://host.example/reports/orders" });
Object.assign(globalThis, { window, document: window.document, Node: window.Node });

const number = (value, code, locale = null) => applyMask(value, "number", code, locale);
const date = (code, locale = null, value = "2026-08-07T14:30:45") => applyMask(value, "date", code, locale);
const SAMPLE = "1234.567";

test("digit placeholders round half away from zero, pad, and group", () => {
    const cases = [
        ["#,##0", "1,235"],
        ["#,##0.0", "1,234.6"],
        ["#,##0.00", "1,234.57"],
        ["#,##0.000", "1,234.567"],
        ["#,##0.0000", "1,234.5670"],
        ["0.00", "1234.57"],
        ["0", "1235"],
        ["#", "1235"],
        ["#,##0.##", "1,234.57"],
        ["#,##0.#", "1,234.6"],
        ["0000000.0", "0001234.6"],
        ["#,###", "1,235"],
        ["?,??0.0?", "1,234.57"],
    ];
    for (const [code, expected] of cases) assert.equal(number(SAMPLE, code), expected, code);
});

test("edge values follow Excel's conventions", () => {
    const cases = [
        ["0.5", "#.##", ".5"],
        ["0.5", "0.##", "0.5"],
        ["0", "#", ""],
        ["0", "0.00", "0.00"],
        ["7", "000", "007"],
        ["1234", "000,000", "001,234"],
        ["1234567.891", "#,##0.00", "1,234,567.89"],
        ["999.995", "0.00", "1000.00"],
        ["2.5", "0", "3"],
        ["-2.5", "0", "-3"],
        ["-0.004", "0.00", "0.00"],
        [12345678901234567890.5, "0", "12345678901234567000"],
        ["12345678901234567890.5", "#,##0.0", "12,345,678,901,234,567,890.5"],
        [0.1 + 0.2, "0.00000000000000000", "0.30000000000000004"],
        ["1e3", "#,##0", "1,000"],
        [123n, "0.0", "123.0"],
    ];
    for (const [value, code, expected] of cases) assert.equal(number(value, code), expected, `${value} ${code}`);
});

test("trailing commas scale by thousands", () => {
    const cases = [
        ["#,##0,", "1,235"],
        ["#,##0.0,", "1,234.6"],
        ["#,##0.00,,", "1.23"],
        ["#,##0,\"K\"", "1,235K"],
        ["0.0,,\" M\"", "1.2 M"],
    ];
    for (const [code, expected] of cases) assert.equal(number("1234567", code), expected, code);
});

test("percent multiplies by one hundred only when bare", () => {
    const cases = [
        ["0.123456", "0%", "12%"],
        ["0.123456", "0.0%", "12.3%"],
        ["0.123456", "0.00%", "12.35%"],
        ["6590.01", "0.00%", "659001.00%"],
        ["6590.01", "#,##0.00\"%\"", "6,590.01%"],
        ["6590.01", "#,##0.00 %", "659,001.00 %"],
        ["0.5", "%0", "%50"],
        ["-0.25", "0.0%", "-25.0%"],
        ["0.1234567890123456789", "0.0000000000000000%", "12.3456789012345679%"],
    ];
    for (const [value, code, expected] of cases) assert.equal(number(value, code), expected, `${value} ${code}`);
});

test("literals frame the digits", () => {
    const cases = [
        ["$#,##0.00", "$1,234.57"],
        ["€#,##0.00", "€1,234.57"],
        ["£#,##0.00", "£1,234.57"],
        ["¥#,##0", "¥1,235"],
        ["#,##0.00 \"CAD\"", "1,234.57 CAD"],
        ["\"CA$\"#,##0.00", "CA$1,234.57"],
        ["0 \"units\"", "1235 units"],
        ["0\\h", "1235h"],
        ["_(0_)", " 1235 "],
        ["*-0", "1235"],
        ["[$€-407]#,##0.00", "€1,234.57"],
        ["[$ CHF]#,##0", " CHF1,235"],
        ["[Red]#,##0", "1,235"],
        ["[Color 10]#,##0", "1,235"],
        ["0 \"a\" \"b\"", "1235 a b"],
        ["(0)", "(1235)"],
        ["+0", "+1235"],
        ["0 \"<b>\"", "1235 <b>"],
        ["0/0", null],
        ["0 \"x\" 0", null],
    ];
    for (const [code, expected] of cases) assert.equal(number(SAMPLE, code), expected, code);
});

test("sections select by sign", () => {
    const cases = [
        ["1234.567", "#,##0.00;(#,##0.00)", "1,234.57"],
        ["-1234.567", "#,##0.00;(#,##0.00)", "(1,234.57)"],
        ["-1234.567", "#,##0.00", "-1,234.57"],
        ["-1234.567", "$#,##0.00", "-$1,234.57"],
        ["-1234.567", "#,##0.00;\"minus \"#,##0.00", "minus 1,234.57"],
        ["0", "#,##0.00;(#,##0.00);\"-\"", "-"],
        ["0", "#,##0.00;(#,##0.00)", "0.00"],
        ["5", "#,##0.00;(#,##0.00);\"-\"", "5.00"],
        ["-0.001", "0.00;(0.00)", "(0.00)"],
        ["1", "0;0;0;@", null],
        ["1", "\"x\";0", null],
        ["1", "0;\"a\";\"b\"", "1"],
        ["-1", "0;\"a\";\"b\"", "a"],
        ["0", "0;\"a\";\"b\"", "b"],
    ];
    for (const [value, code, expected] of cases) assert.equal(number(value, code), expected, `${value} ${code}`);
});

test("unsupported number codes render nothing so the caller falls through", () => {
    const codes = ["General", "#,##0.00E+00", "0.00e-0", "@", "0 0", "0\"unterminated", "0\\", "0_", "[>100]0", "[Red",
        "abc", "", "   ", "0.0,0", "%", "\"only text\""];
    for (const code of codes) assert.equal(number(SAMPLE, code), null, JSON.stringify(code));
    assert.equal(number(SAMPLE, null), null);
    assert.equal(number(SAMPLE, undefined), null);
    assert.equal(number(SAMPLE, 42), null);
    assert.equal(number("not a number", "0.00"), null);
    assert.equal(number(Number.NaN, "0.00"), null);
    assert.equal(number(null, "0.00"), null);
});

test("over-long masks are rejected on the boundary", () => {
    const code = "0".repeat(MAX_MASK_LENGTH);
    assert.equal(number("1", code), "0".repeat(MAX_MASK_LENGTH - 1) + "1");
    assert.equal(number("1", code + "0"), null);
});

test("separators and minus sign follow the locale while the code fixes the digits", () => {
    assert.match(number("1234.5", "#,##0.00", "fr-CA"), /^1[\s  ]234,50$/);
    assert.match(number("-1234.5", "#,##0.00", "fr-CA"), /^-1[\s  ]234,50$/);
    assert.match(number("1234.5", "$#,##0.00", "fr-CA"), /^\$1[\s  ]234,50$/);
    assert.match(number("-1234.5", "#,##0.00;(#,##0.00)", "fr-CA"), /^\(1[\s  ]234,50\)$/);
    assert.equal(number("1234.5", "0.00", "fr-CA"), "1234,50");
    assert.equal(number("1234", "000,000", "fr-CA").replace(/[\s  ]/g, " "), "001 234");
    assert.equal(number("1234567.891", "#,##0.00", "fr-CA").replace(/[\s\u00a0\u202f]/g, " "), "1 234 567,89");
    assert.equal(number("0.5", "0.0%", "en"), "50.0%");
    assert.equal(number("0.5", "0.0%", "fr-CA"), "50,0%");
    assert.equal(number("1234.5", "#,##0.00", "en-US"), "1,234.50");
});

test("date tokens render every width", () => {
    const cases = [
        ["yyyy-mm-dd", "2026-08-07"],
        ["yyyy-mm-dd hh:mm", "2026-08-07 14:30"],
        ["yyyy-mm-dd hh:mm:ss", "2026-08-07 14:30:45"],
        ["YYYY-MM-DD HH:MM:SS", "2026-08-07 14:30:45"],
        ["h:mm AM/PM", "2:30 PM"],
        ["hh:mm am/pm", "02:30 pm"],
        ["h:mm A/P", "2:30 P"],
        ["h:mm a/p", "2:30 p"],
        ["hh:mm:ss", "14:30:45"],
        ["h:m:s", "14:30:45"],
        ["mm/dd/yyyy", "08/07/2026"],
        ["dd/mm/yy", "07/08/26"],
        ["m/d/yyyy", "8/7/2026"],
        ["mmm d, yyyy", "Aug 7, 2026"],
        ["mmmm d, yyyy", "August 7, 2026"],
        ["mmmmm", "A"],
        ["ddd", "Fri"],
        ["dddd, mmmm d, yyyy", "Friday, August 7, 2026"],
        ["mmm d, yyyy h:mm AM/PM", "Aug 7, 2026 2:30 PM"],
        ["yyyy\"年\"m\"月\"d\"日\"", "2026年8月7日"],
        ["d\\.m\\.yyyy", "7.8.2026"],
        ["mm:ss", "30:45"],
        ["h \"h\" mm \"min\"", "14 h 30 min"],
        ["yyyy", "2026"],
        ["yy", "26"],
    ];
    for (const [code, expected] of cases) assert.equal(date(code), expected, code);
});

test("midnight and noon on the twelve-hour clock", () => {
    assert.equal(date("h:mm AM/PM", null, "2026-01-01T00:05:00"), "12:05 AM");
    assert.equal(date("hh:mm", null, "2026-01-01T00:05:00"), "00:05");
    assert.equal(date("h:mm", null, "2026-01-01T00:05:00"), "0:05");
    assert.equal(date("h:mm AM/PM", null, "2026-01-01T12:15:00"), "12:15 PM");
    assert.equal(date("yyyy-mm-dd hh:mm:ss", null, "2026-01-01"), "2026-01-01 00:00:00");
    assert.equal(date("yyyy-mm-dd hh:mm", null, "2026-01-01 09:07"), "2026-01-01 09:07");
});

test("date names follow the locale", () => {
    assert.match(date("mmmm d, yyyy", "fr-CA").toLocaleLowerCase("fr-CA"), /^août 7, 2026$/);
    assert.match(date("dddd", "fr-CA").toLocaleLowerCase("fr-CA"), /^vendredi$/);
    assert.match(date("mmm", "fr-CA").toLocaleLowerCase("fr-CA"), /^aoû/);
    assert.equal(date("dd.mm.yyyy", "fr-CA"), "07.08.2026");
});

test("unsupported date codes render nothing", () => {
    const codes = ["[h]:mm", "yyyy-mm-dd Q", "yyyyy", "hhh", "mmmmmm", "\"only text\"", "", "yyyy\"open", "yyyy]"];
    for (const code of codes) assert.equal(date(code), null, JSON.stringify(code));
    assert.equal(date("yyyy-mm-dd", null, "not a date"), null);
    assert.equal(date("yyyy-mm-dd", null, null), null);
});

test("format codes flow through cell, aggregate, and text renderers", () => {
    assert.equal(formatValue("6590.01", "number", true, "#,##0.00\"%\""), "6,590.01%");
    assert.equal(formatValue("6590.01", "number", false, "abc"), "6,590.01", "an invalid code falls through to default text");
    assert.equal(formatValue("6590", "number", false, "abc"), "6590");
    assert.equal(formatValue(null, "number", false, "0.00"), "");
    assert.equal(formatAgg("1234.5", "number", "#,##0"), "1,235");
    assert.equal(formatAgg(null, "number", "#,##0"), "—");
    assert.equal(formatValue("2026-08-07T00:00:00", "date", false, "dd/mm/yyyy"), "07/08/2026");
    assert.equal(formatValue("2026-08-07T00:00:00", "date", false, "[h]"), "2026-08-07", "an invalid date code falls through");
    assert.equal(formatValue("hello", "text", false, "0.00"), "hello", "text columns ignore masks");
});

test("presets document themselves with rendered samples", () => {
    const numbers = masksFor("number");
    assert.deepEqual(numbers.map(p => p.value), NUMBER_MASK_PRESETS);
    assert.ok(numbers.every(p => p.example && p.example !== p.value));
    assert.equal(numbers.find(p => p.value === "#,##0.00").example, "1,234.57");
    assert.equal(numbers.find(p => p.value === "0.00%").example, "123456.70%");
    assert.equal(numbers.find(p => p.value === "#,##0.00\"%\"").example, "1,234.57%");
    const dates = masksFor("date");
    assert.deepEqual(dates.map(p => p.value), DATE_MASK_PRESETS);
    assert.equal(dates.find(p => p.value === "yyyy-mm-dd hh:mm").example, "2026-08-07 14:30");
    assert.deepEqual(masksFor("text"), []);
    assert.deepEqual(masksFor("bool"), []);
    assert.match(masksFor("number", "fr-CA").find(p => p.value === "#,##0.00").example, /1[\s  ]234,57/);
});

test("mask validity mirrors what the formatter accepts", () => {
    assert.equal(maskIsValid("number", ""), true);
    assert.equal(maskIsValid("number", null), true);
    assert.equal(maskIsValid("number", "#,##0.00"), true);
    assert.equal(maskIsValid("number", "percent2"), false, "the old closed tokens are gone");
    assert.equal(maskIsValid("number", "abc"), false);
    assert.equal(maskIsValid("number", "yyyy-mm-dd"), false);
    assert.equal(maskIsValid("date", "yyyy-mm-dd"), true);
    assert.equal(maskIsValid("date", "dateLong"), false, "the old closed tokens are gone");
    assert.equal(maskIsValid("date", "#,##0"), false);
    assert.equal(maskIsValid("text", "anything"), false);
    assert.equal(maskIsValid("text", ""), true);
});
