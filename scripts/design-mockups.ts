export {};

declare function escapeHtmlNoBr(text: string): string;

type Stage = {
    model: string;
    steps: number;
    cfg: number;
    sampler: string;
    scheduler: string;
};
type Keyframe = { frame: number; source: string };
type Clip = {
    prompt: string;
    duration: number;
    stages: Stage[];
    keyframes: Keyframe[];
    audio: string;
    join: string;
    overlap: number;
    skipped: boolean;
    source: string;
    resize?: string;
    seedvr?: boolean;
    audioOnly?: boolean;
    reuseAudio?: boolean;
    separateAudio?: boolean;
    loras: { model: string; weight: number }[];
    references: { source: string; strength: number }[];
    controls: { preset: string; strength: number }[];
    relays: { prompt: string; begin: number; end: number }[];
};
type Project = {
    clips: Clip[];
    global: string;
    width: number;
    height: number;
    fps: number;
    enabled: boolean;
};
const key = "videostages-design-study-v1";
const model =
    "LTX-2/ltx-2.5-22b-distilled-transformer-comfy-int8-convrot.safetensors";
const stage = (): Stage => ({
    model,
    steps: 8,
    cfg: 1,
    sampler: "Euler",
    scheduler: "Normal",
});
const clip = (): Clip => ({
    prompt: "",
    duration: 10,
    stages: [stage()],
    keyframes: [],
    audio: "Native",
    join: "Cut",
    overlap: 16,
    skipped: false,
    source: "None",
    loras: [],
    references: [],
    controls: [],
    relays: [],
});
function initial(): Project {
    const first = clip();
    first.stages.push({ ...stage(), steps: 4 });
    first.loras.push({ model: "Camera motion LoRA (sample)", weight: 0.7 });
    const second = clip();
    second.prompt = "The camera emerges from the trees into a sunlit clearing.";
    return {
        clips: [first, second],
        global: "Cinematic natural light, realistic motion.",
        width: 768,
        height: 1216,
        fps: 24,
        enabled: true,
    };
}
let project: Project;
try {
    project = JSON.parse(sessionStorage.getItem(key) || "null") || initial();
} catch {
    project = initial();
}
let selected = 0;
let section = "Prompt";
let item = 0;
let sectionItems: Record<string, number> = {};
let position = 15;
const layout = document.body.dataset.layout || "strip";
const workbench = document.getElementById("study-workbench") as HTMLElement;
const shell = document.querySelector(".study-shell") as HTMLElement;
const globalPrompt = document.getElementById(
    "global-prompt",
) as HTMLTextAreaElement;
const names = [
    "Prompt",
    "Stages",
    "Keyframes",
    "Audio",
    "References",
    "LoRAs",
    "IC-LoRAs",
    "Relay prompts",
    "Source video",
];
const collections: Record<
    string,
    "stages" | "keyframes" | "references" | "loras" | "controls" | "relays"
