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

interface ParsedRef {
    source: string;
    frame: number;
    fromEnd?: boolean;
    expanded?: boolean;
    uploadFileName?: string | null;
}

interface ParsedStage {
    model: string;
    steps?: number;
    cfgScale?: number;
    upscale?: number;
    expanded?: boolean;
    skipped?: boolean;
}

interface ParsedClip {
    name?: string;
    duration?: number;
    width?: number;
    height?: number;
    expanded?: boolean;
    skipped?: boolean;
    refs?: ParsedRef[];
    stages?: ParsedStage[];
}

const setupParameterPanel = (): void => {
    const groupContent = document.createElement("div");
    groupContent.id = "input_group_content_videostages";
    document.body.appendChild(groupContent);

    const enableToggle = document.createElement("input");
    enableToggle.type = "checkbox";
    enableToggle.id = "input_enableadditionalvideostages";
    enableToggle.checked = true;
    document.body.appendChild(enableToggle);

    const stagesInput = document.createElement("input");
    stagesInput.type = "text";
    stagesInput.id = "input_videostages";
    document.body.appendChild(stagesInput);

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

const parseStored = (): ParsedClip[] =>
    JSON.parse(getStagesInput().value || "[]") as ParsedClip[];

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

        it("preserves existing JSON state", () => {
            getStagesInput().value = JSON.stringify([
                {
                    name: "First",
                    duration: 4,
                    width: 800,
                    height: 600,
                    refs: [{ source: "Refiner", frame: 5, fromEnd: true }],
                    stages: [
                        { model: "ltx-2.3-22b-dev", steps: 8, cfgScale: 1 },
                    ],
                },
            ]);

            const editor = new VideoStageEditor();
            editor.init();

            const clips = parseStored();
            expect(clips).toHaveLength(1);
            expect(clips[0].name).toBe("First");
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
