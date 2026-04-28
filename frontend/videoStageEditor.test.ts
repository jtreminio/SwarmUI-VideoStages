import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import {
    stubAceStepFunRegistry,
    stubBase2EditStageRegistry,
} from "./__test_helpers__/registries";
import { __resetPersistenceForTests } from "./persistence";
import { videoStageEditor } from "./videoStageEditor";

const flushReRender = async (): Promise<void> => {
    jest.runOnlyPendingTimers();
    await Promise.resolve();
};

function must<T>(value: T | null | undefined): T {
    if (value == null) {
        expect(value).not.toBeNull();
        throw new Error("unreachable");
    }
    return value;
}

function initEditor(): void {
    const editor = videoStageEditor();
    editor.init();
}

function appendCoreDimensionInputs(widthVal: string, heightVal: string): void {
    const coreWidthInput = document.createElement("input");
    coreWidthInput.type = "number";
    coreWidthInput.id = "input_width";
    coreWidthInput.value = widthVal;
    document.body.appendChild(coreWidthInput);

    const coreHeightInput = document.createElement("input");
    coreHeightInput.type = "number";
    coreHeightInput.id = "input_height";
    coreHeightInput.value = heightVal;
    document.body.appendChild(coreHeightInput);
}

function clickDocumentAddClip(): void {
    must(
        document.querySelector(
            '[data-clip-action="add-clip"]',
        ) as HTMLButtonElement | null,
    ).click();
}

const OPEN_GROUP_GLYPH = "\u2B9F";

interface ParsedRef {
    source: string;
    frame: number;
    fromEnd?: boolean;
    expanded?: boolean;
    uploadFileName?: string | null;
    uploadedImage?: {
        data?: string;
        fileName?: string | null;
    } | null;
}

interface ParsedStage {
    model: string;
    control?: number;
    controlNetStrength?: number;
    steps?: number;
    cfgScale?: number;
    upscale?: number;
    upscaleMethod?: string;
    refStrengths?: number[];
    expanded?: boolean;
    skipped?: boolean;
}

interface ParsedClip {
    duration?: number;
    audioSource?: string;
    controlNetSource?: string;
    controlNetLora?: string;
    saveAudioTrack?: boolean;
    clipLengthFromAudio?: boolean;
    reuseAudio?: boolean;
    uploadedAudio?: {
        data?: string;
        fileName?: string | null;
    } | null;
    width?: number;
    height?: number;
    expanded?: boolean;
    skipped?: boolean;
    refs?: ParsedRef[];
    stages?: ParsedStage[];
}

interface ParsedConfig {
    width?: number;
    height?: number;
    fps?: number;
    clips: ParsedClip[];
}

const setupParameterPanel = (): void => {
    const groupContent = document.createElement("div");
    groupContent.id = "input_group_content_videostages";
    document.body.appendChild(groupContent);

    const stagesInput = document.createElement("input");
    stagesInput.type = "text";
    stagesInput.id = "input_videostages";
    document.body.appendChild(stagesInput);

    const vsWidthInput = document.createElement("input");
    vsWidthInput.type = "number";
    vsWidthInput.id = "input_vswidth";
    vsWidthInput.value = "0";
    document.body.appendChild(vsWidthInput);

    const vsHeightInput = document.createElement("input");
    vsHeightInput.type = "number";
    vsHeightInput.id = "input_vsheight";
    vsHeightInput.value = "0";
    document.body.appendChild(vsHeightInput);

    const vsFpsInput = document.createElement("input");
    vsFpsInput.type = "number";
    vsFpsInput.id = "input_vsfps";
    vsFpsInput.value = "24";
    document.body.appendChild(vsFpsInput);

    const groupToggle = document.createElement("input");
    groupToggle.type = "checkbox";
    groupToggle.id = "input_group_content_videostages_toggle";
    groupToggle.checked = true;
    document.body.appendChild(groupToggle);

    const videoModel = document.createElement("select");
    videoModel.id = "input_videomodel";
    const noVideoModelOption = document.createElement("option");
    noVideoModelOption.value = "";
    noVideoModelOption.text = "None";
    videoModel.appendChild(noVideoModelOption);
    const videoModelOption = document.createElement("option");
    videoModelOption.value = "ltx-2.3-22b-dev";
    videoModelOption.text = "ltx-2.3-22b-dev";
    videoModel.appendChild(videoModelOption);
    videoModel.value = "";
    document.body.appendChild(videoModel);

    const loras = document.createElement("select");
    loras.id = "input_loras";
    for (const value of [
        "ltx-ic-lora.safetensors",
        "detail-lora.safetensors",
    ]) {
        const opt = document.createElement("option");
        opt.value = value;
        opt.text = value;
        loras.appendChild(opt);
    }
    document.body.appendChild(loras);

    for (const id of ["input_sampler", "input_scheduler"]) {
        const select = document.createElement("select");
        select.id = id;
        const opt = document.createElement("option");
        opt.value = id === "input_sampler" ? "euler" : "normal";
        opt.text = opt.value;
        select.appendChild(opt);
        select.value = opt.value;
        document.body.appendChild(select);
    }

    const refinerUpscaleMethod = document.createElement("select");
    refinerUpscaleMethod.id = "input_refinerupscalemethod";
    for (const value of ["pixel-lanczos", "pixel-bicubic"]) {
        const opt = document.createElement("option");
        opt.value = value;
        opt.text = value;
        refinerUpscaleMethod.appendChild(opt);
    }
    document.body.appendChild(refinerUpscaleMethod);

    const vae = document.createElement("select");
    vae.id = "input_vae";
    const vaeOpt = document.createElement("option");
    vaeOpt.value = "Automatic";
    vaeOpt.text = "Automatic";
    vae.appendChild(vaeOpt);
    document.body.appendChild(vae);

    const widthInput = document.createElement("input");
    widthInput.type = "number";
    widthInput.id = "input_aspectratiowidth";
    widthInput.value = "1024";
    document.body.appendChild(widthInput);

    const heightInput = document.createElement("input");
    heightInput.type = "number";
    heightInput.id = "input_aspectratioheight";
    heightInput.value = "768";
    document.body.appendChild(heightInput);

    const fpsInput = document.createElement("input");
    fpsInput.type = "number";
    fpsInput.id = "input_videofps";
    fpsInput.value = "24";
    document.body.appendChild(fpsInput);

    const framesInput = document.createElement("input");
    framesInput.type = "number";
    framesInput.id = "input_videoframes";
    framesInput.value = "48";
    document.body.appendChild(framesInput);
};

const getStagesInput = (): HTMLInputElement =>
    document.getElementById("input_videostages") as HTMLInputElement;

const setImageToVideoWorkflow = (): void => {
    const videoModel = document.getElementById(
        "input_videomodel",
    ) as HTMLSelectElement;
    videoModel.value = "ltx-2.3-22b-dev";
};

const parseStoredConfig = (): ParsedConfig => {
    const parsed = JSON.parse(getStagesInput().value || "[]") as
        | ParsedClip[]
        | ParsedConfig;
    if (Array.isArray(parsed)) {
        return { clips: parsed };
    }
    return {
        width: parsed.width,
        height: parsed.height,
        fps: parsed.fps,
        clips: Array.isArray(parsed.clips) ? parsed.clips : [],
    };
};

const parseStored = (): ParsedClip[] => parseStoredConfig().clips;

