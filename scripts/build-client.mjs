import { build, context } from "esbuild";

const options = {
    entryPoints: [
        "src/InteractiveReport.AspNetCore/Ui/src/ir.js",
        "src/InteractiveReport.AspNetCore/Ui/src/ir-admin.js",
        "src/InteractiveReport.AspNetCore/Ui/src/ir-chart.js",
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
