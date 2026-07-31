import { describe, expect, it } from "@jest/globals";
import { testArchitectureCatalog } from "../__test_helpers__/architectureFixtures";
import type { Clip, IcLora } from "../types";
import {
    hasArchitectureSlotSourcedIcLora,
    normalizeArchitectureIcLoras,
    reconcileArchitectureIncomingIcLoraDrives,
} from "./behaviorRegistry";

const baseEntry: IcLora = {
    lora: "ltx-ic-lora-pose.safetensors",
    preset: "pose",
    driveSource: "Upload",
    driveData: "visual",
    driveMediaKinds: ["image", "video"],
    stage: -1,
    strength: 1,
    attentionStrength: 1,
    controlType: "none",
    driveMedia: null,
};

describe("architecture-owned LTX behavior", () => {
    it("recognizes the finite backend ControlNet source vocabulary", () => {
        const entry = (driveSource: string): IcLora => ({
            ...baseEntry,
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
        const raw = { icLoras: [baseEntry] };

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
            ...baseEntry,
            preset: "custom",
            driveSource: "Incoming",
            stage: 0,
        };
        const ltx = {
            architectureHint: "ltx2",
            skipped: false,
            initVideo: null,
            stages: [{ model: "ltx", skipped: false }],
            icLoras: [incoming],
        } as unknown as Clip;
        const foreign = {
            architectureHint: "future-video",
            skipped: false,
            initVideo: null,
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
            ...baseEntry,
            preset: "custom",
            driveSource: "Incoming",
            stage: 0,
        };
        const clip = (architecture: string, model: string): Clip =>
            ({
                architectureHint: architecture,
                skipped: false,
                initVideo: null,
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
