import { describe, expect, it } from "@jest/globals";
import type { IcLora } from "../types";
import {
    architectureBehavior,
    isArchitectureHdrFeature,
    normalizeArchitectureIcLoras,
} from "./behaviorRegistry";

const hdrEntry: IcLora = {
    lora: "ltx-hdr.safetensors",
    preset: "hdr",
    source: "Upload",
    stage: -1,
    strength: 1,
    attentionStrength: 1,
    controlType: "none",
    driveMedia: null,
};

describe("architecture behavior registry", () => {
    it("keeps LTX feature recognition behind the LTX adapter", () => {
        expect(architectureBehavior("ltx2")).not.toBeNull();
        expect(architectureBehavior("future-video")).toBeNull();
        expect(isArchitectureHdrFeature("ltx2", hdrEntry)).toBe(true);
        expect(isArchitectureHdrFeature("future-video", hdrEntry)).toBe(false);
    });

    it("does not silently apply LTX normalization to another architecture", () => {
        const raw = { icLoras: [hdrEntry] };

        expect(
            normalizeArchitectureIcLoras("future-video", raw, 1, false),
        ).toEqual([]);
        expect(
            normalizeArchitectureIcLoras("future-video", raw, 1, false, true),
        ).toHaveLength(1);
    });
});
