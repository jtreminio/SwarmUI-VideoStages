import { afterEach, describe, expect, it, jest } from "@jest/globals";
import {
    initVideoFixture,
    minimalClip,
    minimalStage,
} from "./__test_helpers__/clipFixtures";
import {
    mountCheckbox,
    mountPromptBox,
    mountVideoStagesData,
} from "./__test_helpers__/dom";
import { MEDIA_SOURCE_PREVIOUS_CLIP } from "./generatedMediaSource";
import { setVideoStagesHostBridgeForTests } from "./host";
import { createDefaultVideoStagesHostBridge } from "./host/defaultVideoStagesHostBridge";
import { __resetPersistenceForTests } from "./persistence/repository";
import {
    applyRefineToClip,
    hasRefinementWorkToDo,
    refineNeedsExtraStageMessage,
    refineVideoButton,
} from "./refineVideoButton";
import type { AuthoringDocument, Clip } from "./types";

const makeConfig = (clips: Clip[]): AuthoringDocument => ({
    width: 512,
    height: 512,
    fps: 24,
    dimsExplicit: false,
    clips,
});

const captureRefinePayload = (
    sourceVideostages: string,
    sourceDimensions?: { width: number; height: number },
): {
    select: (src: string) => void;
    generated: Promise<Record<string, unknown>>;
} => {
    const callbacks: ((src: string) => void)[] = [];
    let resolveGenerated: ((value: Record<string, unknown>) => void) | null =
        null;
    const generated = new Promise<Record<string, unknown>>((resolve) => {
        resolveGenerated = resolve;
    });
    const base = createDefaultVideoStagesHostBridge();
    setVideoStagesHostBridgeForTests({
        ...base,
        registerRefineVideoButton: (callback) => {
            callbacks.push(callback);
        },
        getMediaMetadata: async () =>
            JSON.stringify({
                sui_image_params: { videostages: sourceVideostages },
            }),
        interpretMediaMetadata: (metadata) => metadata,
        toDataUrl: async () => "data:video/mp4;base64,AA==",
        createInitVideoElement: () => {
            const video = document.createElement("video");
            video.pause = jest.fn();
            video.load = jest.fn();
            if (sourceDimensions) {
                Object.defineProperties(video, {
                    duration: { value: 5.4 },
                    videoWidth: { value: sourceDimensions.width },
                    videoHeight: { value: sourceDimensions.height },
                });
                queueMicrotask(() =>
                    video.dispatchEvent(new Event("loadedmetadata")),
                );
            } else {
                queueMicrotask(() => video.dispatchEvent(new Event("error")));
            }
            return video;
        },
        showError: (message) => resolveGenerated?.({ error: message }),
        generate: (overrides) => resolveGenerated?.(overrides),
    });
    refineVideoButton();
    return {
        select: (src) => callbacks[0](src),
        generated,
    };
};

afterEach(() => {
    __resetPersistenceForTests();
    setVideoStagesHostBridgeForTests(null);
    document.body.innerHTML = "";
});

describe("hasRefinementWorkToDo", () => {
    it("explains where more refinement work is needed", () => {
        expect(refineNeedsExtraStageMessage()).toContain(
            "another stage or clip",
        );
        expect(refineNeedsExtraStageMessage()).toContain(
            "last completed stage",
        );
    });

    it("returns false when VideoStages group is disabled", () => {
        const config = makeConfig([
            minimalClip({ stages: [minimalStage(), minimalStage()] }),
        ]);
        expect(hasRefinementWorkToDo(config, false)).toBe(false);
    });

    it("returns false when there are no clips", () => {
        const config = makeConfig([]);
        expect(hasRefinementWorkToDo(config, true)).toBe(false);
    });

    it("returns false when clip 0 is skipped", () => {
        const config = makeConfig([
            minimalClip({
                skipped: true,
                stages: [minimalStage(), minimalStage()],
            }),
        ]);
        expect(hasRefinementWorkToDo(config, true)).toBe(false);
    });

    it("returns false when clip 0 has only stage 0", () => {
        const config = makeConfig([minimalClip({ stages: [minimalStage()] })]);
        expect(hasRefinementWorkToDo(config, true)).toBe(false);
    });

    it("uses another clip when clip 0 has only stage 0", () => {
        const config = makeConfig([
            minimalClip({ stages: [minimalStage()] }),
            minimalClip({
                initVideo: initVideoFixture({
                    source: MEDIA_SOURCE_PREVIOUS_CLIP,
                    data: "",
                    fileName: null,
                }),
                stages: [minimalStage()],
            }),
        ]);
        expect(hasRefinementWorkToDo(config, true)).toBe(true);
    });

    it("uses a defined stage 1 even when it is inactive", () => {
        const config = makeConfig([
            minimalClip({
                stages: [minimalStage(), minimalStage({ skipped: true })],
            }),
        ]);
        expect(hasRefinementWorkToDo(config, true)).toBe(true);
    });

    it("uses stage 1 when it is active", () => {
        const config = makeConfig([
            minimalClip({ stages: [minimalStage(), minimalStage()] }),
        ]);
        expect(hasRefinementWorkToDo(config, true)).toBe(true);
    });

    it("ignores whether later defined stages are active", () => {
        const config = makeConfig([
            minimalClip({
                stages: [
                    minimalStage(),
                    minimalStage(),
                    minimalStage({ skipped: true }),
                ],
            }),
        ]);
        expect(hasRefinementWorkToDo(config, true)).toBe(true);
    });

    it("accepts stage 1 when the whole stage suffix is inactive", () => {
        const config = makeConfig([
            minimalClip({
                stages: [
                    minimalStage({ skipped: true }),
                    minimalStage({ skipped: true }),
                ],
            }),
        ]);
        expect(hasRefinementWorkToDo(config, true)).toBe(true);
    });
});

