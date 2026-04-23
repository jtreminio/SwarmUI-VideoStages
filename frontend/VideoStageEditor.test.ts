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

        it("uses SwarmUI-style pot slider jumps for clip width and height", () => {
            const editor = new VideoStageEditor();
            editor.init();

            const widthNumber = document.querySelector(
                '[data-clip-field="width"][type="number"]',
            ) as HTMLInputElement | null;
            const widthSlider = document.querySelector(
                '[data-clip-field="width"][type="range"]',
            ) as HTMLInputElement | null;
            const heightNumber = document.querySelector(
                '[data-clip-field="height"][type="number"]',
            ) as HTMLInputElement | null;
            const heightSlider = document.querySelector(
                '[data-clip-field="height"][type="range"]',
            ) as HTMLInputElement | null;

            expect(widthNumber?.step).toBe("32");
            expect(widthSlider?.step).toBe("1");
            expect(widthSlider?.dataset.ispot).toBe("true");
            expect(heightNumber?.step).toBe("32");
            expect(heightSlider?.step).toBe("1");
            expect(heightSlider?.dataset.ispot).toBe("true");
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
