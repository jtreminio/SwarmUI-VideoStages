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

const SWARM_JS_DIR = path.resolve(__dirname, "..", "..", "..", "wwwroot", "js");

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
