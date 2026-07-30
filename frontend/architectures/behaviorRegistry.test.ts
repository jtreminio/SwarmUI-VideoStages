import { describe, expect, it } from "@jest/globals";
import { testArchitectureCatalog } from "../__test_helpers__/architectureFixtures";
import type { Clip, IcLora } from "../types";
import {
    architectureBehavior,
    hasArchitectureSlotSourcedIcLora,
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

    it("recognizes the finite backend ControlNet source vocabulary", () => {
        const entry = (driveSource: string): IcLora => ({
            ...hdrEntry,
            driveSource,
        });

        expect(
            hasArchitectureSlotSourcedIcLora("ltx2", [
                entry("  controlnet   2  "),
            ]),
        ).toBe(true);
        expect(
            hasArchitectureSlotSourcedIcLora("ltx2", [entry("CONTROL NET 03")]),
        ).toBe(true);
        expect(
            hasArchitectureSlotSourcedIcLora("ltx2", [
                entry("ControlNet 4"),
                entry("future-slot"),
                entry("Incoming"),
                entry("Upload"),
            ]),
        ).toBe(false);
        expect(
            hasArchitectureSlotSourcedIcLora("future-video", [
                entry("ControlNet 1"),
            ]),
        ).toBe(false);
    });

    it("does not silently apply LTX normalization to another architecture", () => {
        const raw = { icLoras: [hdrEntry] };

        expect(
            normalizeArchitectureIcLoras("future-video", raw, 1, false),
        ).toEqual([]);
        expect(
            normalizeArchitectureIcLoras("future-video", raw, 1, false, {
                preserveDormantLtx: true,
            }),
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
            stages: [{ model: "ltx", skipped: false }],
            icLoras: [incoming],
        } as unknown as Clip;
        const foreign = {
            architecture: "future-video",
            skipped: false,
            sourceVideo: null,
            stages: [{ model: "future-model", skipped: false }],
            icLoras: [{ ...incoming }],
        } as unknown as Clip;

        reconcileArchitectureIncomingIcLoraDrives(
            [ltx, foreign],
            "text-to-video",
            testArchitectureCatalog(),
        );

        expect(ltx.icLoras[0].driveSource).toBe("Upload");
        expect(foreign.icLoras[0].driveSource).toBe("Incoming");
    });

    it("resolves the behavior owner from Stage 0 when a catalog is supplied", () => {
        const incoming = {
            ...hdrEntry,
            preset: "custom",
            driveSource: "Incoming",
            stage: 0,
        };
        const clip = (architecture: string, model: string): Clip =>
            ({
                architecture,
                skipped: false,
                sourceVideo: null,
                stages: [{ model, skipped: false }],
                icLoras: [{ ...incoming }],
            }) as unknown as Clip;
        const resolved = clip("stale-foreign-hint", "ltx");
        const unresolved = clip("ltx2", "removed-model.safetensors");
        const catalog = testArchitectureCatalog();

        reconcileArchitectureIncomingIcLoraDrives(
            [resolved],
            "text-to-video",
            catalog,
        );
        reconcileArchitectureIncomingIcLoraDrives(
            [unresolved],
            "text-to-video",
            catalog,
        );

        expect(resolved.icLoras[0].driveSource).toBe("Upload");
        expect(unresolved.icLoras[0].driveSource).toBe("Incoming");
    });
});
