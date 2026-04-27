import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const projectRoot = path.resolve(__dirname, "..");
const coverageDirectory = path.join(projectRoot, "coverage");
const coverageInputPath = path.join(coverageDirectory, "coverage-final.json");
const coverageOutputPath = path.join(
    coverageDirectory,
    "uncovered-functions.json",
);

const toRelativePath = (filePath) => {
    return path.relative(projectRoot, filePath).split(path.sep).join("/");
};

const normalizeFunctionName = (name, functionId) => {
    if (!name || /^\(anonymous_\d+\)$/.test(name)) {
        return `(anonymous:${functionId})`;
    }

    return name;
};

const compareFunctionsByLocation = (left, right) => {
    if (left.line !== right.line) {
        return left.line - right.line;
    }

    if (left.column !== right.column) {
        return left.column - right.column;
    }

    return left.name.localeCompare(right.name);
};

const readCoverageInput = async () => {
    try {
        return await fs.readFile(coverageInputPath, "utf8");
    } catch (error) {
        if (error && typeof error === "object" && "code" in error) {
            if (error.code === "ENOENT") {
                throw new Error(
                    "Missing coverage/coverage-final.json. Run `npm run test:coverage` or `npm run coverage:functions` first.",
                );
            }
        }

        throw error;
    }
};

const rawCoverage = await readCoverageInput();
const coverageByFile = JSON.parse(rawCoverage);

let totalFileCount = 0;
let totalFunctionCount = 0;
let coveredFunctionCount = 0;

const files = [];

for (const [absoluteFilePath, fileCoverage] of Object.entries(coverageByFile)) {
    const functionMap = fileCoverage.fnMap ?? {};
    const functionHits = fileCoverage.f ?? {};
    const functionEntries = Object.entries(functionMap);

    if (functionEntries.length === 0) {
        continue;
    }

    totalFileCount += 1;

    const uncoveredFunctions = [];

    for (const [functionId, functionMetadata] of functionEntries) {
        const hits = Number(functionHits[functionId] ?? 0);
        const start =
            functionMetadata.loc?.start ?? functionMetadata.decl?.start;
        const end = functionMetadata.loc?.end ?? functionMetadata.decl?.end;

        totalFunctionCount += 1;

        if (hits > 0) {
            coveredFunctionCount += 1;
            continue;
        }

        uncoveredFunctions.push({
            name: normalizeFunctionName(functionMetadata.name, functionId),
            line: start?.line ?? functionMetadata.line ?? 0,
            column: start?.column ?? 0,
            endLine: end?.line ?? start?.line ?? functionMetadata.line ?? 0,
            endColumn: end?.column ?? start?.column ?? 0,
            hits,
        });
    }

    if (uncoveredFunctions.length === 0) {
        continue;
    }

    files.push({
        file: toRelativePath(absoluteFilePath),
        uncoveredFunctionCount: uncoveredFunctions.length,
        functions: uncoveredFunctions.sort(compareFunctionsByLocation),
    });
}

files.sort((left, right) => {
    if (left.uncoveredFunctionCount !== right.uncoveredFunctionCount) {
        return right.uncoveredFunctionCount - left.uncoveredFunctionCount;
    }

    return left.file.localeCompare(right.file);
});

const uncoveredFunctionCount = totalFunctionCount - coveredFunctionCount;
const functionCoveragePercent =
    totalFunctionCount === 0
        ? 100
        : Number(
              ((coveredFunctionCount / totalFunctionCount) * 100).toFixed(2),
          );

const report = {
    generatedAt: new Date().toISOString(),
    source: toRelativePath(coverageInputPath),
    summary: {
        totalFileCount,
        filesWithUncoveredFunctions: files.length,
        totalFunctionCount,
        coveredFunctionCount,
        uncoveredFunctionCount,
        functionCoveragePercent,
    },
    files,
};

await fs.mkdir(coverageDirectory, { recursive: true });
await fs.writeFile(coverageOutputPath, `${JSON.stringify(report, null, 2)}\n`);

console.log(
    `Wrote ${toRelativePath(coverageOutputPath)} with ${uncoveredFunctionCount} uncovered functions across ${files.length} files.`,
);