> = {
    Stages: "stages",
    Keyframes: "keyframes",
    References: "references",
    LoRAs: "loras",
    "IC-LoRAs": "controls",
    "Relay prompts": "relays",
};
const e = (value: unknown) => escapeHtmlNoBr(String(value));
const current = () => project.clips[selected];
const save = () => sessionStorage.setItem(key, JSON.stringify(project));
const button = (text: string, attributes: string = "") => {
    const classes = attributes.match(/class="([^"]*)"/)?.[1] || "";
    return `<button type="button" class="basic-button ${classes}" ${attributes.replace(/class="[^"]*"/, "")}>${text}</button>`;
};
function count(name: string, c = current()) {
    const collection = collections[name];
    return collection ? c[collection].length : null;
}
function field(
    label: string,
    path: string,
    value: string | number,
    type = "text",
    attributes = "",
) {
    return `<label>${label}<input type="${type}" data-field="${path}" value="${e(value)}" ${attributes}></label>`;
}
function select(label: string, path: string, value: string, choices: string[]) {
    return `<label>${label}<select data-field="${path}">${choices.map((choice) => `<option ${value === choice ? "selected" : ""}>${e(choice)}</option>`).join("")}</select></label>`;
}
function duration() {
    return project.clips.reduce(
        (total, c, index) =>
            total +
            (c.skipped ? 0 : c.duration) -
            (index && !c.skipped && c.join === "Crossfade"
                ? c.overlap / project.fps
                : 0),
        0,
    );
}
function output() {
    return `<div class="study-output"><button data-action="enabled"><strong>${project.enabled ? "ENABLED" : "DISABLED"}</strong></button><button data-section="Output">${project.width}×${project.height} · ${project.fps} fps · ${duration().toFixed(1)}s</button></div>`;
}
function sectionNames(index = selected) {
    return [...names, ...(index > 0 ? ["Join"] : []), "Output"];
}
function nav(c: Clip, index: number) {
    return `<nav class="study-sections" aria-label="Clip ${index} sections">${sectionNames(
        index,
    )
        .map(
            (name) =>
                `<button data-clip="${index}" data-section="${name}" class="${index === selected && name === section ? "active" : ""}"><span>${name}</span><small>${count(name, c) ?? ""}</small></button>`,
        )
        .join("")}</nav>`;
}
function flow() {
    return `<div class="study-flow">${output()}${project.clips.map((c, index) => `${index ? button(`→ ${e(c.join)} →`, `class="study-join" data-clip="${index}" data-section="Join"`) : ""}<div class="study-flow-card ${selected === index ? "selected" : ""}" data-card="${index}"><div><button data-clip="${index}"><b>Clip ${index}${c.skipped ? " · skipped" : ""}</b></button><button data-clip="${index}" data-section="Stages">${c.stages.length} stages</button><span class="study-note">${c.duration}s</span></div><button class="study-prompt-shortcut" aria-label="Edit clip ${index} prompt" data-clip="${index}" data-section="Prompt" data-focus="prompt">Prompt · ${e(c.prompt || "Using global prompt")}</button></div>`).join("")}${button("+ Add clip", 'data-action="add-clip"')}</div>`;
}
function tree() {
    return `<aside class="study-tree">${output()}${project.clips.map((c, index) => `${index ? button(`↓ ${e(c.join)}`, `data-clip="${index}" data-section="Join" class="study-join"`) : ""}<div class="study-tree-heading ${index === selected ? "selected" : ""}">${button(`Clip ${index} <small>· ${c.duration}s${c.skipped ? " · skipped" : ""}</small>`, `data-clip="${index}"`)}</div>${index === selected ? nav(c, index) : `<nav class="study-sections">${button(`Prompt · ${e(c.prompt ? `${c.prompt.slice(0, 24)}…` : "Global")}`, `data-clip="${index}" data-section="Prompt" data-focus="prompt"`)}</nav>`}`).join("")}${button("+ Add clip", 'data-action="add-clip"')}</aside>`;
}
function editorSection(section: string) {
    let item = sectionItems[section] || 0;
    const c = current();
    const collection = collections[section];
    const length = collection ? c[collection].length : 0;
    item = Math.max(0, Math.min(item, length - 1));
    let fields = "";
    let tabs = "";
    let actions = "";
    if (collection) {
        actions = button(
            `+ ${section === "Stages" ? "Stage" : section === "Keyframes" ? "Keyframe" : "Add"}`,
            'data-action="add-item"',
        );
        if (length > 1 || (length && section !== "Stages")) {
            tabs = `<div class="study-subnav">${Array.from({ length }, (_, i) => button(section === "Stages" ? `S${i} · ${i ? "Refine" : "Generate"}` : section === "Keyframes" ? `◆ Frame ${c.keyframes[i].frame}` : `${section} ${i}`, `data-item="${i}" class="${i === item ? "active" : ""}"`)).join("")}</div>`;
        }
        if (length && !(section === "Stages" && item === 0))
            actions += button("Remove", 'data-action="remove-item"');
    }
    if (section === "Prompt") {
        fields = `<label>Prompt <textarea data-field="prompt" id="clip-prompt" rows="3" placeholder="Blank inherits the global prompt…">${e(c.prompt)}</textarea></label><div class="study-form-grid">${field("Duration (s)", "duration", c.duration, "number", 'min="1" max="120" step="0.5"')}<div class="wide study-note" data-inheritance>${c.prompt ? "Using this clip’s prompt." : `Using global prompt: ${e(project.global)}`}</div></div><details><summary>Other clip settings</summary><div class="study-form-grid">${select("Reference resize", "resize", c.resize || "Crop", ["Crop", "Stretch", "Fit (black padding)"])}<label class="study-check"><input type="checkbox" data-field="seedvr" ${c.seedvr ? "checked" : ""}>Use SeedVR</label><label class="study-check"><input type="checkbox" data-field="audioOnly" ${c.audioOnly ? "checked" : ""}>Audio only</label></div></details>`;
        actions =
            button(
                c.skipped ? "Include clip" : "Skip clip",
                'data-action="skip"',
            ) +
            (project.clips.length > 1
                ? button("Delete clip", 'data-action="delete-clip"')
                : "");
    } else if (section === "Stages" && length) {
        const s = c.stages[item];
        fields = `<div class="study-form-grid"><div class="wide">${select("Model", `stages.${item}.model`, s.model, [model, "LTX-2/LTX2-5_Stubelius_Remix_Beta1_int8_convrot.safetensors"])}</div>${field("Steps", `stages.${item}.steps`, s.steps, "number", 'min="1" max="100"')}${field("CFG Scale", `stages.${item}.cfg`, s.cfg, "number", 'min="0" step="0.1"')}${select("Sampler", `stages.${item}.sampler`, s.sampler, ["Euler", "Euler Ancestral (Randomizing)", "Heun (2x Slow)"])}${select("Scheduler", `stages.${item}.scheduler`, s.scheduler, ["Normal", "Simple", "Karras"])}</div>`;
    } else if (section === "Keyframes") {
        fields = `<div class="study-note">Click the ruler to add a keyframe, or enter an exact frame below.</div><div class="study-ruler" aria-label="Keyframe positions"><span>0s</span><span>${c.duration / 2}s</span><span>${c.duration}s</span>${c.keyframes.map((k, i) => `<button aria-label="Keyframe at frame ${k.frame}" data-item="${i}" style="left:${(k.frame / (c.duration * project.fps)) * 100}%">◆</button>`).join("")}</div><div class="study-keyframe-tools"><label for="exact-frame">Frame</label><input id="exact-frame" type="number" min="0" max="${c.duration * project.fps - 1}" value="${position}">${button("+ Add at frame", 'data-action="add-at-frame"')}</div>${length ? `<div class="study-form-grid">${select("Image Source", `keyframes.${item}.source`, c.keyframes[item].source, ["Refiner Output", "Base Output", "Upload"])}${field("Attach at Frame", `keyframes.${item}.frame`, c.keyframes[item].frame, "number", `min="0" max="${c.duration * project.fps - 1}"`)}</div>` : '<div class="study-note">No keyframes yet.</div>'}`;
    } else if (section === "Audio") {
        fields = `<div class="study-form-grid">${select("Audio Source", "audio", c.audio, ["Native", "Upload", "ControlNet", ...(selected ? ["Previous Clip Output"] : [])])}<label class="study-check"><input type="checkbox" data-field="reuseAudio" ${c.reuseAudio ? "checked" : ""}>Reuse captured stage audio</label><label class="study-check"><input type="checkbox" data-field="separateAudio" ${c.separateAudio ? "checked" : ""}>Save audio separately</label></div>${c.audio === "Upload" ? '<label>Audio file<input type="file" accept="audio/*"></label>' : ""}`;
    } else if (section === "Join") {
        fields = `<div class="study-form-grid">${select("Join type", "join", c.join, ["Cut", "Continue", "Crossfade"])}${c.join === "Crossfade" ? field("Overlap (frames)", "overlap", c.overlap, "number", 'min="1" max="120"') : ""}</div><p class="study-note">Clip ${selected - 1} → Clip ${selected}. ${c.join === "Crossfade" ? `${c.overlap} shared frames · ${(c.overlap / project.fps).toFixed(2)}s overlap.` : c.join === "Continue" ? "Continue from the previous clip’s ending." : "Hard cut between clips."}</p>`;
    } else if (section === "Source video") {
        fields = `<div class="study-form-grid">${select("Video source", "source", c.source, ["None", "Upload", ...(selected ? ["Previous Clip Output"] : [])])}</div>${c.source === "Upload" ? '<label>Video file<input type="file" accept="video/*"></label>' : ""}`;
    } else if (section === "Output") {
        fields = `<div class="study-form-grid">${field("Width", "project.width", project.width, "number", 'min="64" step="32"')}${field("Height", "project.height", project.height, "number", 'min="64" step="32"')}${field("Frame rate", "project.fps", project.fps, "number", 'min="1" max="120"')}</div>`;
    } else if (section === "LoRAs" && length) {
        const l = c.loras[item];
        fields = `<div class="study-form-grid"><div class="wide">${field("LoRA model", `loras.${item}.model`, l.model)}</div>${field("Weight", `loras.${item}.weight`, l.weight, "number", 'step="0.1"')}</div>`;
    } else if (section === "References" && length) {
        const ref = c.references[item];
        fields = `<div class="study-form-grid">${select("Image source", `references.${item}.source`, ref.source, ["Upload", "Base Output", "Refiner Output"])}${field("Strength", `references.${item}.strength`, ref.strength, "number", 'step="0.1" min="0"')}</div>${ref.source === "Upload" ? '<label>Reference image<input type="file" accept="image/*"></label>' : ""}`;
    } else if (section === "IC-LoRAs" && length) {
        const control = c.controls[item];
        fields = `<div class="study-form-grid">${select("Preset", `controls.${item}.preset`, control.preset, ["Union Control", "Depth", "Pose"])}${field("Strength", `controls.${item}.strength`, control.strength, "number", 'step="0.1" min="0"')}</div>`;
    } else if (section === "Relay prompts" && length) {
        const relay = c.relays[item];
        fields = `<label>Prompt<textarea data-field="relays.${item}.prompt" rows="2">${e(relay.prompt)}</textarea></label><div class="study-form-grid">${field("Begin (s)", `relays.${item}.begin`, relay.begin, "number", `min="0" max="${c.duration}" step="0.1"`)}${field("End (s)", `relays.${item}.end`, relay.end, "number", `min="0" max="${c.duration}" step="0.1"`)}</div>`;
    } else {
        fields = `<div class="study-empty">No ${section.toLowerCase()} in this clip. Use + Add to configure one.</div>`;
    }
    sectionItems[section] = item;
    return `<section class="study-section input-group input-group-open" data-editor-section="${section}" aria-labelledby="section-${sectionNames().indexOf(section)}"><div class="study-editor-heading input-group-header"><strong id="section-${sectionNames().indexOf(section)}">${section === "Output" ? "Output" : `Clip ${selected} · ${section}${section === "Stages" ? ` · S${item}` : ""}`}</strong><small>${c.duration}s${c.skipped ? " · skipped" : ""}</small><div class="study-actions">${actions}</div></div>${tabs}<div class="study-fields">${fields}</div></section>`;
}
function markSection() {
    for (const link of workbench.querySelectorAll<HTMLElement>(
        "nav [data-section]",
    )) {
        const active =
            link.dataset.section === section &&
            Number(link.dataset.clip) === selected;
        link.classList.toggle("active", active);
        if (active) link.setAttribute("aria-current", "location");
        else link.removeAttribute("aria-current");
    }
}
function followScroll() {
    const editor = workbench.querySelector<HTMLElement>(".study-editor");
    if (!editor) return;
    const top = editor.getBoundingClientRect().top + 12;
    let active = "Prompt";
    for (const group of editor.querySelectorAll<HTMLElement>(
        "[data-editor-section]",
    )) {
        if (group.getBoundingClientRect().top <= top)
            active = group.dataset.editorSection as string;
    }
    section = active;
    markSection();
}
function jumpTo(name: string) {
    const editor = workbench.querySelector<HTMLElement>(".study-editor");
    const group = workbench.querySelector<HTMLElement>(
        `[data-editor-section="${name}"]`,
    );
    if (!editor || !group) return;
    editor.scrollTop +=
        group.getBoundingClientRect().top - editor.getBoundingClientRect().top;
    section = name;
    markSection();
}
function render(jump?: string) {
    const previous =
        workbench.querySelector<HTMLElement>(".study-editor")?.scrollTop || 0;
    const railScroll =
        workbench.querySelector<HTMLElement>(
            ".study-tree, .study-workspace > .study-sections",
        )?.scrollTop || 0;
    const stripScroll =
        workbench.querySelector<HTMLElement>(".study-flow")?.scrollLeft || 0;
    if (selected === 0 && section === "Join") section = "Prompt";
    const content = sectionNames().map(editorSection).join("");
    if (layout === "outline")
        workbench.innerHTML = `<div class="study-tree-layout">${tree()}<main class="study-editor" aria-label="Clip editor">${content}</main></div>`;
    else
        workbench.innerHTML = `${flow()}<div class="study-workspace">${nav(current(), selected)}<main class="study-editor" aria-label="Clip editor">${content}</main></div>`;
    const editor = workbench.querySelector<HTMLElement>(".study-editor");
    if (editor) {
        editor.scrollTop = previous;
        editor.addEventListener("scroll", followScroll, { passive: true });
    }
    workbench
        .querySelector(".study-tree, .study-workspace > .study-sections")
        ?.scrollTo(0, railScroll);
    workbench.querySelector(".study-flow")?.scrollTo(stripScroll, 0);
    if (jump) jumpTo(jump);
    else followScroll();
    updateMeasurements();
}
function refreshNavigation() {
    const flowElement = workbench.querySelector(".study-flow");
    if (flowElement) {
        const scroll = flowElement.scrollLeft;
        flowElement.outerHTML = flow();
        workbench.querySelector(".study-flow")?.scrollTo(scroll, 0);
    }
    const outline = workbench.querySelector(".study-tree");
    if (outline) {
        const scroll = outline.scrollTop;
        outline.outerHTML = tree();
        workbench.querySelector(".study-tree")?.scrollTo(0, scroll);
    }
}
function addKeyframe(frame: number) {
    const c = current();
    const bounded = Math.max(
        0,
        Math.min(Math.round(frame), Math.round(c.duration * project.fps) - 1),
    );
    const found = c.keyframes.findIndex((k) => k.frame === bounded);
    if (found >= 0) item = found;
    else {
        c.keyframes.push({ frame: bounded, source: "Refiner Output" });
        item = c.keyframes.length - 1;
    }
    sectionItems.Keyframes = item;
    position = bounded;
}
workbench.addEventListener("click", (event) => {
    const target = event.target as HTMLElement;
    const group = target.closest<HTMLElement>("[data-editor-section]");
    if (group) {
        section = group.dataset.editorSection as string;
        item = sectionItems[section] || 0;
    }
    const control = target.closest<HTMLButtonElement>("button");
    if (!control) {
        const ruler = target.closest<HTMLElement>(".study-ruler");
        if (ruler) {
            const bounds = ruler.getBoundingClientRect();
            addKeyframe(
                ((event.clientX - bounds.left) / bounds.width) *
                    current().duration *
                    project.fps,
            );
            save();
            render("Keyframes");
            workbench
                .querySelector<HTMLSelectElement>(
                    '[data-editor-section="Keyframes"] select[data-field$=".source"]',
                )
                ?.focus();
        }
        return;
    }
    const changedClip =
        control.dataset.clip !== undefined &&
        Number(control.dataset.clip) !== selected;
    if (changedClip) {
        selected = Number(control.dataset.clip);
        sectionItems = {};
    }
    if (control.dataset.section) section = control.dataset.section;
    item = sectionItems[section] || 0;
    if (!changedClip && control.dataset.section && !control.dataset.action) {
        jumpTo(section);
        if (control.dataset.focus)
            document
                .getElementById("clip-prompt")
                ?.focus({ preventScroll: true });
        return;
    }
    if (control.dataset.item !== undefined) item = Number(control.dataset.item);
    const action = control.dataset.action;
    const c = current();
    if (action === "add-clip") {
        project.clips.push(clip());
        selected = project.clips.length - 1;
        item = 0;
        section = "Prompt";
        sectionItems = {};
    }
    if (action === "delete-clip" && project.clips.length > 1) {
        project.clips.splice(selected, 1);
        selected = Math.max(0, selected - 1);
        item = 0;
        sectionItems = {};
        section = "Prompt";
    }
    if (action === "skip") c.skipped = !c.skipped;
    if (action === "enabled") project.enabled = !project.enabled;
    if (action === "add-at-frame")
        addKeyframe(
            Number(
                (document.getElementById("exact-frame") as HTMLInputElement)
                    .value,
            ),
        );
    if (action === "add-item") {
        if (section === "Stages")
            c.stages.push({ ...c.stages[c.stages.length - 1] });
        if (section === "Keyframes") addKeyframe(position);
        if (section === "LoRAs")
            c.loras.push({ model: "Choose a LoRA (sample)", weight: 1 });
        if (section === "References")
            c.references.push({ source: "Upload", strength: 1 });
        if (section === "IC-LoRAs")
            c.controls.push({ preset: "Union Control", strength: 1 });
        if (section === "Relay prompts")
            c.relays.push({ prompt: "", begin: 0, end: c.duration });
        if (section !== "Keyframes") item = (count(section) || 1) - 1;
    }
    if (action === "remove-item") {
        const collection = collections[section];
        if (collection && !(section === "Stages" && item === 0))
            c[collection].splice(item, 1);
        item = Math.max(0, item - 1);
    }
    sectionItems[section] = item;
    save();
    render(
        changedClip ||
            control.dataset.section ||
            action === "add-clip" ||
            action === "delete-clip"
            ? section
            : undefined,
    );
    if (control.dataset.focus || action === "add-clip")
        document.getElementById("clip-prompt")?.focus();
    if (
        action === "add-at-frame" ||
        (action === "add-item" && section === "Keyframes")
    )
        workbench
            .querySelector<HTMLInputElement>('input[data-field$=".frame"]')
            ?.focus();
    if (action === "add-clip") {
        workbench.querySelector(".study-flow")?.scrollTo(100000, 0);
        workbench.querySelector(".study-tree")?.scrollTo(0, 100000);
    }
});
workbench.addEventListener("input", (event) => {
    const input = event.target as HTMLInputElement;
    const path = input.dataset.field;
    if (!path || (input.type === "number" && !input.validity.valid)) return;
    const parts = path.split(".");
    // Both layouts edit the same sample project.
    let owner = (parts[0] === "project"
        ? project
        : current()) as unknown as Record<string, unknown>;
    if (parts[0] === "project") parts.shift();
    const property = parts.pop() as string;
    for (const part of parts) owner = owner[part] as Record<string, unknown>;
    owner[property] =
        input.type === "number"
            ? Number(input.value)
            : input.type === "checkbox"
              ? input.checked
              : input.value;
    save();
    refreshNavigation();
    const inherited = workbench.querySelector("[data-inheritance]");
    if (inherited && path === "prompt")
        inherited.textContent = input.value
            ? "Using this clip’s prompt."
            : `Using global prompt: ${project.global}`;
});
workbench.addEventListener("change", (event) => {
    const input = event.target as HTMLInputElement;
    if (input.tagName === "SELECT") {
        render();
    }
    if (
        input.type === "number" &&
        input.dataset.field &&
        input.validity.valid
    ) {
        render();
    }
});
globalPrompt.value = project.global;
globalPrompt.addEventListener("input", () => {
    project.global = globalPrompt.value;
    save();
    const inherited = workbench.querySelector("[data-inheritance]");
    if (inherited && !current().prompt)
        inherited.textContent = `Using global prompt: ${project.global}`;
});
document
    .querySelector(`[data-design="${layout}"]`)
    ?.setAttribute("aria-current", "page");
