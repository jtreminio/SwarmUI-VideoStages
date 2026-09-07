import { beforeEach, describe, expect, it, jest } from "@jest/globals";
import { resetArchitectureCatalogForTests } from "../__test_helpers__/architectureCatalog";
import {
    testArchitectureCapabilities,
    testArchitectureCatalog,
    testArchitectureCatalogDto,
} from "../__test_helpers__/architectureFixtures";
import { initVideoFixture } from "../__test_helpers__/clipFixtures";
import { lastSavedClips } from "../__test_helpers__/dom";
import {
    editorRoot as detail,
    editorHarness,
} from "../__test_helpers__/editor";
import { loadAuthoritativeArchitectureCatalog } from "../architectures/catalog";
import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import * as persistence from "../persistence/repository";
import { getSelection, setSelection } from "../selection";
import type { Clip } from "../types";

const committedClips = persistence.getClips;
const fieldByLabel = (label: string): HTMLElement => {
    const row = Array.from(
        detail().querySelectorAll<HTMLElement>(".vst-detail-field"),
    ).find(
        (row) =>
            row.querySelector(".vst-detail-field-label")?.firstChild
                ?.textContent === label,
    );
    if (!row) throw new Error(`Missing field: ${label}`);
    return row;
};
const sliderNumberByLabel = (label: string): HTMLInputElement => {
    const input = fieldByLabel(label).querySelector<HTMLInputElement>(
        "input.auto-slider-number",
    );
    if (!input) throw new Error(`Missing slider: ${label}`);
    return input;
};
describe("production authoring controls", () => {
    const h = editorHarness();
    beforeEach(() => {
        jest.spyOn(HTMLMediaElement.prototype, "pause").mockImplementation(
            () => {},
        );
        jest.spyOn(HTMLMediaElement.prototype, "load").mockImplementation(
            () => {},
        );
    });

    it("creates one reference per media file dropped on Add Reference", async () => {
        const catalog = testArchitectureCatalog();
        catalog.architectures[0].capabilities = testArchitectureCapabilities({
            features: [
                ...catalog.architectures[0].capabilities.features,
                "clipReferences",
            ],
        });
        resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => testArchitectureCatalogDto(catalog),
        });
        await loadAuthoritativeArchitectureCatalog();
        h.setup([{ duration: 4, stages: [{}], references: [] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        h.refreshSpy.mockClear();
        const dock = detail();
        const addReference = dock.querySelector<HTMLButtonElement>(
            '[data-testid="add-references"]',
        );
        if (!addReference) {
            throw new Error("add reference button missing");
        }
        const backgroundDragover = new Event("dragover", {
            bubbles: true,
            cancelable: true,
        });
        Object.defineProperty(backgroundDragover, "dataTransfer", {
            value: {
                files: [],
                types: ["Files"],
                dropEffect: "none",
                getData: () => "",
            },
        });
        dock.dispatchEvent(backgroundDragover);

        expect(backgroundDragover.defaultPrevented).toBe(false);

        const dragover = new Event("dragover", {
            bubbles: true,
            cancelable: true,
        });
        const dragoverTransfer = {
            files: [],
            types: ["Files"],
            dropEffect: "none",
            getData: () => "",
        };
        Object.defineProperty(dragover, "dataTransfer", {
            value: dragoverTransfer,
        });
        addReference.dispatchEvent(dragover);

        expect(dragover.defaultPrevented).toBe(true);
        expect(dragoverTransfer.dropEffect).toBe("copy");
        expect(addReference.classList).toContain("vst-file-drop-hover");

        const event = new Event("drop", {
            bubbles: true,
            cancelable: true,
        });
        Object.defineProperty(event, "dataTransfer", {
            value: {
                files: [
                    new File(["png"], "first.png", { type: "image/png" }),
                    new File(["webp"], "second.webp", {
                        type: "image/webp",
                    }),
                ],
                dropEffect: "none",
                getData: () => "",
            },
        });

        addReference.dispatchEvent(event);
        await new Promise((resolve) => setTimeout(resolve, 20));

        expect(event.defaultPrevented).toBe(true);
        expect(
            committedClips()[0].references.map((reference) => ({
                kind: reference.kind,
                fileName: reference.uploadedMedia?.fileName,
            })),
        ).toEqual([
            { kind: "image", fileName: "first.png" },
            { kind: "image", fileName: "second.webp" },
        ]);
        expect(getSelection()).toEqual({
            kind: "clip-ref",
            clipIdx: 0,
            referenceIdx: 1,
        });
        expect(h.refreshSpy).toHaveBeenCalledTimes(1);
    });
    it("creates forced-image keyframes for every file in a dropped folder", async () => {
        h.setup([{ duration: 4, stages: [{}], frameRefs: [] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        h.refreshSpy.mockClear();
        const addKeyframe = detail().querySelector<HTMLButtonElement>(
            '[data-testid="add-keyframes"]',
        );
        if (!addKeyframe) {
            throw new Error("add keyframe button missing");
        }
        const video = new File(["video"], "first.mp4", {
            type: "video/mp4",
        });
        const audio = new File(["audio"], "second.wav", {
            type: "audio/wav",
        });
        const fileEntry = (file: File) => ({
            isFile: true,
            isDirectory: false,
            file: (accept: (picked: File) => void) => accept(file),
        });
        let nestedRead = false;
        const nestedFolder = {
            isFile: false,
            isDirectory: true,
            createReader: () => ({
                readEntries: (accept: (entries: unknown[]) => void) => {
                    accept(nestedRead ? [] : [fileEntry(audio)]);
                    nestedRead = true;
                },
            }),
        };
        let rootRead = false;
        const folder = {
            isFile: false,
            isDirectory: true,
            createReader: () => ({
                readEntries: (accept: (entries: unknown[]) => void) => {
                    accept(rootRead ? [] : [fileEntry(video), nestedFolder]);
                    rootRead = true;
                },
            }),
        };
        const transfer = {
            files: [],
            items: [
                {
                    kind: "file",
                    getAsFile: () => null,
                    webkitGetAsEntry: () => folder,
                },
            ],
            types: ["Files"],
            dropEffect: "none",
            getData: () => "",
        };
        const dragover = new Event("dragover", {
            bubbles: true,
            cancelable: true,
        });
        Object.defineProperty(dragover, "dataTransfer", { value: transfer });
        addKeyframe.dispatchEvent(dragover);

        expect(addKeyframe.classList).toContain("vst-file-drop-hover");

        const drop = new Event("drop", {
            bubbles: true,
            cancelable: true,
        });
        Object.defineProperty(drop, "dataTransfer", { value: transfer });
        addKeyframe.dispatchEvent(drop);
        await new Promise((resolve) => setTimeout(resolve, 20));

        expect(
            committedClips()[0].frameRefs.map((reference) => ({
                source: reference.source,
                fileName: reference.uploadedImage?.fileName,
                data: reference.uploadedImage?.data,
            })),
        ).toEqual([
            {
                source: "Upload",
                fileName: "first.mp4",
                data: expect.stringMatching(/^vst-browser-media:/),
            },
            {
                source: "Upload",
                fileName: "second.wav",
                data: expect.stringMatching(/^vst-browser-media:/),
            },
        ]);
        expect(getSelection()).toEqual({
            kind: "ref",
            clipIdx: 0,
            refIdx: 1,
        });
        expect(h.refreshSpy).toHaveBeenCalledTimes(1);
    });
    it("reports keyframe files that exceed the available positions", async () => {
        const catalog = testArchitectureCatalog();
        catalog.entries[0].enhancements = { referencePositions: ["first"] };
        const showError = jest.fn();
        resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => testArchitectureCatalogDto(catalog),
            showError,
        });
        await loadAuthoritativeArchitectureCatalog();
        h.setup([{ duration: 4, stages: [{}], frameRefs: [] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const addKeyframe = detail().querySelector<HTMLButtonElement>(
            '[data-testid="add-keyframes"]',
        );
        if (!addKeyframe) {
            throw new Error("add keyframe button missing");
        }
        const drop = new Event("drop", {
            bubbles: true,
            cancelable: true,
        });
        Object.defineProperty(drop, "dataTransfer", {
            value: {
                files: [
                    new File(["a"], "first.png", { type: "image/png" }),
                    new File(["b"], "extra.png", { type: "image/png" }),
                ],
                types: ["Files"],
                dropEffect: "none",
                getData: () => "",
            },
        });

        addKeyframe.dispatchEvent(drop);
        await new Promise((resolve) => setTimeout(resolve, 20));

        expect(committedClips()[0].frameRefs).toHaveLength(1);
        expect(showError).toHaveBeenCalledWith(
            expect.stringMatching(/Added 1 of 2 keyframes/),
        );
    });
    it("remaps IC-LoRA stage targets when a stage is deleted", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}, {}, {}],
                icLoras: [
                    { lora: "a", stage: 2 },
                    {
                        lora: "b",
                        stage: 1,
                        driveSource: "Incoming",
                        driveData: "visual",
                    },
                    { lora: "c", stage: 0 },
                    { lora: "d", stage: -1 },
                ],
            },
        ]);
        detail()
            .querySelectorAll<HTMLButtonElement>(
                '[data-section="stages"] [data-testid="remove-item"]',
            )[0]
            ?.click();
        const clip = lastSavedClips<Clip[]>(h.saveSpy)[0];
        expect(clip.stages).toHaveLength(2);
        expect(clip.icLoras.map((e) => e.stage)).toEqual([1, -1, 0, -1]);
        expect(clip.icLoras[1].driveSource).toBe("Upload");
    });
    it("caps the Duration slider at 20 while accepting larger typed values", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        jest.useFakeTimers();
        const duration = sliderNumberByLabel("Duration (s)");
        const range = duration
            .closest(".auto-slider-box")
            ?.querySelector<HTMLInputElement>("input.auto-slider-range");
        if (!range) {
            throw new Error("duration slider missing");
        }
        expect(duration.min).toBe("1");
        expect(duration.max).toBe("9999");
        expect(range.min).toBe("1");
        expect(range.max).toBe("20");

        duration.value = "50";
        duration.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(h.saveSpy).toHaveBeenCalledTimes(1);
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].duration).toBe(50);
    });
    it("renders Use SeedVR below Duration and commits the clip toggle", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });

        const duration = fieldByLabel("Duration (s)");
        const seedVr = fieldByLabel("Use SeedVR");
        expect(duration.nextElementSibling).toBe(seedVr);
        const toggle = seedVr.querySelector<HTMLInputElement>(
            'input[type="checkbox"]',
        );
        expect(toggle?.checked).toBe(false);
        if (!toggle) {
            throw new Error("SeedVR toggle missing");
        }

        toggle.checked = true;
        toggle.dispatchEvent(new Event("change", { bubbles: true }));

        expect(h.saveSpy).toHaveBeenCalledTimes(1);
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].useSeedVr).toBe(true);
    });
    it("sets Clip 1 to Clip 0 audio when Clip 0 becomes audio-only", () => {
        h.setup([
            { duration: 4, stages: [{}], audioOnly: false },
            {
                duration: 4,
                stages: [{}],
                audioSource: "Native",
                initVideo: initVideoFixture({
                    source: "Previous Clip",
                }),
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });

        const toggle = fieldByLabel(
            "Audio Only",
        ).querySelector<HTMLInputElement>('input[type="checkbox"]');
        if (!toggle) {
            throw new Error("Audio Only toggle missing");
        }
        toggle.checked = true;
        toggle.dispatchEvent(new Event("change", { bubbles: true }));

        expect(persistence.getState().clips[1].audioSource).toBe(
            "Previous Clip",
        );
        expect(persistence.getState().clips[1].initVideo).toBeNull();
    });
    it("discards existing Clip 0 audio tracks when Audio Only changes", () => {
        h.setup([
            { duration: 4, stages: [{}], audioOnly: false },
            { duration: 4, stages: [{}] },
        ]);
        const state = persistence.getState();
        state.audioTracks = [
            {
                id: "track-clip-0",
                source: {
                    kind: "Upload",
                    reference: "score.wav",
                    uploadedAudio: null,
                },
                spans: [
                    {
                        id: "span-clip-0",
                        timelineStartSeconds: 1,
                        timelineLengthSeconds: 2,
                        sourceStartSeconds: 0,
                    },
                ],
            },
        ];
        persistence.saveState(state, { notifyDomChange: false });
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });

        const toggle = fieldByLabel(
            "Audio Only",
        ).querySelector<HTMLInputElement>('input[type="checkbox"]');
        if (!toggle) {
            throw new Error("Audio Only toggle missing");
        }
        toggle.checked = true;
        toggle.dispatchEvent(new Event("change", { bubbles: true }));

        expect(persistence.getState().audioTracks).toEqual([]);
    });
    it("discards a single audio-only output track", () => {
        h.setup([{ duration: 4, stages: [{}], audioOnly: false }]);
        const state = persistence.getState();
        state.audioTracks = [
            {
                id: "track-only",
                source: {
                    kind: "Upload",
                    reference: "score.wav",
                    uploadedAudio: null,
                },
                spans: [
                    {
                        id: "span-only",
                        timelineStartSeconds: 0,
                        timelineLengthSeconds: 2,
                        sourceStartSeconds: 0,
                    },
                ],
            },
        ];
        persistence.saveState(state, { notifyDomChange: false });
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });

        const toggle = fieldByLabel(
            "Audio Only",
        ).querySelector<HTMLInputElement>('input[type="checkbox"]');
        if (!toggle) {
            throw new Error("Audio Only toggle missing");
        }
        toggle.checked = true;
        toggle.dispatchEvent(new Event("change", { bubbles: true }));

        expect(persistence.getState().audioTracks).toEqual([]);
    });
    it("disables Audio Only when a source video is selected", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}],
                initVideo: initVideoFixture(),
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });

        const audioOnly = fieldByLabel("Audio Only");
        expect(
            audioOnly.querySelector<HTMLInputElement>('input[type="checkbox"]')
                ?.disabled,
        ).toBe(true);
        expect(audioOnly.classList.contains("vst-audio-disabled")).toBe(true);
    });
    it("offers Clip 0 audio only to Clip 1 as its previous clip", () => {
        h.setup([
            { duration: 4, stages: [{}], audioOnly: true },
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "audio", clipIdx: 1 });

        let source =
            fieldByLabel("Audio Source").querySelector<HTMLSelectElement>(
                "select",
            );
        expect(
            Array.from(source?.options ?? []).map((option) => ({
                value: option.value,
                label: option.textContent,
            })),
        ).toEqual([{ value: "Previous Clip", label: "Clip 0 audio" }]);
        expect(source?.disabled).toBe(true);

        setSelection({ kind: "audio", clipIdx: 2 });
        source =
            fieldByLabel("Audio Source").querySelector<HTMLSelectElement>(
                "select",
            );
        expect(
            Array.from(source?.options ?? []).map((option) => ({
                value: option.value,
                label: option.textContent,
            })),
        ).toContainEqual({
            value: "Previous Clip",
            label: "Previous Clip Output",
        });
    });
    it("disables the Duration field when clip length is derived from audio", () => {
        h.setup([
            {
                duration: 4,
                audioSource: "Upload",
                clipLengthFromAudio: true,
                stages: [{}],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const field = fieldByLabel("Duration (s)");
        expect(field.querySelector<HTMLInputElement>("input")?.disabled).toBe(
            true,
        );
        expect(field.classList.contains("vst-field-disabled")).toBe(true);
    });
    it("reserves the derived-duration hint row while duration is editable", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });

        const hint = fieldByLabel("Duration (s)").querySelector<HTMLElement>(
            ".vst-detail-field-hint",
        );
        expect(hint?.textContent).toBe(
            "(derived from a reference's media length)",
        );
        expect(hint?.classList).toContain("vst-detail-field-hint-hidden");
    });
    it("clamps an edited ref frame and writes it through saveClips", () => {
        h.setup([
            {
                duration: 5,
                stages: [{}],
                frameRefs: [{ source: "Base", frame: 1 }],
            },
        ]);
        setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
        jest.useFakeTimers();
        const frameRow = Array.from(
            document.querySelectorAll<HTMLElement>(
                ".vst-clip-editor .vst-detail-field",
            ),
        ).find((r) =>
            r
                .querySelector(".vst-detail-field-label")
                ?.textContent?.startsWith("Attach at Frame"),
        );
        const input = frameRow?.querySelector<HTMLInputElement>("input");
        if (!input) {
            throw new Error("frame input missing");
        }
        input.value = "9999";
        input.dispatchEvent(new Event("change", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].frameRefs[0].frame).toBe(
            121,
        );
        const refreshed = h.field("keyframes", "Attach at Frame");
        refreshed.value = "7";
        refreshed.dispatchEvent(new Event("change", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].frameRefs[0].frame).toBe(7);
    });
    it("renders the audio editor and live-applies source + flags", () => {
        h.setup([
            {
                duration: 5,
                stages: [{}, {}, {}],
                icLoras: [
                    {
                        lora: "some-lora",
                        driveSource: "ControlNet 1",
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "audio", clipIdx: 0 });
        const select =
            fieldByLabel("Audio Source").querySelector<HTMLSelectElement>(
                "select",
            );
        if (!select) {
            throw new Error("audio source select missing");
        }
        expect(Array.from(select.options).map((o) => o.value)).toContain(
            "ControlNet",
        );
        expect(
            fieldByLabel("Clip Length from Audio").querySelector("input")
                ?.disabled,
        ).toBe(true);

        const reuse = fieldByLabel(
            "Reuse Captured Stage Audio",
        ).querySelector<HTMLInputElement>("input");
        if (!reuse) {
            throw new Error("reuse checkbox missing");
        }
        reuse.checked = true;
        reuse.dispatchEvent(new Event("change", { bubbles: true }));
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].reuseAudio).toBe(true);

        select.value = "Upload";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].audioSource).toBe("Upload");
        expect(
            fieldByLabel("Clip Length from Audio").querySelector("input")
                ?.disabled,
        ).toBe(false);
    });
    it("offers the previous clip output as Base Audio after the first clip", () => {
        h.setup([
            { duration: 5, stages: [{}] },
            { duration: 5, stages: [{}] },
        ]);

        setSelection({ kind: "audio", clipIdx: 0 });
        const first = fieldByLabel("Audio Source").querySelector("select");
        expect(first?.value).toBe("Native");

        setSelection({ kind: "audio", clipIdx: 1 });
        const second = fieldByLabel("Audio Source").querySelector("select");
        expect(
            Array.from(second?.options ?? []).map((option) => ({
                value: option.value,
                label: option.textContent,
            })),
        ).toContainEqual({
            value: "Previous Clip",
            label: "Previous Clip Output",
        });
    });
    it("trims uploaded base audio with the shared modal", () => {
        h.setup([
            {
                duration: 5,
                stages: [{}],
                audioSource: "Upload",
                uploadedAudio: {
                    data: "data:audio/wav;base64,AAAA",
                    fileName: "voice.wav",
                },
                uploadedAudioDurationSeconds: 8,
                uploadedAudioStartSeconds: 1,
                uploadedAudioLengthSeconds: 4,
            },
        ]);
        setSelection({ kind: "audio", clipIdx: 0 });

        expect(
            document.querySelector(".vst-sidebar-audio-preview")?.tagName,
        ).toBe("AUDIO");
        document
            .querySelector<HTMLButtonElement>(
                ".vst-detail-audio [data-vst-open-trim]",
            )
            ?.click();

        expect(document.querySelector(".vst-trim-modal-player")?.tagName).toBe(
            "AUDIO",
        );
        const inInput = document.querySelector<HTMLInputElement>(
            '[data-vst-trim-field="in"]',
        );
        if (!inInput) {
            throw new Error("base audio trim input missing");
        }
        inInput.value = "2";
        inInput.dispatchEvent(new Event("input", { bubbles: true }));
        document
            .querySelector<HTMLButtonElement>("[data-vst-trim-apply]")
            ?.click();

        expect(persistence.getState().clips[0]).toMatchObject({
            uploadedAudioStartSeconds: 2,
            uploadedAudioLengthSeconds: 3,
        });
        const preview = document.querySelector<HTMLAudioElement>(
            ".vst-sidebar-audio-preview",
        );
        preview?.dispatchEvent(new Event("loadedmetadata"));
        expect(preview?.currentTime).toBe(2);
    });
    it("offers captured-stage audio reuse below three active stages", () => {
        h.setup([{ duration: 5, stages: [{}, {}, { skipped: true }] }]);
        setSelection({ kind: "audio", clipIdx: 0 });
        const row = fieldByLabel("Reuse Captured Stage Audio");
        const reuse = row.querySelector<HTMLInputElement>("input");
        expect(reuse?.disabled).toBe(false);
        expect(row.textContent).toContain("second active stage");
        expect(row.textContent).toContain("third active stage");
    });
    it("adds references at unique rounded ten-percent frame intervals before wrapping", () => {
        h.setup([{ duration: 5, stages: [{}], frameRefs: [] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });

        for (let index = 0; index < 12; index++) {
            const add = document.querySelector<HTMLButtonElement>(
                '[data-testid="add-keyframes"]',
            );
            expect(add).not.toBeNull();
            add?.click();
        }

        const frames = committedClips()[0].frameRefs.map(
            (reference) => reference.frame,
        );
        expect(frames).toEqual([
            1, 13, 25, 37, 49, 61, 73, 85, 97, 109, 121, 2,
        ]);
        expect(new Set(frames).size).toBe(12);
    });
    it("restores later clips after skipping an earlier clip", () => {
        h.setup([
            { duration: 2, stages: [{}] },
            { duration: 3, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
        h.section("prompt")
            .querySelector<HTMLButtonElement>('[data-testid="toggle-clip"]')
            ?.click();
        expect(committedClips().map((clip) => clip.skipped)).toEqual([
            false,
            true,
            true,
        ]);
        setSelection({ kind: "clip", clipIdx: 2, stageIdx: 0 });
        h.section("prompt")
            .querySelector<HTMLButtonElement>('[data-testid="toggle-clip"]')
            ?.click();
        expect(committedClips().map((clip) => clip.skipped)).toEqual([
            false,
            false,
            false,
        ]);
        expect(h.field("prompt", "Duration (s)").value).toBe("4");
    });
    it("restores earlier stages when a skipped later stage is enabled", () => {
        h.setup([{ duration: 4, stages: [{}, {}, {}] }]);
        h.item("stages", 1)
            .querySelector<HTMLInputElement>('[data-testid="toggle-stage"]')
            ?.click();
        expect(
            committedClips()[0].stages.map((stage) => stage.skipped),
        ).toEqual([false, true, true]);
        h.item("stages", 2)
            .querySelector<HTMLInputElement>('[data-testid="toggle-stage"]')
            ?.click();
        expect(
            committedClips()[0].stages.map((stage) => stage.skipped),
        ).toEqual([false, false, false]);
    });
    it("starts source footage with editable refinement controls", () => {
        h.setup([
            {
                duration: 4,
                stages: [{ control: 0.5, upscale: 2 }, {}],
                initVideo: initVideoFixture({
                    durationSeconds: 4,
                    lengthSeconds: 4,
                }),
            },
        ]);
        expect(h.item("stages", 0).textContent).toContain(
            "starts from the source footage",
        );
        expect(
            h.field("stages", "Control", "input.auto-slider-number", 0).value,
        ).toBe("0.5");
        expect(
            h.field("stages", "Upscale", "input.auto-slider-number", 0).value,
        ).toBe("2");
        expect(
            h.field<HTMLSelectElement>("stages", "Upscale Method", "select", 0)
                .disabled,
        ).toBe(false);
        expect(
            h.field("stages", "Control", "input.auto-slider-number", 1)
                .disabled,
        ).toBe(false);
    });
    it("adds an editable first stage to a source-only clip", () => {
        h.setup([
            {
                duration: 4,
                stages: [],
                initVideo: initVideoFixture({
                    durationSeconds: 4,
                    lengthSeconds: 4,
                }),
            },
        ]);
        h.section("stages")
            .querySelector<HTMLButtonElement>('[data-testid="add-stages"]')
            ?.click();
        expect(committedClips()[0].stages).toHaveLength(1);
        expect(h.item("stages", 0).textContent).toContain("Stage S0");
        expect(
            h.field<HTMLSelectElement>("stages", "Model", "select", 0).value,
        ).toBe(committedClips()[0].stages[0].model);
    });
    it("keeps all keyframe editors editable while switching repeated selections", () => {
        h.setup([
            {
                duration: 5,
                stages: [{}],
                frameRefs: [
                    { source: "Base", frame: 1 },
                    { source: "Refiner", frame: 8 },
                ],
            },
        ]);
        setSelection({ kind: "ref", clipIdx: 0, refIdx: 1 });
        jest.useFakeTimers();
        const input = h.field("keyframes", "Attach at Frame", "input", 1);
        input.value = "12";
        input.dispatchEvent(new Event("change", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(committedClips()[0].frameRefs.map((ref) => ref.frame)).toEqual([
            1, 12,
        ]);
        expect(h.field("keyframes", "Attach at Frame", "input", 0).value).toBe(
            "1",
        );
    });
    it("selects the boundary's incoming clip when dropping references", async () => {
        h.setup([
            { duration: 4, stages: [{}], boundaryOut: "continue" },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "boundary-ref", leftClipIdx: 0 });
        const drop = new Event("drop", { bubbles: true, cancelable: true });
        Object.defineProperty(drop, "dataTransfer", {
            value: {
                files: [
                    new File(["image"], "incoming.png", { type: "image/png" }),
                ],
                types: ["Files"],
                dropEffect: "none",
                getData: () => "",
            },
        });
        h.section("references")
            .querySelector<HTMLButtonElement>('[data-testid="add-references"]')
            ?.dispatchEvent(drop);
        await new Promise((resolve) => setTimeout(resolve, 20));
        expect(committedClips()[1].references[0].uploadedMedia?.fileName).toBe(
            "incoming.png",
        );
        expect(getSelection()).toEqual({
            kind: "clip-ref",
            clipIdx: 1,
            referenceIdx: 0,
        });
    });
    it.each([
        0, 1,
    ])("keeps source video disabled for audio-only flow clip %i", (clipIdx) => {
        h.setup([
            { duration: 4, stages: [{}], audioOnly: true },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "clip", clipIdx, stageIdx: 0 });
        const controls = Array.from(
            h
                .section("source-video")
                .querySelectorAll<
                    HTMLInputElement | HTMLButtonElement | HTMLSelectElement
                >("input, button, select"),
        );
        expect(controls.length).toBeGreaterThan(0);
        expect(controls.every((control) => control.disabled)).toBe(true);
    });
    it("keeps video authoring available after the audio-only source pair", () => {
        h.setup([
            { duration: 4, stages: [{}], audioOnly: true },
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "clip", clipIdx: 2, stageIdx: 0 });
        expect(
            h.field<HTMLSelectElement>("source-video", "Video source", "select")
                .disabled,
        ).toBe(false);
        expect(
            h
                .section("stages")
                .querySelector<HTMLButtonElement>('[data-testid="add-stages"]')
                ?.disabled,
        ).toBe(false);
    });
    it("places keyframe help before its count-from-end label", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}],
                frameRefs: [{ source: "Upload", frame: 0 }],
            },
        ]);
        const field = h.field("keyframes", "Count from clip end");
        const label = field
            .closest(".vst-detail-field-check")
            ?.querySelector("label");
        expect(label?.firstElementChild?.textContent).toBe("?");
        expect(label?.lastElementChild?.textContent).toBe(
            "Count from clip end",
        );
    });
    it("explains source footage above its upload controls", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        const section = h.section("source-video");
        expect(
            section.querySelector(".vst-detail-field-hint")?.textContent,
        ).toBe(
            "Use an existing video file as this clip instead of generating it.",
        );
        expect(
            section.querySelector<HTMLInputElement>('input[type="file"]')
                ?.accept,
        ).toContain("video");
    });
});
