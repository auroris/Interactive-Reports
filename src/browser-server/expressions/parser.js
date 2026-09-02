// Expression lexer and Pratt parser for the portable Interactive Reports expression language.
// Ported from InteractiveReport.Core.Expressions.ExprSyntax.

export class ExprError extends Error {
    constructor(message) {
        super(message);
        this.name = "ExprError";
    }
}

const KEYWORDS = new Set([
    "CASE", "WHEN", "THEN", "ELSE", "END", "AND", "OR", "NOT", "IS", "NULL", "BETWEEN",
]);

const PRECEDENCE = {
    "OR": 1,
    "AND": 2,
    "=": 4,
    "<>": 4,
    "<": 4,
    "<=": 4,
    ">": 4,
    ">=": 4,
    "+": 5,
    "-": 5,
    "||": 5,
    "*": 6,
    "/": 6,
};

const COMPARISON_PREC = 4;

export class Token {
    constructor(kind, text, position) {
        this.kind = kind; // "number" | "string" | "ident" | "quoted_ident" | "op"
        this.text = text;
        this.position = position;
    }
}

export function tokenize(input) {
    const tokens = [];
    let i = 0;
    while (i < input.length) {
        const c = input[i];
        if (/\s/.test(c)) {
            i++;
            continue;
        }

        // Numeric literal
        if (/\d/.test(c)) {
            const start = i;
            while (i < input.length && (/\d/.test(input[i]) || input[i] === ".")) {
                i++;
            }
            tokens.push(new Token("number", input.slice(start, i), start));
            continue;
        }

        // String literal '...' with '' escaping
        if (c === "'") {
            const start = i;
            i++;
            let text = "";
            while (true) {
                if (i >= input.length) {
                    throw new ExprError(`unterminated string starting at position ${start + 1}`);
                }
                if (input[i] === "'") {
                    if (i + 1 < input.length && input[i + 1] === "'") {
                        text += "'";
                        i += 2;
                        continue;
                    }
                    i++;
                    break;
                }
                text += input[i];
                i++;
            }
            tokens.push(new Token("string", text, start));
            continue;
        }

        // Backticked quoted identifier `...` with `` escaping
        if (c === "`") {
            const start = i;
            i++;
            let text = "";
            while (true) {
                if (i >= input.length) {
                    throw new ExprError(`unterminated quoted identifier starting at position ${start + 1}`);
                }
                if (input[i] === "`") {
                    if (i + 1 < input.length && input[i + 1] === "`") {
                        text += "`";
                        i += 2;
                        continue;
                    }
                    i++;
                    break;
                }
                text += input[i];
                i++;
            }
            if (text.length === 0) {
                throw new ExprError(`quoted identifier at position ${start + 1} cannot be empty`);
            }
            tokens.push(new Token("quoted_ident", text, start));
            continue;
        }

        // Identifier or keyword
        if (/[a-zA-Z_]/.test(c)) {
            const start = i;
            while (i < input.length && /[a-zA-Z0-9_$#]/.test(input[i])) {
                i++;
            }
            tokens.push(new Token("ident", input.slice(start, i), start));
            continue;
        }

        // String concatenation ||
        if (c === "|") {
            if (i + 1 < input.length && input[i + 1] === "|") {
                tokens.push(new Token("op", "||", i));
                i += 2;
                continue;
            }
            throw new ExprError(`single '|' at position ${i + 1} (use '||' for concatenation)`);
        }

        // Comparison operators <, <=, <>, >, >=, !=
        if (c === "<" || c === ">" || c === "!") {
            if (i + 1 < input.length && (input[i + 1] === "=" || (c === "<" && input[i + 1] === ">"))) {
                const text = input.slice(i, i + 2) === "!=" ? "<>" : input.slice(i, i + 2);
                tokens.push(new Token("op", text, i));
                i += 2;
                continue;
            }
            if (c === "!") {
                throw new ExprError(`unexpected character '!' at position ${i + 1} (use '<>' or '!=' for not-equal)`);
            }
            tokens.push(new Token("op", c, i));
            i++;
            continue;
        }

        // Punctuation and simple operators
        if ("+-*/(),=".includes(c)) {
            tokens.push(new Token("op", c, i));
            i++;
            continue;
        }

        throw new ExprError(`unexpected character '${c}' at position ${i + 1}`);
    }
    return tokens;
}

export class ExprParser {
    constructor(tokens) {
        this.tokens = tokens;
        this.pos = 0;
        this.depth = 0;
    }

    get current() {
        if (this.pos < this.tokens.length) {
            return this.tokens[this.pos];
        }
        throw new ExprError("unexpected end of expression");
    }

    get atEnd() {
        return this.pos >= this.tokens.length;
    }

    atOp(op) {
        return !this.atEnd && this.current.kind === "op" && this.current.text === op;
    }

    atKeyword(keyword) {
        return !this.atEnd && this.current.kind === "ident" && this.current.text.toUpperCase() === keyword;
    }

    consume() {
        const tok = this.current;
        this.pos++;
        return tok;
    }

    expectOp(op) {
        if (!this.atOp(op)) {
            throw new ExprError(`expected '${op}' at position ${this.current.position + 1}`);
        }
        return this.consume();
    }

    expectKeyword(keyword) {
        if (!this.atKeyword(keyword)) {
            throw new ExprError(`expected '${keyword}' at position ${this.current.position + 1}`);
        }
        return this.consume();
    }

    parseExpr(precedence = 0) {
        if (++this.depth > 64) {
            throw new ExprError("expression nesting exceeds maximum depth");
        }
        try {
            let left = this.parsePrefix();

            while (!this.atEnd) {
                // Postfix IS [NOT] NULL
                if (this.atKeyword("IS")) {
                    if (precedence >= COMPARISON_PREC) break;
                    const pos = this.consume().position;
                    let negated = false;
                    if (this.atKeyword("NOT")) {
                        this.consume();
                        negated = true;
                    }
                    this.expectKeyword("NULL");
                    left = { type: "null_test", operand: left, negated, pos };
                    continue;
                }

                // Postfix BETWEEN ... AND ...
                if (this.atKeyword("BETWEEN")) {
                    if (precedence >= COMPARISON_PREC) break;
                    const pos = this.consume().position;
                    const lower = this.parseExpr(COMPARISON_PREC + 1);
                    this.expectKeyword("AND");
                    const upper = this.parseExpr(COMPARISON_PREC + 1);
                    left = { type: "between", operand: left, lower, upper, pos };
                    continue;
                }

                // Infix binary operator
                let op = null;
                let opPrec = 0;
                if (this.current.kind === "op" && PRECEDENCE[this.current.text] !== undefined) {
                    op = this.current.text;
                    opPrec = PRECEDENCE[op];
                } else if (this.current.kind === "ident") {
                    const upper = this.current.text.toUpperCase();
                    if (upper === "AND" || upper === "OR") {
                        op = upper;
                        opPrec = PRECEDENCE[op];
                    }
                }

                if (!op || opPrec <= precedence) {
                    break;
                }

                const opToken = this.consume();
                const right = this.parseExpr(opPrec);
                left = { type: "binary", op, left, right, pos: opToken.position };
            }

            return left;
        } finally {
            this.depth--;
        }
    }

    parsePrefix() {
        if (this.atEnd) {
            throw new ExprError("unexpected end of expression");
        }

        const tok = this.current;

        // Number
        if (tok.kind === "number") {
            this.consume();
            return { type: "number", value: Number(tok.text), pos: tok.position };
        }

        // String
        if (tok.kind === "string") {
            this.consume();
            return { type: "string", value: tok.text, pos: tok.position };
        }

        // NULL literal
        if (tok.kind === "ident" && tok.text.toUpperCase() === "NULL") {
            this.consume();
            return { type: "null", pos: tok.position };
        }

        // CASE expression
        if (this.atKeyword("CASE")) {
            return this.parseCase();
        }

        // Unary NOT
        if (this.atKeyword("NOT")) {
            const pos = this.consume().position;
            const operand = this.parseExpr(3); // Binds tighter than AND/OR, looser than comparison
            return { type: "unary", op: "NOT", operand, pos };
        }

        // Unary minus
        if (this.atOp("-")) {
            const pos = this.consume().position;
            const operand = this.parseExpr(7);
            return { type: "unary", op: "-", operand, pos };
        }

        // Parenthesized expression
        if (this.atOp("(")) {
            this.consume();
            const expr = this.parseExpr(0);
            this.expectOp(")");
            return expr;
        }

        // Quoted identifier (column reference)
        if (tok.kind === "quoted_ident") {
            this.consume();
            return { type: "col", name: tok.text, pos: tok.position };
        }

        // Identifier: either a function call or a column reference
        if (tok.kind === "ident") {
            const name = this.consume().text;
            if (this.atOp("(")) {
                this.consume();
                const args = [];
                if (!this.atOp(")")) {
                    while (true) {
                        args.push(this.parseExpr(0));
                        if (this.atOp(",")) {
                            this.consume();
                            continue;
                        }
                        break;
                    }
                }
                this.expectOp(")");
                return { type: "call", name: name.toUpperCase(), args, pos: tok.position };
            }
            return { type: "col", name, pos: tok.position };
        }

        throw new ExprError(`unexpected '${tok.text}' at position ${tok.position + 1}`);
    }

    parseCase() {
        const pos = this.consume().position; // consume CASE
        let operand = null;
        if (!this.atKeyword("WHEN")) {
            operand = this.parseExpr(0);
        }

        const whens = [];
        if (!this.atKeyword("WHEN")) {
            throw new ExprError(`CASE expression must contain at least one WHEN clause at position ${pos + 1}`);
        }

        while (this.atKeyword("WHEN")) {
            this.consume();
            const when = this.parseExpr(0);
            this.expectKeyword("THEN");
            const then = this.parseExpr(0);
            whens.push({ when, then });
        }

        let elseNode = null;
        if (this.atKeyword("ELSE")) {
            this.consume();
            elseNode = this.parseExpr(0);
        }

        this.expectKeyword("END");
        return { type: "case", operand, whens, else: elseNode, pos };
    }
}

/**
 * Parses expression source text into a portable AST.
 *
 * @param {string} source
 * @returns {object} AST node
 */
export function parseExpression(source) {
    if (!source || typeof source !== "string" || !source.trim()) {
        throw new ExprError("expression is empty");
    }
    if (source.length > 2000) {
        throw new ExprError("expression exceeds 2000 characters");
    }

    const tokens = tokenize(source);
    const parser = new ExprParser(tokens);
    const ast = parser.parseExpr(0);
    if (!parser.atEnd) {
        throw new ExprError(`unexpected '${parser.current.text}' at position ${parser.current.position + 1}`);
    }
    return ast;
}
