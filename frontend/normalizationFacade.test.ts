import { describe, expect, it } from "@jest/globals";
import * as normalization from "./normalization";

describe("normalization compatibility facade", () => {
    it("preserves the established public export surface", () => {
        expect(Object.keys(normalization).sort()).toEqual([
            "appendRefToClip",
            "buildDefaultClip",
            "buildDefaultRef",
            "buildDefaultStage",
            "buildDefaultStageRefStrengths",
            "getReferenceFrameMax",
            "normalizeAudioTrackSpan",
            "normalizeAudioTracks",
            "normalizeBoundaryOut",
            "normalizeClip",
            "normalizeClipReferences",
            "normalizeContinueOverlap",
            "normalizeInitVideo",
            "normalizePromptWindows",
            "normalizeRef",
            "normalizeRetake",
            "normalizeStage",
            "normalizeStageControlNetStrengthValue",
            "normalizeStageLoras",
            "normalizeStageRefStrengthValue",
            "normalizeStageRefStrengths",
            "normalizeUploadedMedia",
            "readProp",
            "readRawStageProp",
            "readRawStageString",
            "removeRefAt",
        ]);
    });
});
