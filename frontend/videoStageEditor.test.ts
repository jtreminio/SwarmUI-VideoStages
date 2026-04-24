import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { stubBase2EditStageRegistry } from "./__test_helpers__/registries";
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
    steps?: number;
    cfgScale?: number;
    upscale?: number;
    upscaleMethod?: string;
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
        fps: parsed.fps,
        clips: Array.isArray(parsed.clips) ? parsed.clips : [],
    };
};

const parseStored = (): ParsedClip[] => parseStoredConfig().clips;

describe("videoStageEditor", () => {
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
            const editor = videoStageEditor();
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

            const editor = videoStageEditor();
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

            const editor = videoStageEditor();
            editor.init();

            const config = parseStoredConfig();
            expect(config.width).toBe(1536);
            expect(config.height).toBe(864);
        });

        it("prefers registered VideoStages root FPS over core video FPS", () => {
            const registeredFpsInput = document.getElementById(
                "input_vsfps",
            ) as HTMLInputElement;
            registeredFpsInput.value = "32";

            const editor = videoStageEditor();
            editor.init();

            const config = parseStoredConfig();
            expect(config.fps).toBe(32);
        });

        it("seeds the first stage with the frontend default values", () => {
            const editor = videoStageEditor();
            editor.init();

            const defaultStage = parseStored()[0].stages?.[0];
            expect(defaultStage?.control).toBe(1);
            expect(defaultStage?.steps).toBe(8);
            expect(defaultStage?.cfgScale).toBe(1);
            expect(defaultStage?.upscale).toBe(1);
        });

        it("renders an editor div with a clip stack and add-clip button", () => {
            const editor = videoStageEditor();
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
            const editor = videoStageEditor();
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

            const editor = videoStageEditor();
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

            const editor = videoStageEditor();
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

            const editor = videoStageEditor();
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
            const editor = videoStageEditor();
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
            const editor = videoStageEditor();
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
            const editor = videoStageEditor();
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
            const editor = videoStageEditor();
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
            const editor = videoStageEditor();
            editor.init();

            const durationSlider = document.querySelector(
                '[data-clip-field="duration"][type="range"]',
            ) as HTMLInputElement | null;
            expect(durationSlider).not.toBeNull();
            const slider = must(durationSlider);
            slider.value = "6";
            slider.dispatchEvent(new Event("input", { bubbles: true }));

            const clips = parseStored();
            expect(clips[0].duration).toBe(6);
        });

        it("reflects registered RootWidth/RootHeight slider changes in saved JSON", () => {
            const editor = videoStageEditor();
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
            const editor = videoStageEditor();
            editor.init();

            const audioSource = document.querySelector(
                '[data-clip-field="audioSource"]',
            ) as HTMLSelectElement | null;
            expect(audioSource).not.toBeNull();
            const source = must(audioSource);
            source.value = "Upload";
            source.dispatchEvent(new Event("change", { bubbles: true }));

            expect(parseStored()[0].audioSource).toBe("Upload");
        });

        it("renders a hidden per-clip audio upload field directly below the audio source dropdown", () => {
            const editor = videoStageEditor();
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

            const audioSourceRow = audioSource?.closest(".auto-input");
            expect(audioSourceRow?.nextElementSibling).toBe(uploadField);
        });

        it("reveals the per-clip audio upload field when audioSource changes to Upload", () => {
            const editor = videoStageEditor();
            editor.init();

            const audioSource = document.querySelector(
                '[data-clip-field="audioSource"][data-clip-idx="0"]',
            ) as HTMLSelectElement | null;
            const uploadField = document.querySelector(
                ".vs-clip-audio-upload-field",
            ) as HTMLElement | null;
            expect(audioSource).not.toBeNull();
            expect(uploadField).not.toBeNull();
            const upload = must(uploadField);
            expect(upload.style.display).toBe("none");

            const src = must(audioSource);
            src.value = "Upload";
            src.dispatchEvent(new Event("change", { bubbles: true }));

            expect(upload.style.display).toBe("");
        });

        it("still reveals clip audio Upload when the editor DOM is rebuilt", () => {
            const editor = videoStageEditor();
            editor.init();

            const originalEditor = document.getElementById(
                "videostages_stage_editor",
            ) as HTMLElement | null;
            expect(originalEditor).not.toBeNull();
            const root = must(originalEditor);
            expect(root.parentElement).not.toBeNull();
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
            expect(audioSource).not.toBeNull();
            expect(uploadField).not.toBeNull();
            expect(uploadInput?.type).toBe("file");
            expect(uploadField?.style.display).toBe("none");

            const clipSource = must(audioSource);
            clipSource.value = "Upload";
            clipSource.dispatchEvent(new Event("change", { bubbles: true }));

            expect(parseStored()[0].audioSource).toBe("Upload");
            expect(uploadField?.style.display).toBe("");
        });

        it("stores uploaded audio payload on the clip", () => {
            const editor = videoStageEditor();
            editor.init();

            const audioSource = document.querySelector(
                '[data-clip-field="audioSource"][data-clip-idx="0"]',
            ) as HTMLSelectElement | null;
            expect(audioSource).not.toBeNull();
            const clipAudioSource = must(audioSource);
            clipAudioSource.value = "Upload";
            clipAudioSource.dispatchEvent(
                new Event("change", { bubbles: true }),
            );

            const uploadInput = document.querySelector(
                '.auto-file[data-clip-field="uploadedAudio"][data-clip-idx="0"]',
            ) as HTMLInputElement | null;
            expect(uploadInput).not.toBeNull();
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

        it("keeps per-clip uploads independent across multiple Upload clips", async () => {
            const editor = videoStageEditor();
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

        it("does not rerender the duration number input while typing", async () => {
            const editor = videoStageEditor();
            editor.init();

            const durationNumber = document.querySelector(
                '[data-clip-field="duration"][type="number"]',
            ) as HTMLInputElement | null;
            expect(durationNumber).not.toBeNull();
            const durationEl = must(durationNumber);
            const originalDurationNumber = durationEl;
            durationEl.value = "15";
            durationEl.dispatchEvent(new Event("input", { bubbles: true }));
            await flushReRender();

            expect(parseStored()[0].duration).toBe(15);
            expect(originalDurationNumber.isConnected).toBe(true);
            expect(
                document.querySelector(
                    '[data-clip-field="duration"][type="number"]',
                ),
            ).toBe(originalDurationNumber);

            durationEl.dispatchEvent(new Event("change", { bubbles: true }));
            await flushReRender();

            expect(
                document.querySelector(
                    '[data-clip-field="duration"][type="number"]',
                ),
            ).not.toBe(originalDurationNumber);
        });

        it("uses 0.5 second jumps for the duration slider but leaves manual entry unrestricted", () => {
            const editor = videoStageEditor();
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
            const editor = videoStageEditor();
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
            const editor = videoStageEditor();
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
            const editor = videoStageEditor();
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
            const editor = videoStageEditor();
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
            const editor = videoStageEditor();
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
            const editor = videoStageEditor();
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
            const refUploadField = must(uploadField);
            expect(refUploadField.style.display).toBe("none");

            const refSource = must(sourceSelect);
            refSource.value = "Upload";
            refSource.dispatchEvent(new Event("change", { bubbles: true }));
            expect(refUploadField.style.display).toBe("");
            expect(must(uploadInput).type).toBe("file");

            const refreshedUploadField = document.querySelector(
                ".vs-ref-upload-field",
            ) as HTMLElement | null;
            expect(parseStored()[0].refs?.[0].source).toBe("Upload");
            expect(refreshedUploadField?.style.display).toBe("");
        });

        it("stores ref upload image payload when Swarm provides filedata on the file input", async () => {
            const editor = videoStageEditor();
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
            expect(sourceSelect).not.toBeNull();
            const refSrc = must(sourceSelect);
            refSrc.value = "Upload";
            refSrc.dispatchEvent(new Event("change", { bubbles: true }));

            const uploadInput = document.querySelector(
                '.vs-ref-upload-field .auto-file[data-ref-field="uploadFileName"]',
            ) as HTMLInputElement | null;
            expect(uploadInput).not.toBeNull();
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
            const editor = videoStageEditor();
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
            const refRoot = must(originalEditor);
            expect(refRoot.parentElement).not.toBeNull();
            const refParent = must(refRoot.parentElement);

            const rebuiltEditor = refRoot.cloneNode(true) as HTMLElement;
            refParent.replaceChild(rebuiltEditor, refRoot);

            const sourceSelect = rebuiltEditor.querySelector(
                '[data-ref-field="source"]',
            ) as HTMLSelectElement | null;
            const uploadField = rebuiltEditor.querySelector(
                ".vs-ref-upload-field",
            ) as HTMLElement | null;
            expect(sourceSelect).not.toBeNull();
            expect(uploadField?.style.display).toBe("none");

            const rebuiltSource = must(sourceSelect);
            rebuiltSource.value = "Upload";
            rebuiltSource.dispatchEvent(new Event("change", { bubbles: true }));

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

            const editor = videoStageEditor();
            editor.init();

            const deleteBtn = document.querySelector(
                '[data-ref-action="delete"][data-ref-idx="0"]',
            ) as HTMLButtonElement | null;
            expect(deleteBtn).not.toBeNull();
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
            const editor = videoStageEditor();
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
            expect(frameNumber?.max).toBe("49");
            expect(frameRange?.max).toBe("49");

            const durationNumber = document.querySelector(
                '[data-clip-field="duration"][type="number"]',
            ) as HTMLInputElement | null;
            expect(durationNumber).not.toBeNull();
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
    });

    describe("stage actions", () => {
        it("adds a new stage to a clip", async () => {
            const editor = videoStageEditor();
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
            const editor = videoStageEditor();
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

        it("renders control, steps, cfg scale, and upscale; disables stage 0 upscale only", async () => {
            const editor = videoStageEditor();
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
            const upscale0 = document.querySelector(
                '[data-stage-field="upscale"][type="range"][data-stage-idx="0"]',
            ) as HTMLInputElement | null;

            expect(controlSlider).not.toBeNull();
            expect(stepsSlider).not.toBeNull();
            expect(cfgScaleSlider).not.toBeNull();
            expect(upscale0).not.toBeNull();
            expect(upscale0?.disabled).toBe(true);
            expect(controlSlider?.min).toBe("0.05");
            expect(stepsSlider?.max).toBe("50");
            expect(cfgScaleSlider?.max).toBe("10");
            expect(upscale0?.max).toBe("4");
            const upscale0n = document.querySelector(
                '[data-stage-field="upscale"][type="number"][data-stage-idx="0"]',
            ) as HTMLInputElement | null;
            expect(upscale0n?.disabled).toBe(true);

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
            const editor = videoStageEditor();
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

        it("disables first-stage upscale controls; stage 1 follows upscale vs method rules", async () => {
            const editor = videoStageEditor();
            editor.init();

            const s0Method = document.querySelector(
                '[data-stage-field="upscaleMethod"][data-stage-idx="0"]',
            ) as HTMLSelectElement | null;
            const s0Range = document.querySelector(
                '[data-stage-field="upscale"][type="range"][data-stage-idx="0"]',
            ) as HTMLInputElement | null;
            const s0Number = document.querySelector(
                '[data-stage-field="upscale"][type="number"][data-stage-idx="0"]',
            ) as HTMLInputElement | null;
            expect(s0Method?.disabled).toBe(true);
            expect(s0Range?.disabled).toBe(true);
            expect(s0Number?.disabled).toBe(true);
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
            const s1Range = document.querySelector(
                '[data-stage-field="upscale"][type="range"][data-stage-idx="1"]',
            ) as HTMLInputElement | null;
            expect(s1Range).not.toBeNull();
            expect(s1Range?.disabled).toBe(false);
            expect(s1Method?.disabled).toBe(true);

            const stage1Upscale = must(s1Range);
            stage1Upscale.value = "1.25";
            stage1Upscale.dispatchEvent(new Event("input", { bubbles: true }));
            expect(parseStored()[0].stages?.[1].upscale).toBe(1.25);
            expect(s1Method?.disabled).toBe(false);
        });

        it("keeps stage upscale method when upscale slider changes", async () => {
            const editor = videoStageEditor();
            editor.init();

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
            expect(s1Range).not.toBeNull();
            expect(s1Method).not.toBeNull();
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

        it("updates stored ref strength when a stage ref slider moves", async () => {
            const editor = videoStageEditor();
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
            const refStrength = must(refStrengthSlider);
            refStrength.value = "0.5";
            refStrength.dispatchEvent(new Event("input", { bubbles: true }));

            expect(parseStored()[0].stages?.[0].refStrengths).toEqual([0.5]);
        });

        it("toggles skip state for a stage", async () => {
            const editor = videoStageEditor();
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
            const editor = videoStageEditor();
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
