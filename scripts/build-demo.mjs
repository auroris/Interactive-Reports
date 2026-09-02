// Build script that produces a fully self-contained demo directory ready for Cloudflare Pages.

import fs from "node:fs";
import path from "node:path";
import { build } from "esbuild";

const ROOT = process.cwd();
const DEMO_DIR = path.join(ROOT, "demo");

// 1. Ensure client UI bundle is built
const uiDist = path.join(ROOT, "src", "InteractiveReport.Client.Json", "Ui", "dist");
if (!fs.existsSync(path.join(uiDist, "ir.js"))) {
    console.log("Building client UI bundle...");
    const { execSync } = await import("node:child_process");
    execSync("npm run build:client", { stdio: "inherit" });
}

// 2. Bundle browser-server into demo/browser-server.js
console.log("Bundling browser-server for demo...");
await build({
    entryPoints: [path.join(ROOT, "src", "browser-server", "index.js")],
    outfile: path.join(DEMO_DIR, "browser-server.js"),
    bundle: true,
    format: "esm",
    target: "es2022",
    minify: true,
    sourcemap: true,
    external: ["sql.js"],
    logLevel: "info",
});

// 3. Copy sql.js wasm assets into demo/
console.log("Copying SQLite WASM assets into demo/...");
const sqlJsDist = path.join(ROOT, "node_modules", "sql.js", "dist");
fs.copyFileSync(path.join(sqlJsDist, "sql-wasm.js"), path.join(DEMO_DIR, "sql-wasm.js"));
fs.copyFileSync(path.join(sqlJsDist, "sql-wasm.wasm"), path.join(DEMO_DIR, "sql-wasm.wasm"));

// 4. Copy ir.js, ir-chart.js, help.en.html into demo/
console.log("Copying Interactive Reports client assets into demo/...");
fs.copyFileSync(path.join(uiDist, "ir.js"), path.join(DEMO_DIR, "ir.js"));
fs.copyFileSync(path.join(uiDist, "ir-chart.js"), path.join(DEMO_DIR, "ir-chart.js"));
if (fs.existsSync(path.join(uiDist, "help.en.html"))) {
    fs.copyFileSync(path.join(uiDist, "help.en.html"), path.join(DEMO_DIR, "help.en.html"));
}

console.log("Self-contained demo build complete in demo/");
