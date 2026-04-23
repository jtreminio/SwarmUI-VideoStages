import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { VideoStageEditor } from "./VideoStageEditor";
import { stubBase2EditStageRegistry } from "./__test_helpers__/registries";

const flushReRender = async (): Promise<void> => {
    jest.runOnlyPendingTimers();
    await Promise.resolve();
};

const OPEN_GROUP_GLYPH = "\u2B9F";

interface ParsedRef {
    source: string;
    frame: number;
    fromEnd?: boolean;
    expanded?: boolean;
    uploadFileName?: string | null;
}

interface ParsedStage {
    model: string;
    control?: number;
    steps?: number;
    cfgScale?: number;
    upscale?: number;
    refStrengths?: number[];
    expanded?: boolean;
    skipped?: boolean;
}

interface ParsedClip {
    name?: string;
    duration?: number;
    audioSource?: string;
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

    const groupToggle = document.createElement("input");
    groupToggle.type = "checkbox";
    groupToggle.id = "input_group_content_videostages_toggle";
    groupToggle.checked = true;
    document.body.appendChild(groupToggle);

    const videoModel = document.createElement("select");
    videoModel.id = "input_videomodel";
    const videoModelOption = document.createElement("option");
    videoModelOption.value = "ltx-2.3-22b-dev";
    videoModelOption.text = "ltx-2.3-22b-dev";
    videoModel.appendChild(videoModelOption);
    videoModel.value = "ltx-2.3-22b-dev";
    document.body.appendChild(videoModel);

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
        clips: Array.isArray(parsed.clips) ? parsed.clips : [],
    };
};

const parseStored = (): ParsedClip[] => parseStoredConfig().clips;

