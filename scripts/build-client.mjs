import { build, context } from "esbuild";

const options = {
    entryPoints: [
        "src/client/ir.js",
        "src/client/ir-admin.js",
        "src/client/ir-chart.js",
    ],
    outdir: "src/InteractiveReport.AspNetCore/Ui/dist",
    bundle: true,
    entryNames: "[name]",
    format: "esm",
    target: "es2022",
    loader: { ".css": "text" },
    minify: true,
    sourcemap: true,
    logLevel: "info",
};

if (process.argv.includes("--watch")) {
    const buildContext = await context(options);
    await buildContext.watch();
    console.log("Watching InteractiveReport client sources…");
} else {
    await build(options);
}
