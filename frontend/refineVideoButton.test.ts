import { afterEach, describe, expect, it, jest } from "@jest/globals";
import { minimalClip, minimalStage } from "./__test_helpers__/clipFixtures";
import {
    mountCheckbox,
    mountPromptBox,
    mountVideoStagesData,
} from "./__test_helpers__/dom";
import { setVideoStagesHostBridgeForTests } from "./host";
import { createDefaultVideoStagesHostBridge } from "./host/defaultVideoStagesHostBridge";
import { __resetPersistenceForTests } from "./persistence/repository";
import {
    applyRefineToClipZero,
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

afterEach(() => {
    __resetPersistenceForTests();
    setVideoStagesHostBridgeForTests(null);
});

describe("hasRefinementWorkToDo", () => {
    it("only ever asks for stage 1", () => {
        expect(refineNeedsExtraStageMessage()).toContain("Stage 1 defined");
        expect(refineNeedsExtraStageMessage()).not.toContain("Stage 2");
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

describe("applyRefineToClipZero", () => {
    it("installs the probed video as the clip source", () => {
        const clip = minimalClip({ stages: [minimalStage(), minimalStage()] });
        applyRefineToClipZero(clip, "data:video/mp4;base64,AA==", {
            durationSeconds: 3.5,
            fps: 24,
        });
        expect(clip.initVideo).toEqual({
            data: "data:video/mp4;base64,AA==",
            fileName: "refine-source",
            fps: 24,
            durationSeconds: 3.5,
            startSeconds: 0,
            lengthSeconds: 3.5,
        });
    });

    it("falls back to the authored clip duration when the probe reports none", () => {
        const clip = minimalClip({ duration: 7, stages: [minimalStage()] });
        applyRefineToClipZero(clip, "data:video/mp4;base64,AA==", null);
        expect(clip.initVideo?.lengthSeconds).toBe(7);
    });

    it("passes through stage 0 and leaves later stages runnable", () => {
        const clip = minimalClip({
            stages: [minimalStage(), minimalStage(), minimalStage()],
        });
        applyRefineToClipZero(clip, "data:video/mp4;base64,AA==", {
            durationSeconds: 2,
            fps: 24,
        });
        expect(clip.stages.map((stage) => stage.control)).toEqual([0, 1, 1]);
    });

    it("activates stage 1 when it was inactive", () => {
        const clip = minimalClip({
            stages: [minimalStage(), minimalStage({ skipped: true })],
        });
        applyRefineToClipZero(clip, "data:video/mp4;base64,AA==", {
            durationSeconds: 2,
            fps: 24,
        });
        expect(clip.stages.map((stage) => stage.skipped)).toEqual([
            false,
            false,
        ]);
        expect(clip.stages.map((stage) => stage.control)).toEqual([0, 1]);
    });

    it("repairs the inactive suffix when stage 1 is selected", () => {
        const clip = minimalClip({
            stages: [
                minimalStage(),
                minimalStage({ skipped: true }),
                minimalStage({ skipped: true }),
            ],
        });
        applyRefineToClipZero(clip, "data:video/mp4;base64,AA==", null);
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
        applyRefineToClipZero(clip, "data:video/mp4;base64,AA==", null);
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
        applyRefineToClipZero(clip, "data:video/mp4;base64,AA==", null);
        expect(clip.stages.map((stage) => stage.control)).toEqual([
            0, 0.4, 0.7,
        ]);
    });

    it("handles a document that only defines stage 0", () => {
        const clip = minimalClip({ stages: [minimalStage()] });
        applyRefineToClipZero(clip, "data:video/mp4;base64,AA==", null);
        expect(clip.stages).toMatchObject([{ skipped: false, control: 0 }]);
    });
});

describe("refineVideoButton", () => {
    it.each([
        [
            "two active source stages",
            JSON.stringify({
                clips: [
                    {
                        stages: [{ skipped: false }, { skipped: false }],
                    },
                ],
            }),
        ],
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
                ...(sourceVideostages === undefined
                    ? {}
                    : { videostages: sourceVideostages }),
            },
            sui_extra_data: {
                original_prompt:
                    "<setvar:animal=bird><wildcard:colors> <getvar:animal>",
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
            getCurrentMediaMetadata: () => JSON.stringify(sourceMetadata),
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
