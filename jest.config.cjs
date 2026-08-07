const { createJsWithTsPreset } = require("ts-jest");

/** @type {import('jest').Config} */
module.exports = {
    ...createJsWithTsPreset({
        tsconfig: "./tsconfig.jest.json",
    }),
    clearMocks: true,
    collectCoverageFrom: [
        "<rootDir>/frontend/**/*.ts",
        "!<rootDir>/frontend/**/*.test.ts",
        "!<rootDir>/frontend/**/*.d.ts",
        "!<rootDir>/frontend/__test_helpers__/**",
        "!<rootDir>/frontend/main.ts",
    ],
    coverageDirectory: "<rootDir>/coverage",
    coverageReporters: ["json", "json-summary", "text-summary"],
    // "summary" prints counts only — no reason for a failure, not even a plain bad assertion.
    reporters: process.env.JEST_VERBOSE ? ["default"] : ["summary"],
    setupFiles: ["<rootDir>/scripts/jest.setup.js"],
    setupFilesAfterEnv: ["<rootDir>/scripts/jest.setupAfterEnv.js"],
    testEnvironment: "jsdom",
    testMatch: ["<rootDir>/frontend/**/*.test.ts"],
};
