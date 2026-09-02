// Runs the documentation screenshot capture against a temporary Workbench instance.

import { spawn } from "node:child_process";
import { once } from "node:events";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const workbench = path.join(root, "samples", "Workbench");
const captureScript = path.join(root, "scripts", "docs-screenshots.mjs");
const base = process.env.IR_DOCS_BASE ?? "http://127.0.0.1:5042";
const readyUrl = new URL("/api/reports/orders/view", base);
const startupTimeout = Number(process.env.IR_DOCS_START_TIMEOUT ?? 120_000);

let server;
let serverSpawnError;
let stopPromise;
let receivedSignal;

const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

function describeExit(child) {
    return child.signalCode
        ? `signal ${child.signalCode}`
        : `exit code ${child.exitCode}`;
}

async function waitForWorkbench() {
    const deadline = Date.now() + startupTimeout;
    let lastError;

    while (Date.now() < deadline) {
        if (serverSpawnError) throw serverSpawnError;
        if (server.exitCode !== null || server.signalCode !== null) {
            throw new Error(`Workbench stopped before it became ready (${describeExit(server)}).`);
        }

        try {
            const response = await fetch(readyUrl, { signal: AbortSignal.timeout(2_000) });
            if (response.ok) return;
            lastError = new Error(`readiness request returned HTTP ${response.status}`);
        } catch (error) {
            lastError = error;
        }

        await delay(250);
    }

    const detail = lastError instanceof Error ? ` Last error: ${lastError.message}` : "";
    throw new Error(`Workbench did not become ready at ${readyUrl} within ${startupTimeout}ms.${detail}`);
}

function run(command, args) {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, { cwd: root, env: process.env, stdio: "inherit" });
        child.once("error", reject);
        child.once("exit", (code, signal) => {
            if (code === 0) resolve();
            else reject(new Error(`${command} failed with ${signal ? `signal ${signal}` : `exit code ${code}`}.`));
        });
    });
}

async function stopWorkbench() {
    if (stopPromise) return stopPromise;
    if (!server || server.exitCode !== null || server.signalCode !== null) return;

    stopPromise = (async () => {
        console.log("Stopping Workbench...");
        if (process.platform === "win32") {
            const killer = spawn("taskkill", ["/pid", String(server.pid), "/t", "/f"], {
                cwd: root,
                stdio: "ignore",
            });
            await once(killer, "exit");
        } else {
            try {
                process.kill(-server.pid, "SIGTERM");
            } catch (error) {
                if (error?.code !== "ESRCH") throw error;
            }
        }

        if (server.exitCode === null && server.signalCode === null) {
            await Promise.race([once(server, "exit"), delay(5_000)]);
        }

        if (server.exitCode === null && server.signalCode === null) {
            if (process.platform === "win32") server.kill("SIGKILL");
            else process.kill(-server.pid, "SIGKILL");
        }
    })();

    return stopPromise;
}

for (const signal of ["SIGINT", "SIGTERM"]) {
    process.once(signal, () => {
        receivedSignal ??= signal;
        void stopWorkbench();
    });
}

let failure;
try {
    console.log(`Starting Workbench at ${base}...`);
    server = spawn("dotnet", ["run", "--project", workbench, "--no-launch-profile"], {
        cwd: root,
        detached: process.platform !== "win32",
        env: {
            ...process.env,
            ASPNETCORE_ENVIRONMENT: process.env.ASPNETCORE_ENVIRONMENT ?? "Development",
            ASPNETCORE_URLS: base,
        },
        stdio: "inherit",
    });
    server.once("error", error => { serverSpawnError = error; });

    await waitForWorkbench();
    console.log("Workbench is ready. Capturing documentation screenshots...");
    await run(process.execPath, [captureScript]);
} catch (error) {
    failure = error;
} finally {
    await stopWorkbench();
}

if (failure && !receivedSignal) console.error(failure);
if (failure || receivedSignal) process.exitCode = receivedSignal === "SIGINT" ? 130 : receivedSignal ? 143 : 1;
