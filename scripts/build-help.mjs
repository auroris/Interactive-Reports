// Renders docs/USER-GUIDE*.md into the standalone help pages the packaged report serves at
// {prefix}/ui/help.{locale}.html and shows in its Help window. Every screenshot the guide
// references is re-encoded and embedded as a base64 data URI, so a page is one self-contained
// file: no image routes, no relative paths, and the same bytes whether the page is opened on
// its own or fetched into the dialog.
//
//   node scripts/build-help.mjs            # lossless WebP (default; flat UI screenshots stay small)
//   IR_HELP_IMAGE=avif node scripts/build-help.mjs
//
// Output: src/InteractiveReport.Client.Json/Ui/dist/help.<locale>.html. `npm run build` runs
// this after the bundles, and the .csproj embeds everything in Ui/dist.

import { readFile, readdir, writeFile, mkdir } from "node:fs/promises";
import path from "node:path";
import { Marked } from "marked";
import sharp from "sharp";

const docsDir = path.resolve("docs");
const outDir = path.resolve("src/InteractiveReport.Client.Json/Ui/dist");
const imageFormat = (process.env.IR_HELP_IMAGE ?? "webp").toLowerCase();

/** Encodes one screenshot as a data URI in the selected format. */
async function encodeImage(file) {
    const image = sharp(file);
    const buffer = imageFormat === "avif"
        ? await image.avif({ quality: 55, effort: 6 }).toBuffer()
        : await image.webp({ lossless: true, effort: 6 }).toBuffer();
    return `data:image/${imageFormat};base64,${buffer.toString("base64")}`;
}

/** GitHub-style heading slug, so the guide's own table-of-contents links keep working. */
const slugify = text => text.toLowerCase().trim()
    .replace(/<[^>]+>/g, "")
    .replace(/[^\p{L}\p{N}\s-]/gu, "")
    .replace(/\s+/g, "-");

const escapeAttribute = value => String(value)
    .replaceAll("&", "&amp;").replaceAll('"', "&quot;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");

// Every selector is scoped under .ir-help so the same sheet is safe inside the report's shadow
// root, where the packaged --ir-* tokens apply; the fallbacks style the page when opened alone.
const css = `
.ir-help {
    font-family: var(--ir-font, system-ui, -apple-system, "Segoe UI", sans-serif);
    font-size: var(--ir-font-size, 13px);
    line-height: 1.5;
    color: var(--ir-text, #1f2733);
    max-width: 46rem;
    margin: 0 auto;
    outline: none;
}
.ir-help h1 { font-size: 20px; font-weight: 600; margin: 0 0 12px; }
.ir-help h2 { font-size: 16px; font-weight: 600; margin: 26px 0 8px; padding-bottom: 4px; border-bottom: 1px solid var(--ir-border-light, #e8ebee); }
.ir-help h3 { font-size: 14px; font-weight: 600; margin: 18px 0 6px; }
.ir-help p { margin: 8px 0; }
.ir-help ul, .ir-help ol { margin: 8px 0; padding-left: 1.6em; }
.ir-help li { margin: 3px 0; }
.ir-help a { color: var(--ir-accent, #0572ce); }
.ir-help img { display: block; max-width: 100%; height: auto; margin: 10px 0; border: 1px solid var(--ir-border, #d5dbe1); border-radius: 4px; }
.ir-help table { border-collapse: collapse; width: 100%; margin: 8px 0; font-size: 12.5px; }
.ir-help th, .ir-help td { border: 1px solid var(--ir-border-light, #e8ebee); padding: 5px 8px; text-align: left; vertical-align: top; }
.ir-help th { background: var(--ir-bg-header, #f2f4f6); font-weight: 600; }
.ir-help code { font-family: ui-monospace, "Cascadia Code", Consolas, monospace; font-size: 12px; background: var(--ir-bg-soft, #f7f8f9); border: 1px solid var(--ir-border-light, #e8ebee); border-radius: 3px; padding: 0 4px; white-space: nowrap; }
.ir-help pre { background: var(--ir-bg-soft, #f7f8f9); border: 1px solid var(--ir-border-light, #e8ebee); border-radius: 4px; padding: 8px 10px; overflow-x: auto; }
.ir-help pre code { border: 0; padding: 0; white-space: pre; }
.ir-help hr { border: 0; border-top: 1px solid var(--ir-border-light, #e8ebee); margin: 22px 0; }
.ir-help strong { font-weight: 600; }
body.ir-help-page { margin: 0; padding: 24px 16px 40px; background: #fff; }
`;

/** Renders one Markdown guide into a self-contained HTML document. */
async function renderGuide(markdownFile, locale) {
    const markdown = await readFile(markdownFile, "utf8");
    const images = new Map();
    for (const match of markdown.matchAll(/!\[[^\]]*\]\(([^)\s]+)/g)) {
        const href = match[1];
        if (!images.has(href)) images.set(href, await encodeImage(path.resolve(path.dirname(markdownFile), href)));
    }
    const title = /^#\s+(.+)$/m.exec(markdown)?.[1] ?? "Help";

    const marked = new Marked({
        gfm: true,
        renderer: {
            heading({ tokens, depth }) {
                const text = this.parser.parseInline(tokens);
                return `<h${depth} id="${slugify(text)}">${text}</h${depth}>\n`;
            },
            image({ href, text }) {
                const source = images.get(href);
                if (!source) throw new Error(`${markdownFile}: image ${href} was not encoded`);
                return `<img src="${source}" alt="${escapeAttribute(text)}">`;
            },
        },
    });
    const body = marked.parse(markdown);
    return `<!doctype html>
<html lang="${locale}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeAttribute(title.replace(/<[^>]+>/g, ""))}</title>
<style>${css}</style>
</head>
<body class="ir-help-page">
<article class="ir-help">
${body}</article>
</body>
</html>
`;
}

await mkdir(outDir, { recursive: true });
const guides = (await readdir(docsDir)).filter(name => /^USER-GUIDE(\.[A-Za-z-]+)?\.md$/.test(name));
for (const name of guides) {
    const locale = /^USER-GUIDE\.([A-Za-z-]+)\.md$/.exec(name)?.[1] ?? "en";
    const html = await renderGuide(path.join(docsDir, name), locale);
    const target = path.join(outDir, `help.${locale}.html`);
    await writeFile(target, html);
    console.log(`${path.relative(process.cwd(), target)}: ${(Buffer.byteLength(html) / 1024).toFixed(0)} KB (${imageFormat} images)`);
}
