import { defineConfig, devices } from "@playwright/test";
import path from "node:path";

const baseURL = "http://127.0.0.1:5042";
const savedReportsPath = path.resolve("test-results/ui/workbench-saved.db");

export default defineConfig({
    testDir: "./tests/ui/e2e",
    outputDir: "./test-results/ui",
    fullyParallel: true,
    forbidOnly: Boolean(process.env.CI),
    retries: process.env.CI ? 2 : 0,
    reporter: [["line"], ["html", { outputFolder: "playwright-report", open: "never" }]],
    use: {
        baseURL,
        trace: "retain-on-failure",
        screenshot: "only-on-failure",
    },
    projects: [
        {
            name: "chromium",
            use: { ...devices["Desktop Chrome"] },
        },
    ],
    webServer: {
        command: "dotnet run --no-launch-profile --project samples/Workbench --urls http://127.0.0.1:5042",
        url: `${baseURL}/api/reports`,
        reuseExistingServer: false,
        timeout: 120_000,
        env: {
            ASPNETCORE_ENVIRONMENT: "Development",
            InteractiveReportTest__SavedReportsPath: savedReportsPath,
            InteractiveReport__SavedReports__Connection: "PlaywrightSavedReports",
            InteractiveReport__SavedReports__Dialect: "Sqlite",
        },
    },
});
