import { describe, expect, it } from "@jest/globals";
import { minimalClip, minimalStage } from "./__test_helpers__/clipFixtures";
import {
    applyRefineToClipZero,
    countActiveStagesInMetadataClip0,
    hasRefinementWorkToDo,
} from "./refineVideoButton";
import type { AuthoringDocument, Clip } from "./types";

const makeConfig = (clips: Clip[]): AuthoringDocument => ({
    width: 512,
    height: 512,
    fps: 24,
    dimsExplicit: false,
    clips,
});

describe("hasRefinementWorkToDo", () => {
    it("returns false when VideoStages group is disabled", () => {
        const config = makeConfig([
            minimalClip({ stages: [minimalStage(), minimalStage()] }),
        ]);
        expect(hasRefinementWorkToDo(config, false, 1)).toBe(false);
    });

    it("returns false when there are no clips", () => {
        const config = makeConfig([]);
        expect(hasRefinementWorkToDo(config, true, 1)).toBe(false);
    });

    it("returns false when clip 0 is skipped", () => {
        const config = makeConfig([
            minimalClip({
                skipped: true,
                stages: [minimalStage(), minimalStage()],
            }),
        ]);
        expect(hasRefinementWorkToDo(config, true, 1)).toBe(false);
    });

    it("returns false when clip 0 has only stage 0 (skip=1)", () => {
        const config = makeConfig([minimalClip({ stages: [minimalStage()] })]);
        expect(hasRefinementWorkToDo(config, true, 1)).toBe(false);
    });

    it("returns false when stage 1 is skipped (skip=1)", () => {
        const config = makeConfig([
            minimalClip({
                stages: [minimalStage(), minimalStage({ skipped: true })],
            }),
        ]);
        expect(hasRefinementWorkToDo(config, true, 1)).toBe(false);
    });

    it("returns true with two active stages when skip=1", () => {
        const config = makeConfig([
            minimalClip({ stages: [minimalStage(), minimalStage()] }),
        ]);
        expect(hasRefinementWorkToDo(config, true, 1)).toBe(true);
    });

    it("returns false when a middle skip truncates every later stage (skip=1)", () => {
        const config = makeConfig([
            minimalClip({
                stages: [
                    minimalStage(),
                    minimalStage({ skipped: true }),
                    minimalStage(),
                ],
            }),
        ]);
        expect(hasRefinementWorkToDo(config, true, 1)).toBe(false);
    });

    it("returns false when active stages equal skipCount (skip=2)", () => {
        const config = makeConfig([
            minimalClip({ stages: [minimalStage(), minimalStage()] }),
        ]);
        expect(hasRefinementWorkToDo(config, true, 2)).toBe(false);
    });

    it("returns true when active stages exceed skipCount (skip=2)", () => {
        const config = makeConfig([
            minimalClip({
                stages: [minimalStage(), minimalStage(), minimalStage()],
            }),
        ]);
        expect(hasRefinementWorkToDo(config, true, 2)).toBe(true);
    });

    it("does not count stages after the first skip marker (skip=2)", () => {
        const config = makeConfig([
            minimalClip({
                stages: [
                    minimalStage(),
                    minimalStage({ skipped: true }),
                    minimalStage(),
                    minimalStage(),
                ],
            }),
        ]);
        expect(hasRefinementWorkToDo(config, true, 2)).toBe(false);
    });
});

describe("countActiveStagesInMetadataClip0", () => {
    it("returns 0 for malformed JSON", () => {
        expect(countActiveStagesInMetadataClip0("not json")).toBe(0);
    });

    it("returns 0 when the parsed value is not an object", () => {
        expect(countActiveStagesInMetadataClip0("42")).toBe(0);
        expect(countActiveStagesInMetadataClip0('"hello"')).toBe(0);
        expect(countActiveStagesInMetadataClip0("null")).toBe(0);
    });

    it("returns 0 when clips is missing or not an array", () => {
        expect(countActiveStagesInMetadataClip0("{}")).toBe(0);
        expect(
            countActiveStagesInMetadataClip0('{"clips": "not an array"}'),
        ).toBe(0);
    });

    it("returns 0 when clips is empty", () => {
        expect(countActiveStagesInMetadataClip0('{"clips": []}')).toBe(0);
    });

    it("returns 0 when clip 0 is skipped", () => {
        const json = JSON.stringify({
            clips: [{ skipped: true, stages: [{}, {}] }],
        });
        expect(countActiveStagesInMetadataClip0(json)).toBe(0);
    });

    it("returns 0 when clip 0 has no stages array", () => {
        const json = JSON.stringify({ clips: [{ skipped: false }] });
        expect(countActiveStagesInMetadataClip0(json)).toBe(0);
    });

    it("counts only the stage prefix before the first skip marker", () => {
        const json = JSON.stringify({
            clips: [
                {
                    skipped: false,
                    stages: [
                        { skipped: false },
                        { skipped: true },
                        { skipped: false },
                    ],
                },
                {
                    skipped: false,
                    stages: [{ skipped: false }, { skipped: false }],
                },
            ],
        });
        expect(countActiveStagesInMetadataClip0(json)).toBe(1);
    });

    it("treats stages without an explicit skipped flag as active", () => {
        const json = JSON.stringify({
            clips: [{ stages: [{}, {}, {}] }],
        });
        expect(countActiveStagesInMetadataClip0(json)).toBe(3);
    });
});

describe("applyRefineToClipZero", () => {
    it("installs the probed video as the clip source", () => {
        const clip = minimalClip({ stages: [minimalStage(), minimalStage()] });
        applyRefineToClipZero(
            clip,
            "data:video/mp4;base64,AA==",
            { durationSeconds: 3.5, fps: 24 },
            1,
        );
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
        applyRefineToClipZero(clip, "data:video/mp4;base64,AA==", null, 1);
        expect(clip.initVideo?.lengthSeconds).toBe(7);
    });

    it("passes through exactly the already-generated stage prefix", () => {
        const clip = minimalClip({
            stages: [minimalStage(), minimalStage(), minimalStage()],
        });
        applyRefineToClipZero(
            clip,
            "data:video/mp4;base64,AA==",
            { durationSeconds: 2, fps: 24 },
            2,
        );
        expect(clip.stages.map((stage) => stage.control)).toEqual([0, 0, 1]);
    });

    it("stops at the first skipped stage rather than counting past it", () => {
        const clip = minimalClip({
            stages: [
                minimalStage(),
                minimalStage({ skipped: true }),
                minimalStage(),
            ],
        });
        applyRefineToClipZero(
            clip,
            "data:video/mp4;base64,AA==",
            { durationSeconds: 2, fps: 24 },
            3,
        );
        expect(clip.stages.map((stage) => stage.control)).toEqual([0, 1, 1]);
    });
});
