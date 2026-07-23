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
            "defaultIcLora",
            "getReferenceFrameMax",
            "hasSlotSourcedIcLora",
            "normalizeAudioSegments",
            "normalizeAudioTrackSpan",
            "normalizeAudioTracks",
            "normalizeBoundaryOut",
            "normalizeClip",
            "normalizeContinueOverlap",
            "normalizeControlNetLora",
            "normalizeControlNetSource",
            "normalizeIcLora",
            "normalizeIcLoraControlType",
            "normalizeIcLoras",
            "normalizePromptWindows",
            "normalizeRef",
            "normalizeRetake",
            "normalizeSourceVideo",
            "normalizeStage",
            "normalizeStageControlNetStrengthValue",
            "normalizeStageLoras",
            "normalizeStageRefStrengthValue",
            "normalizeStageRefStrengths",
            "normalizeUploadedAudio",
            "readProp",
            "readRawStageProp",
            "readRawStageString",
            "reconcileIcLoraStage",
            "removeRefAt",
        ]);
    });
});