describe("applyRefineToClip", () => {
    it("installs the probed video as the clip source", () => {
        const clip = minimalClip({ stages: [minimalStage(), minimalStage()] });
        applyRefineToClip(
            clip,
            "data:video/mp4;base64,AA==",
            {
                durationSeconds: 3.5,
                fps: 24,
                width: 640,
                height: 960,
            },
            1,
        );
        expect(clip.initVideo).toEqual({
            source: "Upload",
            data: "data:video/mp4;base64,AA==",
            fileName: "refine-source",
            fps: 24,
            durationSeconds: 3.5,
            startSeconds: 0,
            lengthSeconds: 3.5,
        });
        expect(clip.duration).toBe(3.5);
    });

    it("uses the source video's baked soundtrack instead of regenerating selected audio", () => {
        const clip = minimalClip({
            audioSource: "audio0",
            saveAudioTrack: true,
            clipLengthFromAudio: true,
            uploadedAudio: {
                data: "data:audio/wav;base64,QQ==",
                fileName: "old.wav",
            },
            uploadedAudioDurationSeconds: 9,
            uploadedAudioStartSeconds: 1,
            uploadedAudioLengthSeconds: 4,
            stages: [minimalStage(), minimalStage()],
        });

        applyRefineToClip(clip, "data:video/mp4;base64,AA==", null, 1);

        expect(clip).toMatchObject({
            audioSource: "Upload",
            saveAudioTrack: false,
            clipLengthFromAudio: false,
            uploadedAudio: null,
            uploadedAudioDurationSeconds: 0,
            uploadedAudioStartSeconds: 0,
            uploadedAudioLengthSeconds: 0,
        });
    });

    it("falls back to the authored clip duration when the probe reports none", () => {
        const clip = minimalClip({ duration: 7, stages: [minimalStage()] });
        applyRefineToClip(clip, "data:video/mp4;base64,AA==", null, 1);
        expect(clip.initVideo?.lengthSeconds).toBe(7);
    });

    it("passes through stage 0 without changing later controls", () => {
        const clip = minimalClip({
            stages: [minimalStage(), minimalStage(), minimalStage()],
        });
        applyRefineToClip(
            clip,
            "data:video/mp4;base64,AA==",
            {
                durationSeconds: 2,
                fps: 24,
                width: 640,
                height: 960,
            },
            1,
        );
        expect(clip.stages.map((stage) => stage.control)).toEqual([0, 1, 1]);
    });

    it("activates stage 1 when it was inactive", () => {
        const clip = minimalClip({
            stages: [minimalStage(), minimalStage({ skipped: true })],
        });
        applyRefineToClip(
            clip,
            "data:video/mp4;base64,AA==",
            {
                durationSeconds: 2,
                fps: 24,
                width: 640,
                height: 960,
            },
            1,
        );
        expect(clip.stages.map((stage) => stage.skipped)).toEqual([
            false,
            false,
        ]);
        expect(clip.stages.map((stage) => stage.control)).toEqual([0, 1]);
    });

    it("activates the remaining stage suffix from stage 1", () => {
        const clip = minimalClip({
            stages: [
                minimalStage(),
                minimalStage({ skipped: true }),
                minimalStage({ skipped: true }),
            ],
        });
        applyRefineToClip(clip, "data:video/mp4;base64,AA==", null, 1);
        expect(clip.stages.map((stage) => stage.skipped)).toEqual([
            false,
            false,
            false,
        ]);
    });

    it("preserves stage 1 control", () => {
        const clip = minimalClip({
            stages: [minimalStage(), minimalStage({ control: 0.4 })],
        });
        applyRefineToClip(clip, "data:video/mp4;base64,AA==", null, 1);
        expect(clip.stages.map((stage) => stage.control)).toEqual([0, 0.4]);
    });

    it("does not turn later stages into passthroughs", () => {
        const clip = minimalClip({
            stages: [
                minimalStage({ control: 0.8 }),
                minimalStage({ control: 0.4 }),
                minimalStage({ control: 0.7 }),
            ],
        });
        applyRefineToClip(clip, "data:video/mp4;base64,AA==", null, 1);
        expect(clip.stages.map((stage) => stage.control)).toEqual([
            0, 0.4, 0.7,
        ]);
    });

    it("handles a document that only defines stage 0", () => {
        const clip = minimalClip({ stages: [minimalStage()] });
        applyRefineToClip(clip, "data:video/mp4;base64,AA==", null, 1);
        expect(clip.stages).toMatchObject([{ skipped: false, control: 0 }]);
    });
});

