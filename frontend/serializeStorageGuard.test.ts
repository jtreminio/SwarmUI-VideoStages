import { describe, expect, it } from "@jest/globals";
import { testArchitectureCatalog } from "./__test_helpers__/architectureFixtures";
import { minimalClip } from "./__test_helpers__/clipFixtures";
import { normalizeClip } from "./normalization";
import { serializeClipsForStorage } from "./persistence";
import {
    STORED_CLIP_KEYS,
    STORED_REF_KEYS,
    STORED_STAGE_KEYS,
    type StoredClip,
} from "./storageTypes";
import type { Clip, RootDefaults } from "./types";

const getRootDefaults = (): RootDefaults => ({
    modelCatalog: testArchitectureCatalog(),
    modelValues: ["ltx"],
    modelLabels: ["LTX"],
    loraValues: ["detail.safetensors"],
    loraLabels: ["Detail"],
    samplerValues: ["euler"],
    samplerLabels: ["Euler"],
    schedulerValues: ["normal"],
    schedulerLabels: ["Normal"],
    upscaleMethodValues: ["latentmodel-a.safetensors"],
    upscaleMethodLabels: ["Latent Model: a.safetensors"],
    width: 1024,
    height: 1024,
    fps: 24,
    frames: 48,
    control: 0.5,
    controlMin: 0,
    controlMax: 1,
    controlStep: 0.05,
    upscale: 1,
    upscaleMin: 0.25,
    upscaleMax: 4,
    upscaleStep: 0.25,
    steps: 8,
    stepsMin: 1,
    stepsMax: 50,
    stepsStep: 1,
    cfgScale: 1,
    cfgScaleMin: 0,
    cfgScaleMax: 10,
    cfgScaleStep: 0.5,
});

const getDefaultStageModel = (modelValues: string[]): string =>
    modelValues[0] ?? "";

/** normalizeClip on a JSON-cloned raw record, mirroring the persistence parse. */
const normalize = (raw: unknown): Clip =>
    normalizeClip(
        JSON.parse(JSON.stringify(raw)) as Record<string, unknown>,
        getRootDefaults,
        getDefaultStageModel,
    );

const store = (clip: Clip): StoredClip => serializeClipsForStorage([clip])[0];

/**
 * A clip with EVERY persisted field set to a meaningful, round-trip-stable
 * value, so the guard exercises the whole serializer projection — a new stored
 * field that the fixture leaves at its default would slip the population check.
 */
const maximalClip = (): Clip =>
    minimalClip({
        skipped: true,
        boundaryOut: "crossfade",
        boundaryOutOverlap: 16,
        duration: 4,
        audioSource: "Native",
        icLoras: [
            {
                lora: "detail.safetensors",
                preset: "custom",
                source: "Upload",
                stage: -1,
                strength: 0.75,
                attentionStrength: 0.5,
                controlType: "canny",
                video: {
                    data: "data:video/mp4;base64,AAAA",
                    fileName: "d.mp4",
                },
                driveAudioRef: true,
            },
        ],
        saveAudioTrack: true,
        clipLengthFromAudio: true,
        clipLengthFromControlNet: false,
        reuseAudio: true,
        uploadedAudio: {
            data: "data:audio/wav;base64,AAAA",
            fileName: "v.wav",
        },
        sourceVideo: {
            data: "data:video/mp4;base64,CCCC",
            fileName: "src.mp4",
            fps: 24,
            durationSeconds: 8,
            startSeconds: 1,
            lengthSeconds: 4,
        },
        audioSegments: [
            {
                source: {
                    data: "data:audio/wav;base64,BBBB",
                    fileName: "s.wav",
                },
                startSeconds: 0.5,
                trimStartSeconds: 0.2,
                lengthSeconds: 1,
            },
        ],
        retake: { startSeconds: 1, lengthSeconds: 1, strength: 0.7 },
        refs: [
            {
                source: "Upload",
                uploadFileName: "ref.png",
                uploadedImage: {
                    data: "data:image/png;base64,CCCC",
                    fileName: "ref.png",
                },
                frame: 2,
                fromEnd: true,
            },
        ],
        stages: [
            {
                skipped: false,
                control: 0.5,
                controlNetStrength: 0.7,
                refStrengths: [0.8],
                upscale: 1,
                upscaleMethod: "latentmodel-a.safetensors",
                model: "ltx",
                modelProfileId: "ltx-2.3",
                steps: 8,
                cfgScale: 1,
                sampler: "euler",
                scheduler: "normal",
                loras: [{ name: "detail.safetensors", weight: 0.6 }],
            },
        ],
    });

describe("serialize storage completeness guard", () => {
    it("round-trips every persisted field of Clip/Stage/RefImage losslessly", () => {
        // Canonicalize first: a dropped stored field then shows up as an a/b diff.
        const a = store(normalize(maximalClip()));
        const b = store(normalize(a));

        expect(b).toEqual(a);
        for (const key of STORED_CLIP_KEYS) {
            expect(b[key]).toEqual(a[key]);
        }
        for (const key of STORED_REF_KEYS) {
            expect(b.refs[0][key]).toEqual(a.refs[0][key]);
        }
        for (const key of STORED_STAGE_KEYS) {
            expect(b.stages[0][key]).toEqual(a.stages[0][key]);
        }
    });

    it("the fixture actually populates every field the Stored* types declare", () => {
        const stored = store(normalize(maximalClip()));

        for (const key of STORED_CLIP_KEYS) {
            expect(Object.hasOwn(stored, key)).toBe(true);
        }
        for (const key of STORED_REF_KEYS) {
            expect(Object.hasOwn(stored.refs[0], key)).toBe(true);
        }
        for (const key of STORED_STAGE_KEYS) {
            expect(Object.hasOwn(stored.stages[0], key)).toBe(true);
        }
        // Every optional container is present, so the nested projections above
        // are genuinely exercised.
        expect(stored.icLoras.length).toBeGreaterThan(0);
        expect(stored.audioSegments.length).toBeGreaterThan(0);
        expect(stored.refs.length).toBeGreaterThan(0);
        expect(stored.stages.length).toBeGreaterThan(0);
        expect(stored.stages[0].loras.length).toBeGreaterThan(0);
        expect(stored.stages[0].refStrengths.length).toBeGreaterThan(0);
        expect(stored.retake).not.toBeNull();
        expect(stored.uploadedAudio).not.toBeNull();
    });
});