describe("videoStageEditor", () => {
    beforeEach(() => {
        jest.useFakeTimers();
        __resetPersistenceForTests();
        setupParameterPanel();
    });

    afterEach(() => {
        jest.useRealTimers();
        document.body.innerHTML = "";
    });

    describe("init / seeding", () => {
        it("does not re-enable a disabled VideoStages group while seeding startup JSON", () => {
            const groupToggle = document.getElementById(
                "input_group_content_videostages_toggle",
            ) as HTMLInputElement;
            groupToggle.checked = false;
            for (const inputId of [
                "input_videostages",
                "input_vswidth",
                "input_vsheight",
            ]) {
                const input = document.getElementById(inputId);
                input?.addEventListener("change", () => {
                    groupToggle.checked = true;
                });
            }

            initEditor();

            const clips = parseStored();
            expect(clips).toHaveLength(1);
            expect(groupToggle.checked).toBe(false);
        });

        it("seeds a single default clip when no JSON is present", () => {
            initEditor();

            const clips = parseStored();
            expect(clips).toHaveLength(1);
            expect(clips[0].stages).toHaveLength(1);
            expect(clips[0].duration).toBe(5);
            expect(clips[0].controlNetSource).toBe("ControlNet 1");
            expect(clips[0].controlNetLora).toBe("");
            expect(clips[0].refs).toEqual([]);
            expect(
                document.querySelector(".vs-clip-card .header-label")
                    ?.textContent,
            ).toBe("Clip 0");
        });

        it("renders and stores per-clip ControlNet source choice", () => {
            initEditor();

            const controlNetSource = document.querySelector(
                '[data-clip-field="controlNetSource"][data-clip-idx="0"]',
            ) as HTMLSelectElement | null;
            const select = must(controlNetSource);
            expect([...select.options].map((option) => option.value)).toEqual([
                "ControlNet 1",
                "ControlNet 2",
                "ControlNet 3",
            ]);

            select.value = "ControlNet 3";
            select.dispatchEvent(new Event("change", { bubbles: true }));

            expect(parseStored()[0].controlNetSource).toBe("ControlNet 3");
        });

        it("renders and stores per-clip ControlNet LoRA choice", () => {
            initEditor();

            const controlNetLora = document.querySelector(
                '[data-clip-field="controlNetLora"][data-clip-idx="0"]',
            ) as HTMLSelectElement | null;
            const select = must(controlNetLora);
            expect([...select.options].map((option) => option.value)).toEqual([
                "",
                "ltx-ic-lora.safetensors",
                "detail-lora.safetensors",
            ]);

            select.value = "ltx-ic-lora.safetensors";
            select.dispatchEvent(new Event("change", { bubbles: true }));

            expect(parseStored()[0].controlNetLora).toBe(
                "ltx-ic-lora.safetensors",
            );
        });

        it("seeds image-to-video defaults with a refiner reference", () => {
            setImageToVideoWorkflow();

            initEditor();

            const clips = parseStored();
            expect(clips[0].refs).toHaveLength(1);
            expect(clips[0].refs?.[0].source).toBe("Refiner");
            expect(clips[0].refs?.[0].frame).toBe(1);
            expect(clips[0].stages?.[0].refStrengths).toEqual([1]);
        });

        it("does not serialize root dimensions from SwarmUI core width and height fields", () => {
            appendCoreDimensionInputs("1344", "832");

            initEditor();

            const config = parseStoredConfig();
            expect(config.width).toBeUndefined();
            expect(config.height).toBeUndefined();
        });

        it("does not serialize registered VideoStages root width and height", () => {
            const registeredWidthInput = document.getElementById(
                "input_vswidth",
            ) as HTMLInputElement;
            registeredWidthInput.value = "1536";

            const registeredHeightInput = document.getElementById(
                "input_vsheight",
            ) as HTMLInputElement;
            registeredHeightInput.value = "864";

            appendCoreDimensionInputs("1344", "832");

            initEditor();

            const config = parseStoredConfig();
            expect(config.width).toBeUndefined();
            expect(config.height).toBeUndefined();
        });

        it("does not serialize registered VideoStages root FPS", () => {
            const registeredFpsInput = document.getElementById(
                "input_vsfps",
            ) as HTMLInputElement;
            registeredFpsInput.value = "32";

            initEditor();

            const config = parseStoredConfig();
            expect(config.fps).toBeUndefined();
        });

        it("seeds the first stage with the frontend default values", () => {
            initEditor();

            const defaultStage = parseStored()[0].stages?.[0];
            expect(defaultStage?.control).toBe(0.5);
            expect(defaultStage?.steps).toBe(8);
            expect(defaultStage?.cfgScale).toBe(1);
            expect(defaultStage?.upscale).toBe(1);
        });

        it("renders an editor div with a clip stack and add-clip button", () => {
            initEditor();

            const editorDiv = document.getElementById(
                "videostages_stage_editor",
            );
            expect(
                editorDiv?.querySelector("[data-vs-clip-stack]"),
            ).not.toBeNull();
            expect(
                editorDiv?.querySelector('[data-clip-action="add-clip"]'),
            ).not.toBeNull();
        });

        it("does not render its own root width/height fields (uses registered SwarmUI sliders)", () => {
            initEditor();

            const widthNumber = document.querySelector(
                '[data-root-field="width"]',
            );
            const heightNumber = document.querySelector(
                '[data-root-field="height"]',
            );
            expect(widthNumber).toBeNull();
            expect(heightNumber).toBeNull();
        });

        it("seeds registered RootWidth/RootHeight sliders from core dimensions when at sentinel", () => {
            appendCoreDimensionInputs("1280", "720");

            initEditor();

            const registeredWidth = document.getElementById(
                "input_vswidth",
            ) as HTMLInputElement;
            const registeredHeight = document.getElementById(
                "input_vsheight",
            ) as HTMLInputElement;
            expect(registeredWidth.value).toBe("1280");
            expect(registeredHeight.value).toBe("720");
        });

        it("does not overwrite registered RootWidth/RootHeight when the user has set them", () => {
            const registeredWidth = document.getElementById(
                "input_vswidth",
            ) as HTMLInputElement;
            registeredWidth.value = "1024";
            const registeredHeight = document.getElementById(
                "input_vsheight",
            ) as HTMLInputElement;
            registeredHeight.value = "768";

            appendCoreDimensionInputs("1280", "720");

            initEditor();

            expect(registeredWidth.value).toBe("1024");
            expect(registeredHeight.value).toBe("768");
        });

        it("preserves existing JSON state", () => {
            getStagesInput().value = JSON.stringify({
                clips: [
                    {
                        duration: 4,
                        audioSource: "Upload",
                        refs: [{ source: "Refiner", frame: 5, fromEnd: true }],
                        stages: [
                            { model: "ltx-2.3-22b-dev", steps: 8, cfgScale: 1 },
                        ],
                    },
                ],
            });

            initEditor();

            const config = parseStoredConfig();
            const clips = parseStored();
            expect(config.width).toBeUndefined();
            expect(config.height).toBeUndefined();
            expect(config.fps).toBeUndefined();
            expect(clips).toHaveLength(1);
            expect(clips[0].audioSource).toBe("Upload");
            expect(clips[0].refs?.[0].source).toBe("Refiner");
            expect(clips[0].refs?.[0].fromEnd).toBe(true);
            expect(clips[0].stages?.[0].steps).toBe(8);
            expect(
                document.querySelector(".vs-clip-card .header-label")
                    ?.textContent,
            ).toBe("Clip 0");
        });

        it("disables stored clip duration when Clip Length from Audio is enabled", () => {
            getStagesInput().value = JSON.stringify({
                clips: [
                    {
                        duration: 4,
                        audioSource: "Upload",
                        clipLengthFromAudio: true,
                        refs: [],
                        stages: [{ model: "ltx-2.3-22b-dev", steps: 8 }],
                    },
                ],
            });

            initEditor();

            const durationNumber = document.querySelector(
                '[data-clip-field="duration"].auto-slider-number',
            ) as HTMLInputElement | null;
            const durationSlider = document.querySelector(
                '[data-clip-field="duration"][type="range"]',
            ) as HTMLInputElement | null;
            expect(parseStored()[0].clipLengthFromAudio).toBe(true);
            expect(durationNumber?.disabled).toBe(true);
            expect(durationSlider?.disabled).toBe(true);
        });

        it("renders stored AceStepFun audio source with a friendly label", () => {
            getStagesInput().value = JSON.stringify({
                clips: [
                    {
                        duration: 4,
                        audioSource: "audio0",
                        refs: [],
                        stages: [{ model: "ltx-2.3-22b-dev", steps: 8 }],
                    },
                ],
            });

            initEditor();

            const audioSource = must(
                document.querySelector(
                    '[data-clip-field="audioSource"][data-clip-idx="0"]',
                ) as HTMLSelectElement | null,
            );
            expect(audioSource.value).toBe("audio0");
            expect(audioSource.selectedOptions[0]?.textContent).toBe(
                "AceStepFun Audio 0",
            );
        });
    });

    describe("clip actions", () => {
        it("adds a new clip when the add-clip button is clicked", async () => {
            initEditor();

            clickDocumentAddClip();
            await flushReRender();

            const clips = parseStored();
            expect(clips).toHaveLength(2);
            expect(
                [
                    ...document.querySelectorAll(".vs-clip-card .header-label"),
                ].map((el) => el.textContent),
            ).toEqual(["Clip 0", "Clip 1"]);
        });

        it("adds new image-to-video clips with a refiner reference", async () => {
            setImageToVideoWorkflow();
            initEditor();

            clickDocumentAddClip();
            await flushReRender();

            const clips = parseStored();
            expect(clips[1].refs).toHaveLength(1);
            expect(clips[1].refs?.[0].source).toBe("Refiner");
            expect(clips[1].stages?.[0].refStrengths).toEqual([1]);
        });

        it("shows Clip 0 header after deleting the first clip when two existed", async () => {
            initEditor();

            clickDocumentAddClip();
            await flushReRender();

            const deleteFirst = document.querySelector(
                '[data-clip-action="delete"][data-clip-idx="0"]',
            ) as HTMLButtonElement | null;
            must(deleteFirst).click();
            await flushReRender();

            const clips = parseStored();
            expect(clips).toHaveLength(1);
            expect(
                document.querySelector(".vs-clip-card .header-label")
                    ?.textContent,
            ).toBe("Clip 0");
        });

        it("allows deleting the only clip", async () => {
            initEditor();

            const deleteBtn = must(
                document.querySelector(
                    '[data-clip-action="delete"][data-clip-idx="0"]',
                ) as HTMLButtonElement | null,
            );
            expect(deleteBtn.disabled).toBe(false);

            deleteBtn.click();
            await flushReRender();

            expect(parseStored()).toHaveLength(0);
            expect(document.querySelector(".vs-clip-card")).toBeNull();
            expect(
                document.querySelector('[data-clip-action="add-clip"]'),
            ).not.toBeNull();
        });

        it("derives Clip 0 header from index even when legacy JSON had a different name field", () => {
            getStagesInput().value = JSON.stringify({
                width: 1024,
                height: 768,
                fps: 24,
                clips: [
                    {
                        name: "Clip 1",
                        duration: 2,
                        audioSource: "Native",
                        refs: [],
                        stages: [{ model: "ltx-2.3-22b-dev", steps: 8 }],
                    },
                ],
            });

            initEditor();

            expect(
                document.querySelector(".vs-clip-card .header-label")
                    ?.textContent,
            ).toBe("Clip 0");
        });

        it("toggles collapse state for a clip when the native shrinkable header is clicked", async () => {
            initEditor();

            const header = must(
                document.querySelector(
                    ".vs-clip-card > .input-group-shrinkable",
                ) as HTMLElement | null,
            );
            header.click();
            await flushReRender();

            const clips = parseStored();
            expect(clips[0].expanded).toBe(false);
        });

        it("does not collapse the clip when the skip action button inside the header is clicked", async () => {
            initEditor();

            clickDocumentAddClip();
            await flushReRender();
            const skipBtn = must(
                document.querySelector(
                    '[data-clip-action="skip"]',
                ) as HTMLButtonElement | null,
            );
            skipBtn.click();
            await flushReRender();

            const clips = parseStored();
            expect(clips[0].expanded).toBe(true);
            expect(clips[0].skipped).toBe(true);
            const refreshedSkipBtn = document.querySelector(
                '[data-clip-action="skip"]',
            ) as HTMLButtonElement | null;
            expect(refreshedSkipBtn?.className).toContain("vs-btn-skip-active");
        });

        it("toggles skip state for a clip when more than one exists", async () => {
            initEditor();

            clickDocumentAddClip();
            await flushReRender();
            const skipBtn = must(
                document.querySelector(
                    '[data-clip-action="skip"]',
                ) as HTMLButtonElement | null,
            );
            skipBtn.click();
            await flushReRender();

            const clips = parseStored();
            expect(clips[0].skipped).toBe(true);
        });

        it("updates clip duration when the slider thumb moves", () => {
            initEditor();

            const durationSlider = document.querySelector(
                '[data-clip-field="duration"][type="range"]',
            ) as HTMLInputElement | null;
            const slider = must(durationSlider);
            slider.value = "6";
            slider.dispatchEvent(new Event("input", { bubbles: true }));

            const clips = parseStored();
            expect(clips[0].duration).toBe(6);
        });

        it("does not rerender the duration slider while dragging", async () => {
            initEditor();

            const durationSlider = document.querySelector(
                '[data-clip-field="duration"][type="range"]',
            ) as HTMLInputElement | null;
            const slider = must(durationSlider);
            const originalSlider = slider;

            slider.value = "6";
            slider.dispatchEvent(new Event("input", { bubbles: true }));
            await flushReRender();

            expect(parseStored()[0].duration).toBe(6);
            expect(originalSlider.isConnected).toBe(true);
            expect(
                document.querySelector(
                    '[data-clip-field="duration"][type="range"]',
                ),
            ).toBe(originalSlider);

            slider.dispatchEvent(new Event("change", { bubbles: true }));
            await flushReRender();

            expect(
                document.querySelector(
                    '[data-clip-field="duration"][type="range"]',
                ),
            ).not.toBe(originalSlider);
        });

        it("does not restore focus to the duration slider after rerender", async () => {
            initEditor();

            const durationSlider = document.querySelector(
                '[data-clip-field="duration"][type="range"]',
            ) as HTMLInputElement | null;
            const slider = must(durationSlider);

            slider.focus();
            slider.value = "6";
            slider.dispatchEvent(new Event("change", { bubbles: true }));
            await flushReRender();

            const refreshedSlider = must(
                document.querySelector(
                    '[data-clip-field="duration"][type="range"]',
                ) as HTMLInputElement | null,
            );
            expect(document.activeElement).not.toBe(refreshedSlider);
        });

        it("does not write registered RootWidth/RootHeight slider changes into saved JSON", () => {
            initEditor();

            const registeredWidth = document.getElementById(
                "input_vswidth",
            ) as HTMLInputElement;
            const registeredHeight = document.getElementById(
                "input_vsheight",
            ) as HTMLInputElement;

            registeredWidth.value = "1536";
            registeredHeight.value = "864";

            const fpsInput = document.getElementById(
                "input_videofps",
            ) as HTMLInputElement;
            fpsInput.dispatchEvent(new Event("change", { bubbles: true }));

            const durationSlider = document.querySelector(
                '[data-clip-field="duration"][type="range"]',
            ) as HTMLInputElement;
            durationSlider.value = "5";
            durationSlider.dispatchEvent(
                new Event("change", { bubbles: true }),
            );

            const config = parseStoredConfig();
            expect(config.width).toBeUndefined();
            expect(config.height).toBeUndefined();
        });

        it("keeps the VideoStages group disabled when a late root FPS change syncs JSON", () => {
            const groupToggle = document.getElementById(
                "input_group_content_videostages_toggle",
            ) as HTMLInputElement;
            groupToggle.checked = false;

            const stagesInput = getStagesInput();
            stagesInput.addEventListener("change", () => {
                groupToggle.checked = true;
            });

            initEditor();

            const fpsInput = document.getElementById(
                "input_vsfps",
            ) as HTMLInputElement;
            fpsInput.value = "32";
            fpsInput.dispatchEvent(new Event("change", { bubbles: true }));

            const config = parseStoredConfig();
            expect(config.fps).toBeUndefined();
            expect(groupToggle.checked).toBe(false);
        });

        it("re-enables the VideoStages group when a user edits clip fields", () => {
            const groupToggle = document.getElementById(
                "input_group_content_videostages_toggle",
            ) as HTMLInputElement;
            groupToggle.checked = false;

            const stagesInput = getStagesInput();
            stagesInput.addEventListener("change", () => {
                groupToggle.checked = true;
            });

            initEditor();

            const audioSource = document.querySelector(
                '[data-clip-field="audioSource"]',
            ) as HTMLSelectElement | null;
            const source = must(audioSource);
            source.value = "Upload";
            source.dispatchEvent(new Event("change", { bubbles: true }));

            expect(parseStored()[0].audioSource).toBe("Upload");
            expect(groupToggle.checked).toBe(true);
        });

        it("stores clip audio source at the clip level", () => {
            initEditor();

            const audioSource = document.querySelector(
                '[data-clip-field="audioSource"]',
            ) as HTMLSelectElement | null;
            const source = must(audioSource);
            source.value = "Upload";
            source.dispatchEvent(new Event("change", { bubbles: true }));

            expect(parseStored()[0].audioSource).toBe("Upload");
        });

        it("stores the Save Audio Track checkbox at the clip level", () => {
            stubAceStepFunRegistry(["audio0"]);
            initEditor();

            const audioSource = document.querySelector(
                '[data-clip-field="audioSource"][data-clip-idx="0"]',
            ) as HTMLSelectElement | null;
            const saveAudioTrack = document.querySelector(
                '[data-clip-field="saveAudioTrack"][data-clip-idx="0"]',
            ) as HTMLInputElement | null;
            const source = must(audioSource);
            source.value = "audio0";
            source.dispatchEvent(new Event("change", { bubbles: true }));
            const checkbox = must(saveAudioTrack);
            expect(checkbox.checked).toBe(false);
            expect(parseStored()[0].saveAudioTrack).toBe(false);

            checkbox.checked = true;
            checkbox.dispatchEvent(new Event("change", { bubbles: true }));

            expect(parseStored()[0].saveAudioTrack).toBe(true);
        });

        it("stores the Reuse Audio checkbox at the clip level", () => {
            initEditor();

            const reuseAudio = document.querySelector(
                '[data-clip-field="reuseAudio"][data-clip-idx="0"]',
            ) as HTMLInputElement | null;
            const checkbox = must(reuseAudio);
            expect(checkbox.checked).toBe(false);
            expect(parseStored()[0].reuseAudio).toBe(false);

            checkbox.checked = true;
            checkbox.dispatchEvent(new Event("change", { bubbles: true }));

            expect(parseStored()[0].reuseAudio).toBe(true);
        });

        it("renders clip audio controls inside an AUDIO section", () => {
            initEditor();

            const audioSection = Array.from(
                document.querySelectorAll(".vs-section-block"),
            ).find(
                (section) =>
                    section.querySelector(".vs-section-block-title")
                        ?.textContent === "AUDIO",
            ) as HTMLElement | undefined;
            const audioSource = audioSection?.querySelector(
                '[data-clip-field="audioSource"][data-clip-idx="0"]',
            ) as HTMLSelectElement | null;
            const uploadField = audioSection?.querySelector(
                ".vs-clip-audio-upload-field",
            ) as HTMLElement | null;
            const saveAudioTrackField = audioSection?.querySelector(
                ".vs-clip-save-audio-track-field",
            ) as HTMLElement | null;
            const clipLengthFromAudioField = audioSection?.querySelector(
                ".vs-clip-length-from-audio-field",
            ) as HTMLElement | null;
            const reuseAudioField = audioSection?.querySelector(
                ".vs-clip-reuse-audio-field",
            ) as HTMLElement | null;
            const uploadInput = audioSection?.querySelector(
                '.auto-file[data-clip-field="uploadedAudio"][data-clip-idx="0"]',
            ) as HTMLInputElement | null;
            const reuseAudio = audioSection?.querySelector(
                '[data-clip-field="reuseAudio"][data-clip-idx="0"]',
            ) as HTMLInputElement | null;
            const clipLengthFromAudio = audioSection?.querySelector(
                '[data-clip-field="clipLengthFromAudio"][data-clip-idx="0"]',
            ) as HTMLInputElement | null;
            const saveAudioTrack = audioSection?.querySelector(
                '[data-clip-field="saveAudioTrack"][data-clip-idx="0"]',
            ) as HTMLInputElement | null;

            expect(audioSection).toBeDefined();
            must(audioSource);
            const elUploadField = must(uploadField);
            const elSaveAudioTrackField = must(saveAudioTrackField);
            const elClipLengthFromAudioField = must(clipLengthFromAudioField);
            must(reuseAudioField);
            must(uploadInput);
            must(reuseAudio);
            must(clipLengthFromAudio);
            const elSaveAudioTrack = must(saveAudioTrack);
            expect(elUploadField.style.display).toBe("none");
            expect(elSaveAudioTrackField.style.display).toBe("none");
            expect(elClipLengthFromAudioField.style.display).toBe("none");

            const saveAudioTrackRow = elSaveAudioTrack.closest(".auto-input");
            const nextAutoInput =
                saveAudioTrackRow?.parentElement?.querySelector(
                    ".vs-clip-audio-upload-field",
                );
            expect(nextAutoInput).toBe(elUploadField);
            expect(saveAudioTrackRow).toBe(elSaveAudioTrackField);
        });

        it("only shows Save Audio Track for AceStepFun audio sources", () => {
            stubAceStepFunRegistry(["audio0"]);
            initEditor();

            const audioSource = document.querySelector(
                '[data-clip-field="audioSource"][data-clip-idx="0"]',
            ) as HTMLSelectElement | null;
            const saveAudioTrackField = document.querySelector(
                ".vs-clip-save-audio-track-field",
            ) as HTMLElement | null;
            const saveAudioTrack = document.querySelector(
                '[data-clip-field="saveAudioTrack"][data-clip-idx="0"]',
            ) as HTMLInputElement | null;
            const saveField = must(saveAudioTrackField);
            const trackCheckbox = must(saveAudioTrack);
            expect(saveField.style.display).toBe("none");

            const src = must(audioSource);
            src.value = "audio0";
            src.dispatchEvent(new Event("change", { bubbles: true }));
            expect(src.selectedOptions[0]?.textContent).toBe(
                "AceStepFun Audio 0",
            );
            expect(saveField.style.display).toBe("");

            trackCheckbox.checked = true;
            trackCheckbox.dispatchEvent(new Event("change", { bubbles: true }));
            expect(parseStored()[0].saveAudioTrack).toBe(true);

            src.value = "Upload";
            src.dispatchEvent(new Event("change", { bubbles: true }));
            expect(saveField.style.display).toBe("none");
            expect(trackCheckbox.checked).toBe(false);
            expect(parseStored()[0].saveAudioTrack).toBe(false);
        });

        it("renders checkbox tooltip buttons on the left and leaves Audio Upload as core renders it", () => {
            stubAceStepFunRegistry(["audio0"]);
            initEditor();

            const saveAudioTrackField = document.querySelector(
                ".vs-clip-save-audio-track-field .auto-input-name",
            );
            const clipLengthFromAudioField = document.querySelector(
                ".vs-clip-length-from-audio-field .auto-input-name",
            );
            const reuseAudioField = document.querySelector(
                ".vs-clip-reuse-audio-field .auto-input-name",
            );
            const audioUploadField = document.querySelector(
                ".vs-clip-audio-upload-field .auto-input-name",
            );
            const nameSaveAudioTrack = must(saveAudioTrackField);
            const nameClipLengthFromAudio = must(clipLengthFromAudioField);
            const nameReuseAudio = must(reuseAudioField);
            const nameAudioUpload = must(audioUploadField);

            expect(
                nameSaveAudioTrack.firstElementChild?.classList.contains(
                    "auto-input-qbutton",
                ),
            ).toBe(true);
            expect(
                nameClipLengthFromAudio.firstElementChild?.classList.contains(
                    "auto-input-qbutton",
                ),
            ).toBe(true);
            expect(
                nameReuseAudio.firstElementChild?.classList.contains(
                    "auto-input-qbutton",
                ),
            ).toBe(true);
            expect(
                nameAudioUpload.lastElementChild?.classList.contains(
                    "auto-input-qbutton",
                ),
            ).toBe(true);
            expect(nameSaveAudioTrack.textContent).toBe("?Save Audio Track");
            expect(nameClipLengthFromAudio.textContent).toBe(
                "?Clip Length from Audio",
            );
            expect(nameReuseAudio.textContent).toBe("?Reuse Audio");
            expect(nameAudioUpload.textContent).toBe("Audio Upload?");

            const audioUploadPopover = document.getElementById(
                "popover_vsclip0_uploadedAudio",
            );
            expect(audioUploadPopover).toBeNull();
        });

        it("shows Clip Length from Audio for upload and AceStepFun sources", () => {
            stubAceStepFunRegistry(["audio0"]);
            initEditor();

            const audioSource = document.querySelector(
                '[data-clip-field="audioSource"][data-clip-idx="0"]',
            ) as HTMLSelectElement | null;
            const lengthFromAudioField = document.querySelector(
                ".vs-clip-length-from-audio-field",
            ) as HTMLElement | null;
            const lengthFromAudio = document.querySelector(
                '[data-clip-field="clipLengthFromAudio"][data-clip-idx="0"]',
            ) as HTMLInputElement | null;
            const durationNumber = document.querySelector(
                '[data-clip-field="duration"].auto-slider-number',
            ) as HTMLInputElement | null;
            const durationSlider = document.querySelector(
                '[data-clip-field="duration"][type="range"]',
            ) as HTMLInputElement | null;
            const lengthField = must(lengthFromAudioField);
            expect(lengthField.style.display).toBe("none");

            const src = must(audioSource);
            src.value = "Upload";
            src.dispatchEvent(new Event("change", { bubbles: true }));
            expect(lengthField.style.display).toBe("");

            const checkbox = must(lengthFromAudio);
            checkbox.checked = true;
            checkbox.dispatchEvent(new Event("change", { bubbles: true }));
            expect(parseStored()[0].clipLengthFromAudio).toBe(true);
            expect(durationNumber?.disabled).toBe(true);
            expect(durationSlider?.disabled).toBe(true);

            src.value = "audio0";
            src.dispatchEvent(new Event("change", { bubbles: true }));
            expect(lengthField.style.display).toBe("");
            expect(parseStored()[0].clipLengthFromAudio).toBe(true);

            src.value = "Native";
            src.dispatchEvent(new Event("change", { bubbles: true }));
            expect(lengthField.style.display).toBe("none");
            expect(checkbox.checked).toBe(false);
            expect(parseStored()[0].clipLengthFromAudio).toBe(false);
            expect(durationNumber?.disabled).toBe(false);
            expect(durationSlider?.disabled).toBe(false);
        });

        it("reveals the per-clip audio upload field when audioSource changes to Upload", () => {
            initEditor();

            const audioSource = document.querySelector(
                '[data-clip-field="audioSource"][data-clip-idx="0"]',
            ) as HTMLSelectElement | null;
            const uploadField = document.querySelector(
                ".vs-clip-audio-upload-field",
            ) as HTMLElement | null;
            must(audioSource);
            const upload = must(uploadField);
            expect(upload.style.display).toBe("none");

            const src = must(audioSource);
            src.value = "Upload";
            src.dispatchEvent(new Event("change", { bubbles: true }));

            expect(upload.style.display).toBe("");
        });

        it("still reveals clip audio Upload when the editor DOM is rebuilt", () => {
            initEditor();

            const originalEditor = document.getElementById(
                "videostages_stage_editor",
            ) as HTMLElement | null;
            const root = must(originalEditor);
            const parent = must(root.parentElement);

            const rebuiltEditor = root.cloneNode(true) as HTMLElement;
            parent.replaceChild(rebuiltEditor, root);

            const audioSource = rebuiltEditor.querySelector(
                '[data-clip-field="audioSource"][data-clip-idx="0"]',
            ) as HTMLSelectElement | null;
            const uploadField = rebuiltEditor.querySelector(
                ".vs-clip-audio-upload-field",
            ) as HTMLElement | null;
            const uploadInput = rebuiltEditor.querySelector(
                '.auto-file[data-clip-field="uploadedAudio"][data-clip-idx="0"]',
            ) as HTMLInputElement | null;
            const rebuiltUploadField = must(uploadField);
            const rebuiltUploadInput = must(uploadInput);
            expect(rebuiltUploadInput.type).toBe("file");
            expect(rebuiltUploadField.style.display).toBe("none");

            const clipSource = must(audioSource);
            clipSource.value = "Upload";
            clipSource.dispatchEvent(new Event("change", { bubbles: true }));

            expect(parseStored()[0].audioSource).toBe("Upload");
            expect(rebuiltUploadField.style.display).toBe("");
        });

        it("stores uploaded audio payload on the clip", () => {
            initEditor();

            const audioSource = document.querySelector(
                '[data-clip-field="audioSource"][data-clip-idx="0"]',
            ) as HTMLSelectElement | null;
            const clipAudioSource = must(audioSource);
            clipAudioSource.value = "Upload";
            clipAudioSource.dispatchEvent(
                new Event("change", { bubbles: true }),
            );

            const uploadInput = document.querySelector(
                '.auto-file[data-clip-field="uploadedAudio"][data-clip-idx="0"]',
            ) as HTMLInputElement | null;
            const fileInput = must(uploadInput);

            fileInput.onchange = null;
            fileInput.dataset.filedata = "data:audio/wav;base64,QUJD";
            fileInput.dataset.filename = "clip.wav";
            fileInput.dispatchEvent(new Event("change", { bubbles: true }));

            const clips = parseStored();
            expect(clips[0].uploadedAudio?.data).toBe(
                "data:audio/wav;base64,QUJD",
            );
            expect(clips[0].uploadedAudio?.fileName).toBe("clip.wav");

            const rawConfig = JSON.parse(getStagesInput().value) as Record<
                string,
                unknown
            >;
            expect(rawConfig.uploadedAudio).toBeUndefined();
        });

        it("stores audio selected from the input browser after SwarmUI updates file data", async () => {
            initEditor();

            const audioSource = document.querySelector(
                '[data-clip-field="audioSource"][data-clip-idx="0"]',
            ) as HTMLSelectElement | null;
            const clipAudioSource = must(audioSource);
            clipAudioSource.value = "Upload";
            clipAudioSource.dispatchEvent(
                new Event("change", { bubbles: true }),
            );

            const uploadInput = document.querySelector(
                '.auto-file[data-clip-field="uploadedAudio"][data-clip-idx="0"]',
            ) as HTMLInputElement | null;
            const fileInput = must(uploadInput);

            fileInput.dataset.filename = "inputs/clip.wav";
            fileInput.dataset.filedata = "inputs/clip.wav";
            await Promise.resolve();

            const clips = parseStored();
            expect(clips[0].uploadedAudio?.data).toBe("inputs/clip.wav");
            expect(clips[0].uploadedAudio?.fileName).toBe("clip.wav");
        });

        it("stores uploaded audio payload when the browser provides a File", async () => {
            const originalFileReader = globalThis.FileReader;
            class ImmediateFileReader {
                result: string | ArrayBuffer | null = null;
                private loadListener: EventListenerOrEventListenerObject | null =
                    null;

                addEventListener(
                    type: string,
                    listener: EventListenerOrEventListenerObject,
                ): void {
                    if (type === "load") {
                        this.loadListener = listener;
                    }
                }

                readAsDataURL(): void {
                    this.result = "data:audio/mpeg;base64,QUJD";
                    const event = new ProgressEvent("load");
                    if (typeof this.loadListener === "function") {
                        this.loadListener.call(this, event);
                    } else {
                        this.loadListener?.handleEvent(event);
                    }
                }
            }
            Object.defineProperty(globalThis, "FileReader", {
                value: ImmediateFileReader,
                configurable: true,
            });
            try {
                initEditor();

                const audioSource = document.querySelector(
                    '[data-clip-field="audioSource"][data-clip-idx="0"]',
                ) as HTMLSelectElement | null;
                const clipAudioSource = must(audioSource);
                clipAudioSource.value = "Upload";
                clipAudioSource.dispatchEvent(
                    new Event("change", { bubbles: true }),
                );

                const uploadInput = document.querySelector(
                    '.auto-file[data-clip-field="uploadedAudio"][data-clip-idx="0"]',
                ) as HTMLInputElement | null;
                const fileInput = must(uploadInput);
                const file = new File(["ABC"], "clip.mp3", {
                    type: "audio/mpeg",
                });
                Object.defineProperty(fileInput, "files", {
                    value: [file],
                    configurable: true,
                });
                fileInput.dispatchEvent(new Event("change", { bubbles: true }));

                const clips = parseStored();
                expect(clips[0].uploadedAudio?.data).toBe(
                    "data:audio/mpeg;base64,QUJD",
                );
                expect(clips[0].uploadedAudio?.fileName).toBe("clip.mp3");
            } finally {
                Object.defineProperty(globalThis, "FileReader", {
                    value: originalFileReader,
                    configurable: true,
                });
            }
        });

        it("keeps per-clip uploads independent across multiple Upload clips", async () => {
            initEditor();

            clickDocumentAddClip();
            await flushReRender();

            const audioSources = document.querySelectorAll(
                '[data-clip-field="audioSource"]',
            ) as NodeListOf<HTMLSelectElement>;
            expect(audioSources).toHaveLength(2);
            for (const select of audioSources) {
                select.value = "Upload";
                select.dispatchEvent(new Event("change", { bubbles: true }));
            }

            const uploadInputs = document.querySelectorAll(
                '.auto-file[data-clip-field="uploadedAudio"]',
            ) as NodeListOf<HTMLInputElement>;
            expect(uploadInputs).toHaveLength(2);

            uploadInputs[0].onchange = null;
            uploadInputs[0].dataset.filedata = "data:audio/wav;base64,FIRST";
            uploadInputs[0].dataset.filename = "first.wav";
            uploadInputs[0].dispatchEvent(
                new Event("change", { bubbles: true }),
            );

            uploadInputs[1].onchange = null;
            uploadInputs[1].dataset.filedata = "data:audio/wav;base64,SECOND";
            uploadInputs[1].dataset.filename = "second.wav";
            uploadInputs[1].dispatchEvent(
                new Event("change", { bubbles: true }),
            );

            const clips = parseStored();
            expect(clips[0].uploadedAudio?.data).toBe(
                "data:audio/wav;base64,FIRST",
            );
            expect(clips[0].uploadedAudio?.fileName).toBe("first.wav");
            expect(clips[1].uploadedAudio?.data).toBe(
                "data:audio/wav;base64,SECOND",
            );
            expect(clips[1].uploadedAudio?.fileName).toBe("second.wav");
        });

        it("does not rerender the duration number input while typing", async () => {
            initEditor();

            const durationNumber = document.querySelector(
                '[data-clip-field="duration"].auto-slider-number',
            ) as HTMLInputElement | null;
            const durationEl = must(durationNumber);
            const originalDurationNumber = durationEl;
            durationEl.value = "15";
            durationEl.dispatchEvent(new Event("input", { bubbles: true }));
            await flushReRender();

            expect(parseStored()[0].duration).toBe(15);
            expect(originalDurationNumber.isConnected).toBe(true);
            expect(
                document.querySelector(
                    '[data-clip-field="duration"].auto-slider-number',
                ),
            ).toBe(originalDurationNumber);

            durationEl.dispatchEvent(new Event("change", { bubbles: true }));
            await flushReRender();

            expect(
                document.querySelector(
                    '[data-clip-field="duration"].auto-slider-number',
                ),
            ).not.toBe(originalDurationNumber);
        });

        it("uses 0.5 second jumps for the duration slider but leaves manual entry unrestricted", () => {
            initEditor();

            const durationNumber = document.querySelector(
                '[data-clip-field="duration"].auto-slider-number',
            ) as HTMLInputElement | null;
            const durationSlider = document.querySelector(
                '[data-clip-field="duration"][type="range"]',
            ) as HTMLInputElement | null;

            expect(durationNumber?.type).toBe("number");
            expect(durationNumber?.step).toBe("any");
            expect(durationSlider?.step).toBe("0.5");
        });
    });

    describe("ref actions", () => {
        it("adds a new ref to a clip", async () => {
            initEditor();

            must(
                document.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement | null,
            ).click();
            await flushReRender();

            const clips = parseStored();
            expect(clips[0].refs).toHaveLength(1);
            expect(clips[0].refs?.[0].source).toBe("Refiner");
        });

        it("adds a ref when the click bubbles from button text", async () => {
            initEditor();

            const addRefBtn = must(
                document.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement | null,
            );

            const addRefLabel = must(addRefBtn.firstChild);
            expect(addRefLabel.nodeType).toBe(Node.TEXT_NODE);

            must(addRefLabel).dispatchEvent(
                new MouseEvent("click", { bubbles: true, cancelable: true }),
            );
            await flushReRender();

            expect(parseStored()[0].refs).toHaveLength(1);
        });

        it("adds a ref after the editor root was replaced (clone does not copy listeners)", async () => {
            initEditor();

            const originalEditor = document.getElementById(
                "videostages_stage_editor",
            ) as HTMLElement | null;
            const root = must(originalEditor);
            const parent = must(root.parentElement);
            const rebuiltEditor = root.cloneNode(true) as HTMLElement;
            parent.replaceChild(rebuiltEditor, root);

            must(
                rebuiltEditor.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement | null,
            ).click();
            await flushReRender();

            expect(parseStored()[0].refs).toHaveLength(1);
        });

        it("adds a ref from the clicked editor when duplicate editor ids exist during a rebuild", async () => {
            initEditor();

            const originalEditor = document.getElementById(
                "videostages_stage_editor",
            ) as HTMLElement | null;

            const rebuiltEditor = must(originalEditor).cloneNode(
                true,
            ) as HTMLElement;
            must(originalEditor.parentElement).appendChild(rebuiltEditor);

            const addRefBtn = rebuiltEditor.querySelector(
                '[data-clip-action="add-ref"]',
            ) as HTMLButtonElement | null;

            must(addRefBtn).click();
            await flushReRender();

            expect(parseStored()[0].refs).toHaveLength(1);
            expect(rebuiltEditor.querySelector(".vs-ref-card")).not.toBeNull();
        });

        it("adds a ref when the persisted input is temporarily empty", async () => {
            initEditor();

            getStagesInput().value = "";

            const addRefBtn = document.querySelector(
                '[data-clip-action="add-ref"]',
            ) as HTMLButtonElement | null;

            must(addRefBtn).click();
            await flushReRender();

            expect(parseStored()[0].refs).toHaveLength(1);
        });

        it("uses the updated reverse frame count label", async () => {
            initEditor();

            (
                document.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const refCard = document.querySelector(
                ".vs-ref-card",
            ) as HTMLElement | null;
            expect(refCard?.textContent).toContain("Count in reverse from end");
            expect(refCard?.textContent).not.toContain("Count from last frame");
        });

        it("adds a default ref strength slider for each stage when a ref is added", async () => {
            initEditor();

            (
                document.querySelector(
                    '[data-clip-action="add-stage"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            (
                document.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const clips = parseStored();
            expect(clips[0].refs).toHaveLength(1);
            expect(clips[0].stages?.[0].refStrengths).toEqual([0.8]);
            expect(clips[0].stages?.[1].refStrengths).toEqual([0.8]);
            expect(
                document.querySelectorAll(
                    '[data-stage-field="refStrength_0"][type="range"]',
                ),
            ).toHaveLength(2);
        });

        it("includes Base2Edit refs in the source dropdown when registered", async () => {
            stubBase2EditStageRegistry(["edit0", "edit1"]);
            initEditor();

            (
                document.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();
            const sourceSelect = document.querySelector(
                '[data-ref-field="source"]',
            ) as HTMLSelectElement;
            const values = Array.from(sourceSelect.options).map(
                (opt) => opt.value,
            );
            expect(values).toContain("edit0");
            expect(values).toContain("edit1");
        });

        it("renders reference headers as Ref Image n labels", async () => {
            initEditor();

            (
                document.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const refTitle = document.querySelector(
                ".vs-ref-card .vs-card-title",
            ) as HTMLElement | null;
            const refHead = document.querySelector(
                ".vs-ref-card .vs-card-head",
            ) as HTMLElement | null;
            const refCollapseButton = document.querySelector(
                '[data-ref-action="toggle-collapse"]',
            ) as HTMLButtonElement | null;
            expect(refTitle?.textContent?.trim()).toBe("Ref Image 0");
            expect(refHead?.firstElementChild).toBe(refCollapseButton);
            expect(refCollapseButton?.textContent?.trim()).toBe(
                OPEN_GROUP_GLYPH,
            );
        });

        it("reveals the upload field when the reference source changes to Upload", async () => {
            initEditor();

            (
                document.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const sourceSelect = document.querySelector(
                '[data-ref-field="source"]',
            ) as HTMLSelectElement | null;
            const uploadField = document.querySelector(
                ".vs-ref-upload-field",
            ) as HTMLElement | null;
            const uploadInput = document.querySelector(
                '.vs-ref-upload-field .auto-file[data-ref-field="uploadFileName"]',
            ) as HTMLInputElement | null;
            const refUploadField = must(uploadField);
            expect(refUploadField.style.display).toBe("none");

            const refSource = must(sourceSelect);
            refSource.value = "Upload";
            refSource.dispatchEvent(new Event("change", { bubbles: true }));
            expect(refUploadField.style.display).toBe("");
            expect(must(uploadInput).type).toBe("file");

            const refreshedUploadField = must(
                document.querySelector(
                    ".vs-ref-upload-field",
                ) as HTMLElement | null,
            );
            expect(parseStored()[0].refs?.[0].source).toBe("Upload");
            expect(refreshedUploadField.style.display).toBe("");
        });

        it("stores ref upload image payload when Swarm provides filedata on the file input", async () => {
            initEditor();

            (
                document.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const sourceSelect = document.querySelector(
                '[data-ref-field="source"]',
            ) as HTMLSelectElement | null;
            const refSrc = must(sourceSelect);
            refSrc.value = "Upload";
            refSrc.dispatchEvent(new Event("change", { bubbles: true }));

            const uploadInput = document.querySelector(
                '.vs-ref-upload-field .auto-file[data-ref-field="uploadFileName"]',
            ) as HTMLInputElement | null;
            const refFileInput = must(uploadInput);

            refFileInput.onchange = null;
            refFileInput.dataset.filedata = "data:image/png;base64,QUJD";
            refFileInput.dataset.filename = "ref.png";
            refFileInput.dispatchEvent(new Event("change", { bubbles: true }));

            const clips = parseStored();
            expect(clips[0].refs?.[0].uploadFileName).toBe("ref.png");
            expect(clips[0].refs?.[0].uploadedImage?.data).toBe(
                "data:image/png;base64,QUJD",
            );
            expect(clips[0].refs?.[0].uploadedImage?.fileName).toBe("ref.png");
        });

        it("still reveals Upload when the editor DOM is rebuilt", async () => {
            initEditor();

            (
                document.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const originalEditor = document.getElementById(
                "videostages_stage_editor",
            ) as HTMLElement | null;
            const refRoot = must(originalEditor);
            const refParent = must(refRoot.parentElement);

            const rebuiltEditor = refRoot.cloneNode(true) as HTMLElement;
            refParent.replaceChild(rebuiltEditor, refRoot);

            const sourceSelect = rebuiltEditor.querySelector(
                '[data-ref-field="source"]',
            ) as HTMLSelectElement | null;
            const uploadField = rebuiltEditor.querySelector(
                ".vs-ref-upload-field",
            ) as HTMLElement | null;
            const rebuiltUploadField = must(uploadField);
            expect(rebuiltUploadField.style.display).toBe("none");

            const rebuiltSource = must(sourceSelect);
            rebuiltSource.value = "Upload";
            rebuiltSource.dispatchEvent(new Event("change", { bubbles: true }));

            expect(parseStored()[0].refs?.[0].source).toBe("Upload");
            expect(rebuiltUploadField.style.display).toBe("");
        });

        it("removes the matching ref strength from each stage when a ref is deleted", async () => {
            getStagesInput().value = JSON.stringify([
                {
                    duration: 4,
                    width: 800,
                    height: 600,
                    refs: [
                        { source: "Base", frame: 1 },
                        { source: "Refiner", frame: 5 },
                    ],
                    stages: [
                        {
                            model: "ltx-2.3-22b-dev",
                            steps: 8,
                            cfgScale: 1,
                            refStrengths: [0.3, 0.7],
                        },
                        {
                            model: "ltx-2.3-22b-dev",
                            steps: 12,
                            cfgScale: 1,
                            refStrengths: [0.4, 0.9],
                        },
                    ],
                },
            ]);

            initEditor();

            const deleteBtn = document.querySelector(
                '[data-ref-action="delete"][data-ref-idx="0"]',
            ) as HTMLButtonElement | null;
            must(deleteBtn).click();
            await flushReRender();

            const clips = parseStored();
            expect(clips[0].refs).toHaveLength(1);
            expect(clips[0].refs?.[0].source).toBe("Refiner");
            expect(clips[0].stages?.[0].refStrengths).toEqual([0.7]);
            expect(clips[0].stages?.[1].refStrengths).toEqual([0.9]);
            expect(
                document.querySelector('[data-stage-field="refStrength_1"]'),
            ).toBeNull();
        });

        it("uses the aligned clip duration frame count for the reference frame slider max", async () => {
            initEditor();

            (
                document.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const frameNumber = document.querySelector(
                '[data-ref-field="frame"][type="number"]',
            ) as HTMLInputElement | null;
            const frameRange = document.querySelector(
                '[data-ref-field="frame"][type="range"]',
            ) as HTMLInputElement | null;
            expect(frameNumber?.max).toBe("121");
            expect(frameRange?.max).toBe("121");
            expect(
                frameNumber
                    ?.closest(".auto-slider-box")
                    ?.querySelector(".auto-input-name")?.textContent,
            ).toBe("Frame");

            const durationNumber = document.querySelector(
                '[data-clip-field="duration"].auto-slider-number',
            ) as HTMLInputElement | null;
            const clipDuration = must(durationNumber);
            clipDuration.value = "21.5";
            clipDuration.dispatchEvent(new Event("change", { bubbles: true }));
            await flushReRender();

            const refreshedFrameNumber = document.querySelector(
                '[data-ref-field="frame"][type="number"]',
            ) as HTMLInputElement | null;
            const refreshedFrameRange = document.querySelector(
                '[data-ref-field="frame"][type="range"]',
            ) as HTMLInputElement | null;
            expect(refreshedFrameNumber?.max).toBe("521");
            expect(refreshedFrameRange?.max).toBe("521");
        });

        it("refreshes dependent fields when duration changes commit on focusout", async () => {
            initEditor();

            (
                document.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const durationNumber = document.querySelector(
                '[data-clip-field="duration"].auto-slider-number',
            ) as HTMLInputElement | null;
            const durationEl = must(durationNumber);

            durationEl.value = "21.5";
            durationEl.dispatchEvent(new Event("input", { bubbles: true }));
            durationEl.dispatchEvent(
                new FocusEvent("focusout", { bubbles: true }),
            );
            await flushReRender();

            const refreshedFrameNumber = document.querySelector(
                '[data-ref-field="frame"][type="number"]',
            ) as HTMLInputElement | null;
            const refreshedFrameRange = document.querySelector(
                '[data-ref-field="frame"][type="range"]',
            ) as HTMLInputElement | null;
            expect(refreshedFrameNumber?.max).toBe("521");
            expect(refreshedFrameRange?.max).toBe("521");
        });
    });

    describe("stage actions", () => {
        it("adds a new stage to a clip", async () => {
            initEditor();

            const addStageBtn = document.querySelector(
                '[data-clip-action="add-stage"]',
            ) as HTMLButtonElement;
            addStageBtn.click();
            await flushReRender();

            const clips = parseStored();
            expect(clips[0].stages).toHaveLength(2);
        });

        it("initializes new stages with default ref strengths for existing refs", async () => {
            initEditor();

            (
                document.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement
            ).click();
            (
                document.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            (
                document.querySelector(
                    '[data-clip-action="add-stage"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const clips = parseStored();
            expect(clips[0].stages?.[1].refStrengths).toEqual([0.8, 0.8]);
            expect(
                document.querySelector(
                    '[data-stage-field="refStrength_0"][data-stage-idx="1"]',
                ),
            ).not.toBeNull();
            expect(
                document.querySelector(
                    '[data-stage-field="refStrength_1"][data-stage-idx="1"]',
                ),
            ).not.toBeNull();
        });

        it("renders steps and cfg scale; hides stage 0 control and upscale fields from layout", async () => {
            initEditor();

            const controlSlider = document.querySelector(
                '[data-stage-field="control"][type="range"][data-stage-idx="0"]',
            ) as HTMLInputElement | null;
            const controlNumber = document.querySelector(
                '[data-stage-field="control"][type="number"][data-stage-idx="0"]',
            ) as HTMLInputElement | null;
            const stepsSlider = document.querySelector(
                '[data-stage-field="steps"][type="range"]',
            ) as HTMLInputElement | null;
            const cfgScaleSlider = document.querySelector(
                '[data-stage-field="cfgScale"][type="range"]',
            ) as HTMLInputElement | null;
            const upscale0 = document.querySelector(
                '[data-stage-field="upscale"][type="range"][data-stage-idx="0"]',
            ) as HTMLInputElement | null;

            const s0ControlSlider = must(controlSlider);
            const s0ControlNumber = must(controlNumber);
            const s0StepsSlider = must(stepsSlider);
            const s0CfgScaleSlider = must(cfgScaleSlider);
            const s0Upscale0 = must(upscale0);
            expect(s0ControlSlider.disabled).toBe(false);
            expect(s0ControlNumber.disabled).toBe(false);
            expect(s0ControlSlider.value).toBe("0.5");
            expect(s0ControlNumber.value).toBe("0.5");
            expect(s0Upscale0.disabled).toBe(false);
            expect(
                s0ControlSlider
                    .closest(".vs-first-stage-field-hidden")
                    ?.getAttribute("style"),
            ).toBe("display: none;");
            expect(
                s0Upscale0
                    .closest(".vs-first-stage-field-hidden")
                    ?.getAttribute("style"),
            ).toBe("display: none;");
            expect(s0ControlSlider.min).toBe("0.05");
            expect(s0StepsSlider.max).toBe("50");
            expect(s0CfgScaleSlider.max).toBe("10");
            expect(s0Upscale0.max).toBe("4");
            const upscale0n = document.querySelector(
                '[data-stage-field="upscale"][type="number"][data-stage-idx="0"]',
            ) as HTMLInputElement | null;
            expect(must(upscale0n).disabled).toBe(false);

            (
                document.querySelector(
                    '[data-clip-action="add-stage"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const upscale1 = document.querySelector(
                '[data-stage-field="upscale"][type="range"][data-stage-idx="1"]',
            ) as HTMLInputElement | null;
            expect(upscale1?.disabled).toBe(false);
        });

        it("renders stage headers as Stage n labels with zero-based indexes", async () => {
            initEditor();

            const firstHead = document.querySelector(
                '.vs-card[data-stage-idx="0"] .vs-card-head',
            ) as HTMLElement | null;
            const firstCollapseButton = document.querySelector(
                '[data-stage-action="toggle-collapse"][data-stage-idx="0"]',
            ) as HTMLButtonElement | null;
            const firstTitle = document.querySelector(
                '.vs-card[data-stage-idx="0"] .vs-card-title',
            ) as HTMLElement | null;
            expect(firstHead?.firstElementChild).toBe(firstCollapseButton);
            expect(firstCollapseButton?.textContent?.trim()).toBe(
                OPEN_GROUP_GLYPH,
            );
            expect(firstTitle?.textContent?.trim()).toBe("Stage 0");

            (
                document.querySelector(
                    '[data-clip-action="add-stage"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const secondTitle = document.querySelector(
                '.vs-card[data-stage-idx="1"] .vs-card-title',
            ) as HTMLElement | null;
            const secondCollapseButton = document.querySelector(
                '[data-stage-action="toggle-collapse"][data-stage-idx="1"]',
            ) as HTMLButtonElement | null;
            expect(secondTitle?.textContent?.trim()).toBe("Stage 1");
            expect(secondCollapseButton?.textContent?.trim()).toBe(
                OPEN_GROUP_GLYPH,
            );
            expect(
                document.querySelector(
                    '.vs-card[data-stage-idx="0"] .vs-card-summary',
                ),
            ).toBeNull();
        });

        it("hides first-stage control fields; stage 1 upscale drives method enablement", async () => {
            initEditor();

            const s0Method = document.querySelector(
                '[data-stage-field="upscaleMethod"][data-stage-idx="0"]',
            ) as HTMLSelectElement | null;
            const s0ControlRange = document.querySelector(
                '[data-stage-field="control"][type="range"][data-stage-idx="0"]',
            ) as HTMLInputElement | null;
            const s0ControlNumber = document.querySelector(
                '[data-stage-field="control"][type="number"][data-stage-idx="0"]',
            ) as HTMLInputElement | null;
            const s0Range = document.querySelector(
                '[data-stage-field="upscale"][type="range"][data-stage-idx="0"]',
            ) as HTMLInputElement | null;
            const s0Number = document.querySelector(
                '[data-stage-field="upscale"][type="number"][data-stage-idx="0"]',
            ) as HTMLInputElement | null;
            expect(s0Method?.disabled).toBe(false);
            expect(s0ControlRange?.disabled).toBe(false);
            expect(s0ControlNumber?.disabled).toBe(false);
            expect(s0Range?.disabled).toBe(false);
            expect(s0Number?.disabled).toBe(false);
            expect(
                s0Method
                    ?.closest(".vs-first-stage-field-hidden")
                    ?.getAttribute("style"),
            ).toBe("display: none;");
            expect(
                s0ControlRange
                    ?.closest(".vs-first-stage-field-hidden")
                    ?.getAttribute("style"),
            ).toBe("display: none;");
            expect(
                s0Range
                    ?.closest(".vs-first-stage-field-hidden")
                    ?.getAttribute("style"),
            ).toBe("display: none;");
            expect(parseStored()[0].stages?.[0].control).toBe(0.5);
            expect(parseStored()[0].stages?.[0].upscale).toBe(1);

            (
                document.querySelector(
                    '[data-clip-action="add-stage"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const s1Method = document.querySelector(
                '[data-stage-field="upscaleMethod"][data-stage-idx="1"]',
            ) as HTMLSelectElement | null;
            const s1ControlRange = document.querySelector(
                '[data-stage-field="control"][type="range"][data-stage-idx="1"]',
            ) as HTMLInputElement | null;
            const s1Range = document.querySelector(
                '[data-stage-field="upscale"][type="range"][data-stage-idx="1"]',
            ) as HTMLInputElement | null;
            const stage1Upscale = must(s1Range);
            expect(s1ControlRange?.disabled).toBe(false);
            expect(stage1Upscale.disabled).toBe(false);
            expect(s1Method?.disabled).toBe(true);
            stage1Upscale.value = "1.25";
            stage1Upscale.dispatchEvent(new Event("input", { bubbles: true }));
            expect(parseStored()[0].stages?.[1].upscale).toBe(1.25);
            expect(s1Method?.disabled).toBe(false);
        });

        it("keeps stage upscale method when upscale slider changes", async () => {
            initEditor();

            (
                document.querySelector(
                    '[data-clip-action="add-stage"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const s1Range = document.querySelector(
                '[data-stage-field="upscale"][type="range"][data-stage-idx="1"]',
            ) as HTMLInputElement | null;
            const s1Method = document.querySelector(
                '[data-stage-field="upscaleMethod"][data-stage-idx="1"]',
            ) as HTMLSelectElement | null;
            const range1 = must(s1Range);
            const method1 = must(s1Method);

            range1.value = "1.25";
            range1.dispatchEvent(new Event("input", { bubbles: true }));
            await flushReRender();

            method1.value = "pixel-bicubic";
            method1.dispatchEvent(new Event("change", { bubbles: true }));
            await flushReRender();

            expect(parseStored()[0].stages?.[1].upscaleMethod).toBe(
                "pixel-bicubic",
            );

            range1.value = "1.5";
            range1.dispatchEvent(new Event("input", { bubbles: true }));
            range1.dispatchEvent(new Event("change", { bubbles: true }));
            await flushReRender();

            const stored = parseStored()[0].stages?.[1];
            expect(stored?.upscale).toBe(1.5);
            expect(stored?.upscaleMethod).toBe("pixel-bicubic");

            range1.value = "1";
            range1.dispatchEvent(new Event("input", { bubbles: true }));
            range1.dispatchEvent(new Event("change", { bubbles: true }));
            await flushReRender();

            expect(method1.disabled).toBe(true);
            expect(method1.value).toBe("pixel-bicubic");
            expect(parseStored()[0].stages?.[1].upscaleMethod).toBe(
                "pixel-bicubic",
            );
        });

        it("updates stored stage upscale method from non-bubbling dropdown changes", async () => {
            initEditor();

            (
                document.querySelector(
                    '[data-clip-action="add-stage"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const s1Range = document.querySelector(
                '[data-stage-field="upscale"][type="range"][data-stage-idx="1"]',
            ) as HTMLInputElement | null;
            const s1Method = document.querySelector(
                '[data-stage-field="upscaleMethod"][data-stage-idx="1"]',
            ) as HTMLSelectElement | null;

            const range1 = must(s1Range);
            range1.value = "1.5";
            range1.dispatchEvent(new Event("input", { bubbles: true }));
            await flushReRender();

            const method1 = must(s1Method);
            method1.value = "pixel-bicubic";
            method1.dispatchEvent(new Event("change"));

            expect(parseStored()[0].stages?.[1].upscaleMethod).toBe(
                "pixel-bicubic",
            );
        });

        it("stores stage 1 upscale on non-bubbling range change from numeric half", async () => {
            initEditor();

            (
                document.querySelector(
                    '[data-clip-action="add-stage"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const range = document.querySelector(
                '[data-stage-field="upscale"][type="range"][data-stage-idx="1"]',
            ) as HTMLInputElement | null;

            const syncedRange = must(range);
            syncedRange.value = "1.75";
            syncedRange.dispatchEvent(new Event("change"));

            expect(parseStored()[0].stages?.[1].upscale).toBe(1.75);
        });

        it("updates stored ref strength when a stage ref slider moves", async () => {
            initEditor();

            (
                document.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const refStrengthSlider = document.querySelector(
                '[data-stage-field="refStrength_0"][type="range"]',
            ) as HTMLInputElement | null;
            const refStrength = must(refStrengthSlider);
            refStrength.value = "0.5";
            refStrength.dispatchEvent(new Event("input", { bubbles: true }));

            expect(parseStored()[0].stages?.[0].refStrengths).toEqual([0.5]);
        });

        it("updates stored ControlNet strength when its stage slider moves", async () => {
            initEditor();

            const controlNetStrengthSlider = document.querySelector(
                '[data-stage-field="controlNetStrength"][type="range"]',
            ) as HTMLInputElement | null;
            const controlNetStrength = must(controlNetStrengthSlider);
            controlNetStrength.value = "0.3";
            controlNetStrength.dispatchEvent(
                new Event("input", { bubbles: true }),
            );

            expect(parseStored()[0].stages?.[0].controlNetStrength).toBe(0.3);
        });

        it("toggles skip state for a stage", async () => {
            initEditor();

            (
                document.querySelector(
                    '[data-clip-action="add-stage"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();
            const skipBtn = must(
                document.querySelector(
                    '[data-stage-action="skip"]',
                ) as HTMLButtonElement | null,
            );
            skipBtn.click();
            await flushReRender();

            const clips = parseStored();
            expect(clips[0].stages?.[0].skipped).toBe(true);
            const refreshedSkipBtn = document.querySelector(
                '[data-stage-action="skip"]',
            ) as HTMLButtonElement | null;
            expect(refreshedSkipBtn?.className).toContain("vs-btn-skip-active");
        });

        it("removes a stage when more than one exists", async () => {
            initEditor();

            (
                document.querySelector(
                    '[data-clip-action="add-stage"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();
            const deleteBtn = document.querySelector(
                '[data-stage-action="delete"]',
            ) as HTMLButtonElement;
            deleteBtn.click();
            await flushReRender();

            const clips = parseStored();
            expect(clips[0].stages).toHaveLength(1);
        });

        it("allows deleting the only stage in a clip", async () => {
            initEditor();

            const deleteBtn = must(
                document.querySelector(
                    '[data-stage-action="delete"][data-stage-idx="0"]',
                ) as HTMLButtonElement | null,
            );
            expect(deleteBtn.disabled).toBe(false);

            deleteBtn.click();
            await flushReRender();

            const clips = parseStored();
            expect(clips[0].stages).toHaveLength(0);
            expect(
                document.querySelector(".vs-card[data-stage-idx]"),
            ).toBeNull();
            expect(
                document.querySelector('[data-clip-action="add-stage"]'),
            ).not.toBeNull();
        });
    });
});
