/// <reference types="node" />
import * as fs from "node:fs";
import * as path from "node:path";

import { describe, expect, it } from "@jest/globals";

import { testArchitectureCatalog } from "../../__test_helpers__/architectureFixtures";
import {
    activeDocumentDimensionMultiple,
    snapExplicitDocumentDimensions,
} from "../../documentDimensionSnap";
import type { Clip, IcLora, VideoStagesConfig } from "../../types";
import {
    icLoraDimensionFactor,
    ltx2DimensionFactor,
    ltx2DimensionMultiple,
} from "./dimensionPolicy";

interface PresetCase {
    id: string;
    autoModelName: string;
    dimensionDownscaleFactor?: number;
}

const presetCases = JSON.parse(
    fs.readFileSync(
        path.resolve(
            __dirname,
            "..",
            "..",
            "..",
            "Tests",
            "fixtures",
            "ic-lora-presets.json",
        ),
        "utf8",
    ),
) as PresetCase[];

const icLora = (overrides: Partial<IcLora>): IcLora =>
    ({
        preset: "custom",
        lora: "third-party.safetensors",
        ...overrides,
    }) as IcLora;

const clip = (...entries: IcLora[]): Clip =>
    ({
        architectureHint: "ltx2",
        sourceVideo: null,
        stages: [{ model: "ltx", skipped: false }],
        icLoras: entries,
    }) as unknown as Clip;

describe("LTX dimension policy", () => {
    it("resolves preset and exact curated-model factors", () => {
        expect(
            icLoraDimensionFactor(
                icLora({ preset: "union-control", lora: "[AUTO]" }),
            ),
        ).toBe(2);
        expect(
            icLoraDimensionFactor(
                icLora({
                    lora: "folder/ltx-2.3-22b-ic-lora-pixel-spatial-upscaler-x4-0.9.safetensors",
                }),
            ),
        ).toBe(4);
    });

    it("matches every curated factor in the shared backend fixture", () => {
        for (const presetCase of presetCases) {
            const expected = presetCase.dimensionDownscaleFactor ?? 1;
            expect(
                icLoraDimensionFactor(
                    icLora({ preset: presetCase.id, lora: "[AUTO]" }),
                ),
            ).toBe(expected);
            expect(
                icLoraDimensionFactor(
                    icLora({
                        preset: "custom",
                        lora: presetCase.autoModelName,
                    }),
                ),
            ).toBe(expected);
        }
    });

    it("raises the global grid by the largest active IC-LoRA factor", () => {
        const state = clip(
            icLora({ preset: "union-control" }),
            icLora({ preset: "pixel-spatial-upscaler-x4" }),
        );
        expect(ltx2DimensionFactor(state)).toBe(4);
        expect(ltx2DimensionMultiple(state)).toBe(128);
    });

    it("contributes the IC-LoRA grid through the architecture registry", () => {
        const state = {
            width: 1232,
            height: 688,
            dimsExplicit: true,
            clips: [clip(icLora({ preset: "union-control", lora: "[AUTO]" }))],
        } as VideoStagesConfig;

        const catalog = testArchitectureCatalog();
        expect(activeDocumentDimensionMultiple(state.clips, catalog)).toBe(64);
        expect(snapExplicitDocumentDimensions(state, catalog)).toMatchObject({
            changed: true,
            multiple: 64,
            before: { width: 1232, height: 688 },
            after: { width: 1280, height: 704 },
        });
        expect(state).toMatchObject({ width: 1280, height: 704 });
    });

    it("uses resolved Stage-0 identity for architecture dimension behavior", () => {
        const resolvedLtx = clip(
            icLora({ preset: "union-control", lora: "[AUTO]" }),
        );
        resolvedLtx.architectureHint = "stale-foreign-hint";
        const unresolved = structuredClone(resolvedLtx);
        unresolved.architectureHint = "ltx2";
        unresolved.stages[0].model = "removed-model.safetensors";

        expect(
            activeDocumentDimensionMultiple(
                [resolvedLtx],
                testArchitectureCatalog(),
            ),
        ).toBe(64);
        expect(
            activeDocumentDimensionMultiple(
                [unresolved],
                testArchitectureCatalog(),
            ),
        ).toBe(32);
    });
});
