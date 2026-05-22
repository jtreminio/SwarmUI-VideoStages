import { describe, expect, it } from "@jest/globals";
import { minimalClip, minimalStage } from "./__test_helpers__/clipFixtures";
import {
    countActiveStagesInMetadataClip0,
    hasRefinementWorkToDo,
} from "./refineVideoButton";
import type { Clip, VideoStagesConfig } from "./types";

const makeConfig = (clips: Clip[]): VideoStagesConfig => ({
    width: 512,
    height: 512,
    fps: 24,
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

    it("returns true with three stages even if the middle one is skipped (skip=1)", () => {
        const config = makeConfig([
            minimalClip({
                stages: [
                    minimalStage(),
                    minimalStage({ skipped: true }),
                    minimalStage(),
                ],
            }),
        ]);
        expect(hasRefinementWorkToDo(config, true, 1)).toBe(true);
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

    it("skipped stages don't count toward skipCount comparison (skip=2)", () => {
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
        expect(hasRefinementWorkToDo(config, true, 2)).toBe(true);
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

    it("counts active (non-skipped) stages in clip 0", () => {
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
        expect(countActiveStagesInMetadataClip0(json)).toBe(2);
    });

    it("treats stages without an explicit skipped flag as active", () => {
        const json = JSON.stringify({
            clips: [{ stages: [{}, {}, {}] }],
        });
        expect(countActiveStagesInMetadataClip0(json)).toBe(3);
    });
});
