import { readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";

const directory = new URL("../docs/design-mockups/", import.meta.url);
const shell = await readFile(new URL("shell.html", directory), "utf8");
for (const layout of ["strip", "outline"]) {
    await writeFile(
        new URL(`${layout}.html`, directory),
        shell.replace('data-layout="LAYOUT"', `data-layout="${layout}"`),
    );
}
await build({
    entryPoints: [fileURLToPath(new URL("design-mockups.ts", import.meta.url))],
    outfile: fileURLToPath(new URL("mockups.js", directory)),
    bundle: true,
    format: "iife",
    target: "es2022",
});
