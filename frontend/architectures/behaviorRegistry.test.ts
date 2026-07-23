import { describe, expect, it } from "@jest/globals";
import type { Clip, IcLora } from "../types";
import {
    architectureBehavior,
    isArchitectureHdrFeature,
    normalizeArchitectureIcLoras,
    reconcileArchitectureIncomingIcLoraDrives,
} from "./behaviorRegistry";

const hdrEntry: IcLora = {
    lora: "ltx-hdr.safetensors",
    preset: "hdr",
    driveSource: "Upload",
    driveData: "visual",
    driveMediaKinds: ["image", "video"],
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

    it("repairs only an owning architecture's Incoming entries", () => {
        const incoming = {
            ...hdrEntry,
            preset: "custom",
            driveSource: "Incoming",
            stage: 0,
        };
        const ltx = {
            architecture: "ltx2",
            skipped: false,
            sourceVideo: null,
            stages: [{}],
            icLoras: [incoming],
        } as unknown as Clip;
        const foreign = {
            architecture: "future-video",
            skipped: false,
            sourceVideo: null,
            stages: [{}],
            icLoras: [{ ...incoming }],
        } as unknown as Clip;

        reconcileArchitectureIncomingIcLoraDrives(
            [ltx, foreign],
            "text-to-video",
        );

        expect(ltx.icLoras[0].driveSource).toBe("Upload");
        expect(foreign.icLoras[0].driveSource).toBe("Incoming");
    });
});