document.getElementById("reset-demo")?.addEventListener("click", () => {
    project = initial();
    selected = 0;
    section = "Prompt";
    item = 0;
    sectionItems = {};
    globalPrompt.value = project.global;
    save();
    render("Prompt");
});
document.getElementById("panel-size")?.addEventListener("change", (event) => {
    shell.style.setProperty(
        "--panel-height",
        `${(event.target as HTMLSelectElement).value}vh`,
    );
});
const divider = document.getElementById("panel-divider") as HTMLElement;
divider.addEventListener("pointerdown", (event) => {
    divider.setPointerCapture(event.pointerId);
});
divider.addEventListener("pointermove", (event) => {
    if (divider.hasPointerCapture(event.pointerId))
        shell.style.setProperty(
            "--panel-height",
            `${Math.max(170, Math.min(innerHeight - 235, innerHeight - event.clientY - 39))}px`,
        );
});
divider.addEventListener("keydown", (event) => {
    if (event.key === "ArrowUp" || event.key === "ArrowDown") {
        event.preventDefault();
        const height = workbench.getBoundingClientRect().height;
        shell.style.setProperty(
            "--panel-height",
            `${Math.max(170, Math.min(innerHeight - 235, height + (event.key === "ArrowUp" ? 20 : -20)))}px`,
        );
    }
});
function updateMeasurements() {
    const label = document.getElementById("measurements");
    if (label)
        label.textContent = `${innerWidth}×${innerHeight} · panel ${Math.round(workbench.getBoundingClientRect().height)}px`;
}
new ResizeObserver(updateMeasurements).observe(workbench);
save();
render();
