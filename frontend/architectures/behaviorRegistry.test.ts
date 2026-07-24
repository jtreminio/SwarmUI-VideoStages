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
    hdr: true,
    driveMedia: null,
};

describe("architecture behavior registry", () => {
    it("keeps LTX feature recognition behind the LTX adapter", () => {
        expect(architectureBehavior("ltx2")).not.toBeNull();
        expect(architectureBehavior("future-video")).toBeNull();
        expect(isArchitectureHdrFeature("ltx2", hdrEntry)).toBe(true);
        expect(isArchitectureHdrFeature("future-video", hdrEntry)).toBe(false);
    });

    it("reads the typed hdr contract instead of matching preset or lora names", () => {
        // A LoRA named "MyHDRUpscale" used to be HDR to the UI (name contains "hdr") and not to
        // the backend, so the user was told HDR was on and got flat log footage.
        const namedButNotHdr: IcLora = {
            ...hdrEntry,
            lora: "MyHDRUpscale.safetensors",
            preset: "custom",
            hdr: false,
        };
        const unnamedButHdr: IcLora = {
            ...hdrEntry,
            lora: "plain-name.safetensors",
            preset: "custom",
            hdr: true,
        };

        expect(isArchitectureHdrFeature("ltx2", namedButNotHdr)).toBe(false);
        expect(isArchitectureHdrFeature("ltx2", unnamedButHdr)).toBe(true);
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
