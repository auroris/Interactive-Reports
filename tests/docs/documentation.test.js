import assert from "node:assert/strict";
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, extname, join, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const excludedDirectories = new Set([
  ".git",
  "artifacts",
  "bin",
  "node_modules",
  "obj",
  "playwright-report",
  "releases",
  "test-results",
]);

function filesUnder(directory, extension) {
  const files = [];
  for (const entry of readdirSync(directory)) {
    if (excludedDirectories.has(entry)) continue;
    const path = join(directory, entry);
    if (statSync(path).isDirectory()) files.push(...filesUnder(path, extension));
    else if (extname(path).toLowerCase() === extension) files.push(path);
  }
  return files;
}

const markdownFiles = filesUnder(repositoryRoot, ".md");

function withoutFencedCode(markdown) {
  return markdown.replace(/^\s*```[\s\S]*?^\s*```\s*$/gm, "");
}

function destinations(markdown) {
  const prose = withoutFencedCode(markdown);
  const values = [];
  for (const match of prose.matchAll(/!?\[[^\]]*\]\(([^)]+)\)/g)) values.push(match[1]);
  for (const match of prose.matchAll(/<(?:a|img)\b[^>]+(?:href|src)=["']([^"']+)["'][^>]*>/gi)) {
    values.push(match[1]);
  }
  return values;
}

function localDestination(raw) {
  const value = raw.trim().startsWith("<")
    ? raw.trim().slice(1, raw.trim().indexOf(">"))
    : raw.trim().split(/\s+["']/)[0];
  if (!value || value.startsWith("/") || value.startsWith("//")) return null;
  if (/^[a-z][a-z0-9+.-]*:/i.test(value)) return null;
  return decodeURIComponent(value);
}

function headingAnchors(markdown) {
  const anchors = new Set();
  const duplicates = new Map();
  for (const line of withoutFencedCode(markdown).split(/\r?\n/)) {
    const match = /^ {0,3}#{1,6}\s+(.+?)\s*#*\s*$/.exec(line);
    if (!match) continue;
    const base = match[1]
      .replace(/<[^>]*>/g, "")
      .replace(/[`*_~]/g, "")
      .trim()
      .toLowerCase()
      .replace(/[^\p{L}\p{N}\s_-]/gu, "")
      .replace(/\s+/g, "-");
    const count = duplicates.get(base) ?? 0;
    duplicates.set(base, count + 1);
    anchors.add(count === 0 ? base : `${base}-${count}`);
  }
  return anchors;
}

test("local Markdown links and heading anchors resolve", () => {
  const failures = [];
  for (const source of markdownFiles) {
    const markdown = readFileSync(source, "utf8");
    for (const raw of destinations(markdown)) {
      const destination = localDestination(raw);
      if (destination === null) continue;
      const [relativePath, fragment] = destination.split("#", 2);
      const target = relativePath ? resolve(dirname(source), relativePath) : source;
      if (!existsSync(target)) {
        failures.push(`${source}: missing ${destination}`);
        continue;
      }
      if (fragment && extname(target).toLowerCase() === ".md") {
        const anchors = headingAnchors(readFileSync(target, "utf8"));
        if (!anchors.has(fragment.toLowerCase())) {
          failures.push(`${source}: missing anchor ${destination}`);
        }
      }
    }
  }
  assert.deepEqual(failures, []);
});

test("documented npm scripts exist", () => {
  const scripts = JSON.parse(readFileSync(join(repositoryRoot, "package.json"), "utf8")).scripts;
  const failures = [];
  for (const source of markdownFiles) {
    const markdown = readFileSync(source, "utf8");
    for (const match of markdown.matchAll(/\bnpm\s+run\s+([\w:-]+)/g)) {
      if (!(match[1] in scripts)) failures.push(`${source}: npm run ${match[1]}`);
    }
  }
  assert.deepEqual(failures, []);
});

test("fenced code blocks are balanced and name their language", () => {
  const failures = [];
  for (const source of markdownFiles) {
    let open = null;
    const lines = readFileSync(source, "utf8").split(/\r?\n/);
    for (let index = 0; index < lines.length; index += 1) {
      const match = /^ {0,3}(`{3,}|~{3,})(.*)$/.exec(lines[index]);
      if (!match) continue;
      if (open === null) {
        if (!match[2].trim()) failures.push(`${source}:${index + 1}: missing fence language`);
        open = { character: match[1][0], length: match[1].length, line: index + 1 };
      } else if (match[1][0] === open.character && match[1].length >= open.length
                 && !match[2].trim()) {
        open = null;
      }
    }
    if (open !== null) failures.push(`${source}:${open.line}: unclosed code fence`);
  }
  assert.deepEqual(failures, []);
});

test("documentation does not use retired API names or numbered architecture references", () => {
  const retired = [
    "IReportAccessService",
    "ReportAccessRequest",
    "Resource.Definition",
    "InteractiveReportDefinition",
  ];
  const failures = [];
  for (const source of markdownFiles) {
    const markdown = readFileSync(source, "utf8");
    for (const name of retired) {
      if (markdown.includes(name)) failures.push(`${source}: ${name}`);
    }
    if (/\bArchitecture\b[^\n]*§\s*\d/i.test(markdown)) {
      failures.push(`${source}: numbered Architecture reference`);
    }
  }
  assert.deepEqual(failures, []);
});
