import { defineConfig, devices } from "@playwright/test";

export default defineConfig({
    testDir: "./browser-tests",
    outputDir: "./test-results/playwright",
    fullyParallel: false,
    workers: 1,
    timeout: 45_000,
    expect: {
        timeout: 10_000,
    },
    reporter: [["list"], ["html", { open: "never" }]],
    use: {
        ...devices["Desktop Chrome"],
        baseURL: process.env.SWARMUI_URL ?? "http://127.0.0.1:7801",
        screenshot: "only-on-failure",
        trace: "retain-on-failure",
        video: "retain-on-failure",
    },
});
