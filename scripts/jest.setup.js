/**
 * Jest setup that loads SwarmUI's shared JS utilities (util.js, translator.js,
 * site.js) into the test environment so VideoStages tests can exercise the
 * real browser-side helpers (doToggleGroup, copy_current_image_params, etc.)
 * instead of mocks.
 *
 * The SwarmUI scripts were written for a browser <script> tag and rely on
 * sloppy-mode quirks (implicit global assignments in translator.js, top-level
 * `let`/`function` declarations in site.js). We use indirect eval to evaluate
 * them in the global scope so their function/variable declarations attach to
 * globalThis / window in the jsdom environment.
 */

const fs = require("node:fs");
const path = require("node:path");
const v8 = require("node:v8");

// jsdom's global scope lacks structuredClone (used by the timeline store);
// node's v8 serializer provides the same structured-clone semantics.
if (typeof globalThis.structuredClone !== "function") {
    globalThis.structuredClone = (value) => v8.deserialize(v8.serialize(value));
}

// Locate SwarmUI's shared wwwroot/js by walking up from this file. The fixed
// "../../../wwwroot/js" depth only holds for the main checkout; inside a git
// worktree the extension is nested deeper, so search ancestors for the dir.
const findSwarmJsDir = () => {
    let dir = __dirname;
    for (;;) {
        const candidate = path.join(dir, "wwwroot", "js");
        if (fs.existsSync(candidate)) {
            return candidate;
        }
        const parent = path.dirname(dir);
        if (parent === dir) {
            return path.resolve(__dirname, "..", "..", "..", "wwwroot", "js");
        }
        dir = parent;
    }
};

const SWARM_JS_DIR = findSwarmJsDir();

// biome-ignore lint/security/noGlobalEval: Indirect eval runs trusted local SwarmUI scripts in the global scope so their sloppy-mode declarations reach window.
const indirectEval = eval;

const loadSwarmScript = (relativePath) => {
    const absolutePath = path.join(SWARM_JS_DIR, relativePath);
    const source = fs.readFileSync(absolutePath, "utf8");
    indirectEval(source);
};

/**
 * site.js instantiates `new InputBrowserHelper()` at module load, which calls
 * `getRequiredElementById('input_image_browser_upload_container')`. Stub that
 * element in jsdom so the script finishes loading cleanly.
 */
const inputBrowserStub = document.createElement("div");
inputBrowserStub.id = "input_image_browser_upload_container";
document.body.appendChild(inputBrowserStub);

loadSwarmScript("util.js");
loadSwarmScript("translator.js");
loadSwarmScript("site.js");