describe("refineVideoButton", () => {
    it("puts the Comfy action below WhatTheDuck instead of in Generate", () => {
        document.body.innerHTML = `
            <div id="comfy_workflow_buttons">
                <div id="comfy_quickload" class="comfy_quickload wtd-comfy-save-row">
                    <button id="wtd_comfy_save_workflow_button">Import & Save To Server</button>
                    <select></select>
                </div>
            </div>`;
        const base = createDefaultVideoStagesHostBridge();
        const registerRefineVideoButton = jest.fn();
        setVideoStagesHostBridgeForTests({
            ...base,
            registerRefineVideoButton,
        });

        refineVideoButton();

        const whatTheDuckRow = document.getElementById("comfy_quickload");
        const refineButton = document.getElementById(
            "video_stages_refine_to_comfy_button",
        );
        expect(refineButton).not.toBeNull();
        expect(whatTheDuckRow?.nextElementSibling?.contains(refineButton)).toBe(
            true,
        );
        expect(registerRefineVideoButton).toHaveBeenCalledTimes(1);
    });

    it("does not add the Comfy action without WhatTheDuck", () => {
        document.body.innerHTML = '<div id="comfy_workflow_buttons"></div>';
        const base = createDefaultVideoStagesHostBridge();
        setVideoStagesHostBridgeForTests({
            ...base,
            registerRefineVideoButton: jest.fn(),
        });

        refineVideoButton();

        expect(
            document.getElementById("video_stages_refine_to_comfy_button"),
        ).toBeNull();
    });

    it("sends the refine payload to ComfyUI without starting generation", async () => {
        mountVideoStagesData(
            makeConfig([
                minimalClip({
                    stages: [minimalStage(), minimalStage({ control: 0.5 })],
                }),
            ]),
        );
        mountPromptBox("current panel prompt");
        mountCheckbox("input_group_content_videostages_toggle", {
            checked: true,
        });

        document.body.insertAdjacentHTML(
            "beforeend",
            `<video id="current_image_img" data-src="source.mp4"></video>
             <div id="comfy_workflow_buttons">
                 <div id="comfy_quickload" class="comfy_quickload wtd-comfy-save-row">
                     <button id="wtd_comfy_save_workflow_button">Import & Save To Server</button>
                 </div>
             </div>`,
        );
        const generate = jest.fn();
        let resolveSent: ((value: Record<string, unknown>) => void) | null =
            null;
        const sent = new Promise<Record<string, unknown>>((resolve) => {
            resolveSent = resolve;
        });
        const base = createDefaultVideoStagesHostBridge();
        setVideoStagesHostBridgeForTests({
            ...base,
            registerRefineVideoButton: jest.fn(),
            getMediaMetadata: async () =>
                JSON.stringify({
                    sui_image_params: {
                        prompt: "selected result prompt",
                        seed: 123,
                    },
                }),
            interpretMediaMetadata: (metadata: string) => metadata,
            toDataUrl: async () => "data:video/mp4;base64,AA==",
            createInitVideoElement: () => {
                const video = document.createElement("video");
                video.pause = jest.fn();
                video.load = jest.fn();
                queueMicrotask(() => video.dispatchEvent(new Event("error")));
                return video;
            },
            generate,
            sendToComfyUiAndSave: (overrides: Record<string, unknown>) => {
                resolveSent?.(overrides);
                return Promise.resolve();
            },
        });

        refineVideoButton();

        document.getElementById("video_stages_refine_to_comfy_button")?.click();
        const overrides = await sent;
        expect(generate).not.toHaveBeenCalled();
        expect(overrides).toMatchObject({
            images: 1,
            prompt: "selected result prompt",
            seed: 123,
        });
        const refinedDocument = JSON.parse(
            String(overrides.videostages),
        ) as AuthoringDocument;
        expect(refinedDocument.clips[0]).toMatchObject({
            initVideo: {
                source: "Upload",
                data: "data:video/mp4;base64,AA==",
                fileName: "refine-source",
            },
            stages: [
                { skipped: false, control: 0 },
                { skipped: false, control: 0.5 },
            ],
        });
    });

    it("promotes the next clip instead of joining the rendered source again", async () => {
        mountVideoStagesData(
            makeConfig([
                minimalClip({
                    id: "rendered-clip",
                    stages: [minimalStage()],
                }),
                minimalClip({
                    id: "refinement-clip",
                    initVideo: initVideoFixture({
                        source: MEDIA_SOURCE_PREVIOUS_CLIP,
                        data: "",
                        fileName: null,
                    }),
                    stages: [minimalStage({ control: 0.5, upscale: 2 })],
                }),
                minimalClip({
                    id: "later-clip",
                    skipped: true,
                    stages: [minimalStage()],
                }),
            ]),
        );
        mountPromptBox("current panel prompt");
        mountCheckbox("input_group_content_videostages_toggle", {
            checked: true,
        });

        const callbacks: ((src: string) => void)[] = [];
        let resolveGenerated:
            | ((value: Record<string, unknown>) => void)
            | null = null;
        const generated = new Promise<Record<string, unknown>>((resolve) => {
            resolveGenerated = resolve;
        });
        const base = createDefaultVideoStagesHostBridge();
        setVideoStagesHostBridgeForTests({
            ...base,
            registerRefineVideoButton: (callback) => {
                callbacks.push(callback);
            },
            getMediaMetadata: async () =>
                JSON.stringify({ sui_image_params: {} }),
            interpretMediaMetadata: (metadata) => metadata,
            toDataUrl: async () => "data:video/mp4;base64,AA==",
            createInitVideoElement: () => {
                const video = document.createElement("video");
                video.pause = jest.fn();
                video.load = jest.fn();
                queueMicrotask(() => video.dispatchEvent(new Event("error")));
                return video;
            },
            showError: (message) => resolveGenerated?.({ error: message }),
            generate: (overrides) => resolveGenerated?.(overrides),
        });

        refineVideoButton();
        callbacks[0]("source.mp4");

        const overrides = await generated;
        const refinedDocument = JSON.parse(
            String(overrides.videostages),
        ) as AuthoringDocument;
        expect(refinedDocument.clips).toHaveLength(2);
        expect(refinedDocument.clips[0]).toMatchObject({
            id: "refinement-clip",
            initVideo: {
                source: "Upload",
                data: "data:video/mp4;base64,AA==",
                fileName: "refine-source",
            },
            stages: [{ skipped: false, control: 0.5, upscale: 2 }],
        });
        expect(refinedDocument.clips[1]).toMatchObject({
            id: "later-clip",
            skipped: true,
        });
    });

    it("continues after the source video's last completed clip and stage", async () => {
        mountVideoStagesData(
            makeConfig([
                minimalClip({
                    id: "clip-0",
                    stages: [minimalStage({ id: "stage-0-0" })],
                }),
                minimalClip({
                    id: "clip-1",
                    stages: [
                        minimalStage({ id: "stage-1-0", upscale: 2 }),
                        minimalStage({ id: "stage-1-1", upscale: 1.5 }),
                        minimalStage({ id: "stage-1-2", control: 0.6 }),
                    ],
                }),
                minimalClip({
                    id: "clip-2",
                    skipped: false,
                    stages: [minimalStage({ id: "stage-2-0" })],
                }),
            ]),
        );
        mountPromptBox("current panel prompt");
        mountCheckbox("input_group_content_videostages_toggle", {
            checked: true,
        });

        const sourceVideostages = JSON.stringify({
            schemaVersion: 9,
            clips: [
                {
                    id: "clip-0",
                    skipped: false,
                    stages: [{ id: "stage-0-0", skipped: false }],
                },
                {
                    id: "clip-1",
                    skipped: false,
                    stages: [
                        { id: "stage-1-0", skipped: false },
                        { id: "stage-1-1", skipped: false },
                        { id: "stage-1-2", skipped: true },
                    ],
                },
                {
                    id: "clip-2",
                    skipped: true,
                    stages: [{ id: "stage-2-0", skipped: false }],
                },
            ],
        });
        const { select, generated } = captureRefinePayload(sourceVideostages);
        select("source.mp4");

        const overrides = await generated;
        const refinedDocument = JSON.parse(
            String(overrides.videostages),
        ) as AuthoringDocument;
        expect(refinedDocument.clips).toHaveLength(2);
        expect(refinedDocument.clips[0]).toMatchObject({
            id: "clip-1",
            initVideo: {
                source: "Upload",
                data: "data:video/mp4;base64,AA==",
                fileName: "refine-source",
            },
            stages: [
                {
                    id: "stage-1-0",
                    skipped: false,
                    control: 0,
                    upscale: 1,
                },
                {
                    id: "stage-1-1",
                    skipped: false,
                    control: 0,
                    upscale: 1,
                },
                { id: "stage-1-2", skipped: false, control: 0.6 },
            ],
        });
        expect(refinedDocument.clips[1]).toMatchObject({
            id: "clip-2",
            skipped: false,
        });
    });

    it("promotes the clip after the source video's last completed clip", async () => {
        mountVideoStagesData(
            makeConfig([
                minimalClip({
                    id: "clip-0",
                    stages: [minimalStage({ id: "stage-0-0" })],
                }),
                minimalClip({
                    id: "clip-1",
                    stages: [minimalStage({ id: "stage-1-0" })],
                }),
                minimalClip({
                    id: "clip-2",
                    skipped: true,
                    stages: [minimalStage({ id: "stage-2-0", control: 0.7 })],
                }),
            ]),
        );
        mountPromptBox("current panel prompt");
        mountCheckbox("input_group_content_videostages_toggle", {
            checked: true,
        });

        const sourceVideostages = JSON.stringify({
            schemaVersion: 9,
            clips: [
                {
                    id: "clip-1",
                    skipped: false,
                    stages: [{ id: "stage-1-0", skipped: false }],
                },
                {
                    id: "clip-2",
                    skipped: true,
                    stages: [{ id: "stage-2-0", skipped: false }],
                },
            ],
        });
        const { select, generated } = captureRefinePayload(sourceVideostages, {
            width: 1024,
            height: 1664,
        });
        select("source.mp4");

        const overrides = await generated;
        const refinedDocument = JSON.parse(
            String(overrides.videostages),
        ) as AuthoringDocument;
        expect(overrides).not.toHaveProperty("width");
        expect(overrides).not.toHaveProperty("height");
        expect(refinedDocument).toMatchObject({ width: 1024, height: 1664 });
        expect(refinedDocument.clips).toHaveLength(1);
        expect(refinedDocument.clips[0]).toMatchObject({
            id: "clip-2",
            skipped: false,
            initVideo: {
                source: "Upload",
                data: "data:video/mp4;base64,AA==",
                fileName: "refine-source",
            },
            stages: [{ id: "stage-2-0", skipped: false, control: 0.5 }],
        });
    });

    it("reports when the source video completed the last defined stage", async () => {
        mountVideoStagesData(
            makeConfig([
                minimalClip({
                    stages: [minimalStage(), minimalStage()],
                }),
            ]),
        );
        mountPromptBox("current panel prompt");
        mountCheckbox("input_group_content_videostages_toggle", {
            checked: true,
        });

        const sourceVideostages = JSON.stringify({
            clips: [
                {
                    stages: [{ skipped: false }, { skipped: false }],
                },
            ],
        });
        const { select, generated } = captureRefinePayload(sourceVideostages);
        select("source.mp4");

        await expect(generated).resolves.toEqual({
            error: refineNeedsExtraStageMessage(),
        });
    });

    it("rejects source metadata that does not match the current timeline", async () => {
        jest.spyOn(console, "error").mockImplementation(() => undefined);
        mountVideoStagesData(
            makeConfig([
                minimalClip({
                    id: "current-clip",
                    stages: [
                        minimalStage({ id: "current-stage-0" }),
                        minimalStage({ id: "current-stage-1" }),
                    ],
                }),
            ]),
        );
        mountPromptBox("current panel prompt");
        mountCheckbox("input_group_content_videostages_toggle", {
            checked: true,
        });

        const sourceVideostages = JSON.stringify({
            clips: [
                {
                    id: "source-clip",
                    stages: [{ id: "source-stage", skipped: false }],
                },
            ],
        });
        const { select, generated } = captureRefinePayload(sourceVideostages);
        select("source.mp4");

        await expect(generated).resolves.toEqual({
            error: expect.stringContaining(
                "does not match the current timeline",
            ),
        });
    });

    it.each([
        [
            "one active source stage",
            JSON.stringify({
                clips: [{ stages: [{ skipped: false }] }],
            }),
        ],
        ["malformed source stages", "not json"],
        ["no source stages", undefined],
    ])("uses stage 1 with %s", async (_label, sourceVideostages) => {
        mountVideoStagesData(
            makeConfig([
                minimalClip({
                    audioSource: "audio0",
                    clipLengthFromAudio: true,
                    stages: [minimalStage(), minimalStage()],
                }),
            ]),
        );
        mountPromptBox("current panel prompt");
        mountCheckbox("input_group_content_videostages_toggle", {
            checked: true,
        });

        const callbacks: ((src: string) => void)[] = [];
        let resolveGenerated:
            | ((value: Record<string, unknown>) => void)
            | null = null;
        const generated = new Promise<Record<string, unknown>>((resolve) => {
            resolveGenerated = resolve;
        });
        const sourceMetadata = {
            sui_image_params: {
                prompt: "final parsed prompt with blue bird",
                negativeprompt: "final parsed negative prompt",
                seed: 123,
                textaudiobpm: 146,
                textaudiokeyscale: "Ab major",
                textaudiosigmashift: 6,
                textaudiostyle: "Female voice, eurodance",
                ...(sourceVideostages === undefined
                    ? {}
                    : { videostages: sourceVideostages }),
            },
            sui_extra_data: {
                original_prompt:
                    "<setvar:animal=bird>" +
                    "<param[text2audio bpm]:<random:120-180>>" +
                    "<param[text2audio keyscale]:<wc:music/key_scale>>" +
                    "<param[text2audio sigma shift]:6>" +
                    "<param[text2audio style]:Female voice, eurodance>" +
                    "<wildcard:colors> <getvar:animal>",
                original_negativeprompt: "<random:blur|noise>",
                used_wildcards: ["colors"],
                prompt_variables: { animal: "bird" },
            },
        };
        const base = createDefaultVideoStagesHostBridge();
        setVideoStagesHostBridgeForTests({
            ...base,
            registerRefineVideoButton: (callback) => {
                callbacks.push(callback);
            },
            getMediaMetadata: async () => JSON.stringify(sourceMetadata),
            interpretMediaMetadata: (metadata) => metadata,
            toDataUrl: async () => "data:video/mp4;base64,AA==",
            createInitVideoElement: () => {
                const video = document.createElement("video");
                video.pause = jest.fn();
                video.load = jest.fn();
                queueMicrotask(() => video.dispatchEvent(new Event("error")));
                return video;
            },
            showError: (message) => resolveGenerated?.({ error: message }),
            generate: (overrides) => resolveGenerated?.(overrides),
        });

        refineVideoButton();
        expect(callbacks).toHaveLength(1);
        callbacks[0]("source.mp4");

        const overrides = await generated;
        expect(overrides).toMatchObject({
            prompt: "final parsed prompt with blue bird",
            negativeprompt: "final parsed negative prompt",
            seed: 123,
            acestepfunmodel: null,
            textaudiobpm: 146,
            textaudiokeyscale: "Ab major",
            textaudiosigmashift: 6,
            textaudiostyle: "Female voice, eurodance",
            extra_metadata: sourceMetadata.sui_extra_data,
        });
        const refinedDocument = JSON.parse(
            String(overrides.videostages),
        ) as AuthoringDocument;
        expect(refinedDocument.clips[0].stages).toMatchObject([
            { skipped: false, control: 0 },
            { skipped: false, control: 1 },
        ]);
    });
});