describe("VideoStageEditor", () => {
    beforeEach(() => {
        jest.useFakeTimers();
        setupParameterPanel();
    });

    afterEach(() => {
        jest.useRealTimers();
        document.body.innerHTML = "";
    });

    describe("init / seeding", () => {
        it("seeds a single default clip when no JSON is present", () => {
            const editor = new VideoStageEditor();
            editor.init();

            const clips = parseStored();
            expect(clips).toHaveLength(1);
            expect(clips[0].stages).toHaveLength(1);
            expect(clips[0].refs).toEqual([]);
            expect(clips[0].name).toBe("Clip 0");
        });

        it("seeds root dimensions from SwarmUI core width and height fields", () => {
            const coreWidthInput = document.createElement("input");
            coreWidthInput.type = "number";
            coreWidthInput.id = "input_width";
            coreWidthInput.value = "1344";
            document.body.appendChild(coreWidthInput);

            const coreHeightInput = document.createElement("input");
            coreHeightInput.type = "number";
            coreHeightInput.id = "input_height";
            coreHeightInput.value = "832";
            document.body.appendChild(coreHeightInput);

            const editor = new VideoStageEditor();
            editor.init();

            const config = parseStoredConfig();
            expect(config.width).toBe(1344);
            expect(config.height).toBe(832);
        });

        it("prefers registered VideoStages root params over core width and height defaults", () => {
            const registeredWidthInput = document.getElementById(
                "input_vswidth",
            ) as HTMLInputElement;
            registeredWidthInput.value = "1536";

            const registeredHeightInput = document.getElementById(
                "input_vsheight",
            ) as HTMLInputElement;
            registeredHeightInput.value = "864";

            const coreWidthInput = document.createElement("input");
            coreWidthInput.type = "number";
            coreWidthInput.id = "input_width";
            coreWidthInput.value = "1344";
            document.body.appendChild(coreWidthInput);

            const coreHeightInput = document.createElement("input");
            coreHeightInput.type = "number";
            coreHeightInput.id = "input_height";
            coreHeightInput.value = "832";
            document.body.appendChild(coreHeightInput);

            const editor = new VideoStageEditor();
            editor.init();

            const config = parseStoredConfig();
            expect(config.width).toBe(1536);
            expect(config.height).toBe(864);
        });

        it("seeds the first stage with the frontend default values", () => {
            const editor = new VideoStageEditor();
            editor.init();

            const defaultStage = parseStored()[0].stages?.[0];
            expect(defaultStage?.control).toBe(1);
            expect(defaultStage?.steps).toBe(8);
            expect(defaultStage?.cfgScale).toBe(1);
            expect(defaultStage?.upscale).toBe(1);
        });

        it("renders an editor div with a clip stack and add-clip button", () => {
            const editor = new VideoStageEditor();
            editor.init();

            const editorDiv = document.getElementById(
                "videostages_stage_editor",
            );
            expect(editorDiv).not.toBeNull();
            expect(
                editorDiv?.querySelector("[data-vs-clip-stack]"),
            ).not.toBeNull();
            expect(
                editorDiv?.querySelector('[data-clip-action="add-clip"]'),
            ).not.toBeNull();
        });

        it("does not render its own root width/height fields (uses registered SwarmUI sliders)", () => {
            const editor = new VideoStageEditor();
            editor.init();

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
            const coreWidthInput = document.createElement("input");
            coreWidthInput.type = "number";
            coreWidthInput.id = "input_width";
            coreWidthInput.value = "1280";
            document.body.appendChild(coreWidthInput);

            const coreHeightInput = document.createElement("input");
            coreHeightInput.type = "number";
            coreHeightInput.id = "input_height";
            coreHeightInput.value = "720";
            document.body.appendChild(coreHeightInput);

            const editor = new VideoStageEditor();
            editor.init();

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

            const coreWidthInput = document.createElement("input");
            coreWidthInput.type = "number";
            coreWidthInput.id = "input_width";
            coreWidthInput.value = "1280";
            document.body.appendChild(coreWidthInput);

            const coreHeightInput = document.createElement("input");
            coreHeightInput.type = "number";
            coreHeightInput.id = "input_height";
            coreHeightInput.value = "720";
            document.body.appendChild(coreHeightInput);

            const editor = new VideoStageEditor();
            editor.init();

            expect(registeredWidth.value).toBe("1024");
            expect(registeredHeight.value).toBe("768");
        });

        it("preserves existing JSON state", () => {
            getStagesInput().value = JSON.stringify({
                width: 800,
                height: 600,
                clips: [
                    {
                        name: "First",
                        duration: 4,
                        audioSource: "Upload",
                        refs: [{ source: "Refiner", frame: 5, fromEnd: true }],
                        stages: [
                            { model: "ltx-2.3-22b-dev", steps: 8, cfgScale: 1 },
                        ],
                    },
                ],
            });

            const editor = new VideoStageEditor();
            editor.init();

            const config = parseStoredConfig();
            const clips = parseStored();
            expect(config.width).toBe(800);
            expect(config.height).toBe(600);
            expect(clips).toHaveLength(1);
            expect(clips[0].name).toBe("First");
            expect(clips[0].audioSource).toBe("Upload");
            expect(clips[0].refs?.[0].source).toBe("Refiner");
            expect(clips[0].refs?.[0].fromEnd).toBe(true);
            expect(clips[0].stages?.[0].steps).toBe(8);
        });
    });

    describe("clip actions", () => {
        it("adds a new clip when the add-clip button is clicked", async () => {
            const editor = new VideoStageEditor();
            editor.init();

            const addBtn = document.querySelector(
                '[data-clip-action="add-clip"]',
            ) as HTMLButtonElement;
            addBtn.click();
            await flushReRender();

            const clips = parseStored();
            expect(clips).toHaveLength(2);
            expect(clips[1].name).toBe("Clip 1");
        });

        it("toggles collapse state for a clip when the native shrinkable header is clicked", async () => {
            const editor = new VideoStageEditor();
            editor.init();

            const header = document.querySelector(
                ".vs-clip-card > .input-group-shrinkable",
            ) as HTMLElement | null;
            expect(header).not.toBeNull();
            header?.click();
            await flushReRender();

            const clips = parseStored();
            expect(clips[0].expanded).toBe(false);
        });

        it("does not collapse the clip when the skip action button inside the header is clicked", async () => {
            const editor = new VideoStageEditor();
            editor.init();

            (
                document.querySelector(
                    '[data-clip-action="add-clip"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();
            const skipBtn = document.querySelector(
                '[data-clip-action="skip"]',
            ) as HTMLButtonElement;
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
            const editor = new VideoStageEditor();
            editor.init();

            (
                document.querySelector(
                    '[data-clip-action="add-clip"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();
            const skipBtn = document.querySelector(
                '[data-clip-action="skip"]',
            ) as HTMLButtonElement;
            skipBtn.click();
            await flushReRender();

            const clips = parseStored();
            expect(clips[0].skipped).toBe(true);
        });

        it("updates clip duration when the slider thumb moves", () => {
            const editor = new VideoStageEditor();
            editor.init();

            const durationSlider = document.querySelector(
                '[data-clip-field="duration"][type="range"]',
            ) as HTMLInputElement | null;
            expect(durationSlider).not.toBeNull();
            if (!durationSlider) {
                throw new Error("Expected duration slider to exist.");
            }

            durationSlider.value = "6";
            durationSlider.dispatchEvent(new Event("input", { bubbles: true }));

            const clips = parseStored();
            expect(clips[0].duration).toBe(6);
        });

        it("reflects registered RootWidth/RootHeight slider changes in saved JSON", () => {
            const editor = new VideoStageEditor();
            editor.init();

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
            expect(config.width).toBe(1536);
            expect(config.height).toBe(864);
        });

        it("stores clip audio source at the clip level", () => {
            const editor = new VideoStageEditor();
            editor.init();

            const audioSource = document.querySelector(
                '[data-clip-field="audioSource"]',
            ) as HTMLSelectElement | null;
            expect(audioSource).not.toBeNull();
            if (!audioSource) {
                throw new Error("Expected clip audio source select to exist.");
            }

            audioSource.value = "Upload";
            audioSource.dispatchEvent(new Event("change", { bubbles: true }));

            expect(parseStored()[0].audioSource).toBe("Upload");
        });

        it("renders a hidden per-clip audio upload field directly below the audio source dropdown", () => {
            const editor = new VideoStageEditor();
            editor.init();

            const audioSource = document.querySelector(
                '[data-clip-field="audioSource"][data-clip-idx="0"]',
            ) as HTMLSelectElement | null;
            const uploadField = document.querySelector(
                ".vs-clip-audio-upload-field",
            ) as HTMLElement | null;
            const uploadInput = document.querySelector(
                '.auto-file[data-clip-field="uploadedAudio"][data-clip-idx="0"]',
            ) as HTMLInputElement | null;

            expect(audioSource).not.toBeNull();
            expect(uploadField).not.toBeNull();
            expect(uploadInput).not.toBeNull();
            expect(uploadField?.style.display).toBe("none");

            // The upload field must be the very next sibling (below) the
            // audio-source row inside the clip body.
            const audioSourceRow = audioSource?.closest(".auto-input");
            expect(audioSourceRow?.nextElementSibling).toBe(uploadField);
        });

        it("reveals the per-clip audio upload field when audioSource changes to Upload", () => {
            const editor = new VideoStageEditor();
            editor.init();

            const audioSource = document.querySelector(
                '[data-clip-field="audioSource"][data-clip-idx="0"]',
            ) as HTMLSelectElement | null;
            const uploadField = document.querySelector(
                ".vs-clip-audio-upload-field",
            ) as HTMLElement | null;
            if (!audioSource || !uploadField) {
                throw new Error("Expected per-clip audio controls to exist.");
            }
            expect(uploadField.style.display).toBe("none");

            audioSource.value = "Upload";
            audioSource.dispatchEvent(new Event("change", { bubbles: true }));

            expect(uploadField.style.display).toBe("");
        });

        it("still reveals clip audio Upload when the editor DOM is rebuilt", () => {
            const editor = new VideoStageEditor();
            editor.init();

            const originalEditor = document.getElementById(
                "videostages_stage_editor",
            ) as HTMLElement | null;
            expect(originalEditor).not.toBeNull();
            if (!originalEditor?.parentElement) {
                throw new Error("Expected original editor to be mounted.");
            }

            const rebuiltEditor = originalEditor.cloneNode(true) as HTMLElement;
            originalEditor.parentElement.replaceChild(
                rebuiltEditor,
                originalEditor,
            );

            const audioSource = rebuiltEditor.querySelector(
                '[data-clip-field="audioSource"][data-clip-idx="0"]',
            ) as HTMLSelectElement | null;
            const uploadField = rebuiltEditor.querySelector(
                ".vs-clip-audio-upload-field",
            ) as HTMLElement | null;
            const uploadInput = rebuiltEditor.querySelector(
                '.auto-file[data-clip-field="uploadedAudio"][data-clip-idx="0"]',
            ) as HTMLInputElement | null;
            expect(audioSource).not.toBeNull();
            expect(uploadField).not.toBeNull();
            expect(uploadInput?.type).toBe("file");
            expect(uploadField?.style.display).toBe("none");
            if (!audioSource) {
                throw new Error("Expected rebuilt clip audio source to exist.");
            }

            audioSource.value = "Upload";
            audioSource.dispatchEvent(new Event("change", { bubbles: true }));

            expect(parseStored()[0].audioSource).toBe("Upload");
            expect(uploadField?.style.display).toBe("");
        });

        it("stores uploaded audio payload on the clip", () => {
            const editor = new VideoStageEditor();
            editor.init();

            const audioSource = document.querySelector(
                '[data-clip-field="audioSource"][data-clip-idx="0"]',
            ) as HTMLSelectElement | null;
            if (!audioSource) {
                throw new Error("Expected clip audio source select to exist.");
            }
            audioSource.value = "Upload";
            audioSource.dispatchEvent(new Event("change", { bubbles: true }));

            const uploadInput = document.querySelector(
                '.auto-file[data-clip-field="uploadedAudio"][data-clip-idx="0"]',
            ) as HTMLInputElement | null;
            if (!uploadInput) {
                throw new Error("Expected per-clip upload input to exist.");
            }

            // SwarmUI's inline `onchange="load_media_file(...)"` runs before our
            // delegated handler and would clear dataset.filedata when no real
            // File object exists; nulling onchange lets the test stub the
            // loaded audio payload synchronously.
            uploadInput.onchange = null;
            uploadInput.dataset.filedata = "data:audio/wav;base64,QUJD";
            uploadInput.dataset.filename = "clip.wav";
            uploadInput.dispatchEvent(new Event("change", { bubbles: true }));

            const clips = parseStored();
            expect(clips[0].uploadedAudio?.data).toBe(
                "data:audio/wav;base64,QUJD",
            );
            expect(clips[0].uploadedAudio?.fileName).toBe("clip.wav");

            // The legacy root-level uploadedAudio should not be present.
            const rawConfig = JSON.parse(getStagesInput().value) as Record<
                string,
                unknown
            >;
            expect(rawConfig.uploadedAudio).toBeUndefined();
        });

        it("keeps per-clip uploads independent across multiple Upload clips", async () => {
            const editor = new VideoStageEditor();
            editor.init();

            (
                document.querySelector(
                    '[data-clip-action="add-clip"]',
                ) as HTMLButtonElement
            ).click();
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

        it("migrates a legacy root-level uploadedAudio into Upload-mode clips on load", () => {
            getStagesInput().value = JSON.stringify({
                width: 1024,
                height: 768,
                uploadedAudio: {
                    data: "data:audio/wav;base64,LEGACY",
                    fileName: "legacy.wav",
                },
                clips: [
                    {
                        name: "First",
                        duration: 4,
                        audioSource: "Upload",
                        refs: [],
                        stages: [
                            { model: "ltx-2.3-22b-dev", steps: 8, cfgScale: 1 },
                        ],
                    },
                    {
                        name: "Second",
                        duration: 4,
                        audioSource: "Native",
                        refs: [],
                        stages: [
                            { model: "ltx-2.3-22b-dev", steps: 8, cfgScale: 1 },
                        ],
                    },
                ],
            });

            const editor = new VideoStageEditor();
            editor.init();

            const clips = parseStored();
            expect(clips[0].uploadedAudio?.data).toBe(
                "data:audio/wav;base64,LEGACY",
            );
            expect(clips[0].uploadedAudio?.fileName).toBe("legacy.wav");
            // Native clips should never inherit a legacy root upload.
            expect(clips[1].uploadedAudio).toBeFalsy();

            // The migration should drop the legacy root field on the next save.
            const rawConfig = JSON.parse(getStagesInput().value) as Record<
                string,
                unknown
            >;
            expect(rawConfig.uploadedAudio).toBeUndefined();
        });

        it("does not rerender the duration number input while typing", async () => {
            const editor = new VideoStageEditor();
            editor.init();

            const durationNumber = document.querySelector(
                '[data-clip-field="duration"][type="number"]',
            ) as HTMLInputElement | null;
            expect(durationNumber).not.toBeNull();
            if (!durationNumber) {
                throw new Error("Expected duration number input to exist.");
            }

            const originalDurationNumber = durationNumber;
            durationNumber.value = "15";
            durationNumber.dispatchEvent(new Event("input", { bubbles: true }));
            await flushReRender();

            expect(parseStored()[0].duration).toBe(15);
            expect(originalDurationNumber.isConnected).toBe(true);
            expect(
                document.querySelector(
                    '[data-clip-field="duration"][type="number"]',
                ),
            ).toBe(originalDurationNumber);

            durationNumber.dispatchEvent(
                new Event("change", { bubbles: true }),
            );
            await flushReRender();

            expect(
                document.querySelector(
                    '[data-clip-field="duration"][type="number"]',
                ),
            ).not.toBe(originalDurationNumber);
        });

        it("uses 0.5 second jumps for the duration slider but leaves manual entry unrestricted", () => {
            const editor = new VideoStageEditor();
            editor.init();

            const durationNumber = document.querySelector(
                '[data-clip-field="duration"][type="number"]',
            ) as HTMLInputElement | null;
            const durationSlider = document.querySelector(
                '[data-clip-field="duration"][type="range"]',
            ) as HTMLInputElement | null;

            expect(durationNumber?.step).toBe("any");
            expect(durationSlider?.step).toBe("0.5");
        });
    });

    describe("ref actions", () => {
        it("adds a new ref to a clip", async () => {
            const editor = new VideoStageEditor();
            editor.init();

            const addRefBtn = document.querySelector(
                '[data-clip-action="add-ref"]',
            ) as HTMLButtonElement;
            expect(addRefBtn).not.toBeNull();
            addRefBtn.click();
            await flushReRender();

            const clips = parseStored();
            expect(clips[0].refs).toHaveLength(1);
            expect(clips[0].refs?.[0].source).toBe("Base");
        });

        it("uses the updated reverse frame count label", async () => {
            const editor = new VideoStageEditor();
            editor.init();

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
            const editor = new VideoStageEditor();
            editor.init();

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
            const editor = new VideoStageEditor();
            editor.init();

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
            const editor = new VideoStageEditor();
            editor.init();

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
            const editor = new VideoStageEditor();
            editor.init();

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
            expect(sourceSelect).not.toBeNull();
            expect(uploadField).not.toBeNull();
            expect(uploadInput).not.toBeNull();
            expect(uploadField?.style.display).toBe("none");
            if (!sourceSelect) {
                throw new Error("Expected ref source select to exist.");
            }

            sourceSelect.value = "Upload";
            sourceSelect.dispatchEvent(new Event("change", { bubbles: true }));
            expect(uploadField?.style.display).toBe("");
            expect(uploadInput?.type).toBe("file");

            const refreshedUploadField = document.querySelector(
                ".vs-ref-upload-field",
            ) as HTMLElement | null;
            expect(parseStored()[0].refs?.[0].source).toBe("Upload");
            expect(refreshedUploadField?.style.display).toBe("");
        });

        it("stores the selected upload image filename for a ref", async () => {
            const editor = new VideoStageEditor();
            editor.init();

            (
                document.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const sourceSelect = document.querySelector(
                '[data-ref-field="source"]',
            ) as HTMLSelectElement | null;
            if (!sourceSelect) {
                throw new Error("Expected ref source select to exist.");
            }
            sourceSelect.value = "Upload";
            sourceSelect.dispatchEvent(new Event("change", { bubbles: true }));

            const uploadInput = document.querySelector(
                '.vs-ref-upload-field .auto-file[data-ref-field="uploadFileName"]',
            ) as HTMLInputElement | null;
            expect(uploadInput).not.toBeNull();
            if (!uploadInput) {
                throw new Error("Expected ref upload input to exist.");
            }

            const file = new File(["image"], "ref.png", { type: "image/png" });
            Object.defineProperty(uploadInput, "files", {
                configurable: true,
                value: [file],
            });
            uploadInput.dispatchEvent(new Event("change", { bubbles: true }));

            expect(parseStored()[0].refs?.[0].uploadFileName).toBe("ref.png");
        });

        it("still reveals Upload when the editor DOM is rebuilt", async () => {
            const editor = new VideoStageEditor();
            editor.init();

            (
                document.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const originalEditor = document.getElementById(
                "videostages_stage_editor",
            ) as HTMLElement | null;
            expect(originalEditor).not.toBeNull();
            if (!originalEditor?.parentElement) {
                throw new Error("Expected original editor to be mounted.");
            }

            const rebuiltEditor = originalEditor.cloneNode(true) as HTMLElement;
            originalEditor.parentElement.replaceChild(
                rebuiltEditor,
                originalEditor,
            );

            const sourceSelect = rebuiltEditor.querySelector(
                '[data-ref-field="source"]',
            ) as HTMLSelectElement | null;
            const uploadField = rebuiltEditor.querySelector(
                ".vs-ref-upload-field",
            ) as HTMLElement | null;
            expect(sourceSelect).not.toBeNull();
            expect(uploadField?.style.display).toBe("none");
            if (!sourceSelect) {
                throw new Error("Expected rebuilt ref source select to exist.");
            }

            sourceSelect.value = "Upload";
            sourceSelect.dispatchEvent(new Event("change", { bubbles: true }));

            expect(parseStored()[0].refs?.[0].source).toBe("Upload");
            expect(uploadField?.style.display).toBe("");
        });

        it("removes the matching ref strength from each stage when a ref is deleted", async () => {
            getStagesInput().value = JSON.stringify([
                {
                    name: "First",
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

            const editor = new VideoStageEditor();
            editor.init();

            const deleteBtn = document.querySelector(
                '[data-ref-action="delete"][data-ref-idx="0"]',
            ) as HTMLButtonElement | null;
            expect(deleteBtn).not.toBeNull();
            deleteBtn?.click();
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

        it("refreshes the reference frame slider max when root frames change", async () => {
            const editor = new VideoStageEditor();
            editor.init();

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
            expect(frameNumber?.max).toBe("48");
            expect(frameRange?.max).toBe("48");

            const rootFramesInput = document.getElementById(
                "input_videoframes",
            ) as HTMLInputElement | null;
            expect(rootFramesInput).not.toBeNull();
            if (!rootFramesInput) {
                throw new Error("Expected root video frames input to exist.");
            }

            rootFramesInput.value = "96";
            rootFramesInput.dispatchEvent(
                new Event("change", { bubbles: true }),
            );
            await flushReRender();

            const refreshedFrameNumber = document.querySelector(
                '[data-ref-field="frame"][type="number"]',
            ) as HTMLInputElement | null;
            const refreshedFrameRange = document.querySelector(
                '[data-ref-field="frame"][type="range"]',
            ) as HTMLInputElement | null;
            expect(refreshedFrameNumber?.max).toBe("96");
            expect(refreshedFrameRange?.max).toBe("96");
        });
    });

    describe("stage actions", () => {
        it("adds a new stage to a clip", async () => {
            const editor = new VideoStageEditor();
            editor.init();

            const addStageBtn = document.querySelector(
                '[data-clip-action="add-stage"]',
            ) as HTMLButtonElement;
            addStageBtn.click();
            await flushReRender();

            const clips = parseStored();
            expect(clips[0].stages).toHaveLength(2);
        });

        it("initializes new stages with default ref strengths for existing refs", async () => {
            const editor = new VideoStageEditor();
            editor.init();

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

        it("renders control, steps, cfg scale, and upscale as slider fields", () => {
            const editor = new VideoStageEditor();
            editor.init();

            const controlSlider = document.querySelector(
                '[data-stage-field="control"][type="range"]',
            ) as HTMLInputElement | null;
            const stepsSlider = document.querySelector(
                '[data-stage-field="steps"][type="range"]',
            ) as HTMLInputElement | null;
            const cfgScaleSlider = document.querySelector(
                '[data-stage-field="cfgScale"][type="range"]',
            ) as HTMLInputElement | null;
            const upscaleSlider = document.querySelector(
                '[data-stage-field="upscale"][type="range"]',
            ) as HTMLInputElement | null;

            expect(controlSlider).not.toBeNull();
            expect(stepsSlider).not.toBeNull();
            expect(cfgScaleSlider).not.toBeNull();
            expect(upscaleSlider).not.toBeNull();
            expect(controlSlider?.min).toBe("0.05");
            expect(stepsSlider?.max).toBe("50");
            expect(cfgScaleSlider?.max).toBe("10");
            expect(upscaleSlider?.max).toBe("4");
        });

        it("renders stage headers as Stage n labels with zero-based indexes", async () => {
            const editor = new VideoStageEditor();
            editor.init();

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

        it("only disables Upscale Method when upscale equals 1", () => {
            const editor = new VideoStageEditor();
            editor.init();

            const upscaleMethod = document.querySelector(
                '[data-stage-field="upscaleMethod"]',
            ) as HTMLSelectElement | null;
            const upscaleSlider = document.querySelector(
                '[data-stage-field="upscale"][type="range"]',
            ) as HTMLInputElement | null;
            expect(upscaleMethod).not.toBeNull();
            expect(upscaleSlider).not.toBeNull();
            expect(upscaleMethod?.disabled).toBe(true);
            if (!upscaleMethod || !upscaleSlider) {
                throw new Error("Expected stage upscale controls to exist.");
            }

            upscaleSlider.value = "1.25";
            upscaleSlider.dispatchEvent(new Event("input", { bubbles: true }));
            expect(parseStored()[0].stages?.[0].upscale).toBe(1.25);
            expect(upscaleMethod.disabled).toBe(false);

            upscaleSlider.value = "1";
            upscaleSlider.dispatchEvent(new Event("input", { bubbles: true }));
            expect(parseStored()[0].stages?.[0].upscale).toBe(1);
            expect(upscaleMethod.disabled).toBe(true);
        });

        it("updates stored ref strength when a stage ref slider moves", async () => {
            const editor = new VideoStageEditor();
            editor.init();

            (
                document.querySelector(
                    '[data-clip-action="add-ref"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();

            const refStrengthSlider = document.querySelector(
                '[data-stage-field="refStrength_0"][type="range"]',
            ) as HTMLInputElement | null;
            expect(refStrengthSlider).not.toBeNull();
            if (!refStrengthSlider) {
                throw new Error("Expected stage ref strength slider to exist.");
            }

            refStrengthSlider.value = "0.5";
            refStrengthSlider.dispatchEvent(
                new Event("input", { bubbles: true }),
            );

            expect(parseStored()[0].stages?.[0].refStrengths).toEqual([0.5]);
        });

        it("toggles skip state for a stage", async () => {
            const editor = new VideoStageEditor();
            editor.init();

            (
                document.querySelector(
                    '[data-clip-action="add-stage"]',
                ) as HTMLButtonElement
            ).click();
            await flushReRender();
            const skipBtn = document.querySelector(
                '[data-stage-action="skip"]',
            ) as HTMLButtonElement;
            expect(skipBtn).not.toBeNull();
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
            const editor = new VideoStageEditor();
            editor.init();

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
    });
});
