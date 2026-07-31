/// <reference types="node" />
import * as fs from "node:fs";
import * as path from "node:path";

import { beforeEach, describe, expect, it } from "@jest/globals";

import { mountSelect, mountVideoFps } from "./__test_helpers__/dom";
import {
    createRootConfig,
    type DocumentNormalizationEnvironment,
    decodeStoredDocument,
    serializeStateForStorage,
} from "./persistence/documentCodec";
import { getDefaultStageModel, getRootDefaults } from "./rootDefaults";
import {
    CURRENT_AUTHORING_SCHEMA_VERSION,
    type VideoStagesConfig,
} from "./types";

// Frontend half of the frontend/backend authoring-document contract. The fixture is the exact
// carrier payload this codec emits; the C# AuthoringDocumentContractTests parses the same file and
// asserts every key the backend reads is present in it, so renaming a key on either side breaks the
// pair instead of silently dropping data.
const fixturePath = path.resolve(
    __dirname,
    "..",
    "Tests",
    "fixtures",
    "authoring-document.json",
);
const fixture = (): Record<string, unknown> =>
    JSON.parse(fs.readFileSync(fixturePath, "utf8"));

const normalizationEnvironment = (): DocumentNormalizationEnvironment => {
    const defaults = getRootDefaults();
    return {
        defaults,
        defaultStageModel: getDefaultStageModel(
            defaults.modelValues,
            undefined,
            defaults.modelCatalog,
        ),
    };
};

/**
 * One complete authoring document: every field the codec persists is set to a
 * non-default value so the fixture exercises the whole contract surface.
 */
const contractState = (): VideoStagesConfig => ({
    schemaVersion: CURRENT_AUTHORING_SCHEMA_VERSION,
    width: 768,
    height: 512,
    fps: 24,
    dimsExplicit: true,
    clips: [
        {
            id: "clip-0",
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
            architecturePayload: null,
            skipped: false,
            hue: 210,
            boundaryOut: "continue",
            boundaryOutCarryAudio: true,
            boundaryOutOverlap: 8,
            duration: 3,
            refFraming: "fit-green",
            audioSource: "Native",
            loras: [{ name: "style.safetensors" }],
            icLoras: [
                {
                    lora: "ic-lora-pose.safetensors",
                    preset: "pose",
                    driveSource: "Upload",
                    driveData: "visual",
                    driveMediaKinds: ["video"],
                    stage: 0,
                    strength: 0.9,
                    attentionStrength: 0.8,
                    // Deliberately not the "hdr" preset: the typed flag is preset-independent and
                    // must round-trip on its own.
                    controlType: "canny",
                    hdr: true,
                    driveMedia: {
                        data: "data:video/mp4;base64,QUJD",
                        fileName: "drive.mp4",
                    },
                },
            ],
            saveAudioTrack: true,
            clipLengthFromAudio: false,
            clipLengthFromControlNet: false,
            reuseAudio: true,
            uploadedAudio: {
                data: "data:audio/wav;base64,QUJD",
                fileName: "clip.wav",
            },
            prompt: "",
            promptWindows: [],
            retake: {
                id: "retake-0",
                startSeconds: 0.5,
                lengthSeconds: 1.5,
                strength: 0.7,
            },
            sourceVideo: {
                data: "data:video/mp4;base64,REVG",
                fileName: "source.mp4",
                fps: 30,
                durationSeconds: 6,
                startSeconds: 1,
                lengthSeconds: 3,
            },
            refs: [
                {
                    id: "ref-0",
                    source: "Upload",
                    uploadFileName: "ref.png",
                    uploadedImage: {
                        data: "data:image/png;base64,QUJD",
                        fileName: "ref.png",
                    },
                    frame: 2,
                    fromEnd: true,
                },
            ],
            stages: [
                {
                    id: "stage-0",
                    skipped: false,
                    control: 1,
                    controlNetStrength: 0.8,
                    icLoraStrengths: [0.8],
                    loraWeights: [0.5],
                    refStrengths: [0.6],
                    upscale: 1,
                    upscaleMethod: "pixel-lanczos",
                    model: "ltx-2.3.safetensors",
                    modelProfileId: "ltx-2.3",
                    steps: 12,
                    cfgScale: 4.5,
                    sampler: "euler",
                    scheduler: "normal",
                },
                {
                    id: "stage-1",
                    skipped: true,
                    control: 0.5,
                    controlNetStrength: 0.8,
                    icLoraStrengths: [0.5],
                    loraWeights: [0],
                    refStrengths: [0.4],
                    upscale: 1.5,
                    upscaleMethod: "latentmodel-upscaler.safetensors",
                    model: "ltx-2.3.safetensors",
                    modelProfileId: "ltx-2.3",
                    steps: 8,
                    cfgScale: 1,
                    sampler: "euler",
                    scheduler: "normal",
                },
            ],
        },
        {
            id: "clip-1",
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
            architecturePayload: null,
            skipped: false,
            hue: 40,
            boundaryOut: "cut",
            boundaryOutCarryAudio: false,
            boundaryOutOverlap: 1,
            duration: 2,
            refFraming: "crop",
            audioSource: "Native",
            loras: [],
            icLoras: [],
            saveAudioTrack: false,
            clipLengthFromAudio: false,
            clipLengthFromControlNet: false,
            reuseAudio: false,
            uploadedAudio: null,
            prompt: "",
            promptWindows: [],
            retake: null,
            sourceVideo: null,
            refs: [],
            stages: [
                {
                    id: "stage-2",
                    skipped: false,
                    control: 0.5,
                    controlNetStrength: 0.8,
                    icLoraStrengths: [],
                    loraWeights: [],
                    refStrengths: [],
                    upscale: 1,
                    upscaleMethod: "latentmodel-upscaler.safetensors",
                    model: "ltx-2.3.safetensors",
                    modelProfileId: "ltx-2.3",
                    steps: 8,
                    cfgScale: 1,
                    sampler: "euler",
                    scheduler: "normal",
                },
            ],
        },
    ],
    audioTracks: [
        {
            id: "track-0",
            volume: 0.75,
            source: {
                kind: "Upload",
                reference: "",
                uploadedAudio: {
                    data: "data:audio/wav;base64,REVG",
                    fileName: "track.wav",
                },
            },
            spans: [
                {
                    id: "span-0",
                    timelineStartSeconds: 1,
                    timelineLengthSeconds: 3,
                    sourceStartSeconds: 0.5,
                },
            ],
        },
    ],
});

describe("authoring document contract fixture", () => {
    beforeEach(() => {
        document.body.innerHTML = "";
        mountVideoFps(24);
        mountSelect("input_videomodel", {
            options: ["ltx-2.3.safetensors"],
            value: "ltx-2.3.safetensors",
        });
        mountSelect("input_refinerupscalemethod", {
            options: ["pixel-lanczos", "latentmodel-upscaler.safetensors"],
            value: "pixel-lanczos",
        });
    });

    it("encodes the canonical document exactly as the shared fixture", () => {
        expect(JSON.parse(serializeStateForStorage(contractState()))).toEqual(
            fixture(),
        );
    });

    it("round-trips the shared fixture through decode and re-encode", () => {
        const decoded = decodeStoredDocument(
            JSON.stringify(fixture()),
            {
                width: 1024,
                height: 1024,
                fps: 24,
            },
            normalizationEnvironment(),
        );
        expect(decoded).not.toBeNull();
        if (!decoded) {
            return;
        }
        const reencoded = serializeStateForStorage(
            createRootConfig(decoded.dims, decoded.clips, decoded.audioTracks),
        );
        expect(JSON.parse(reencoded)).toEqual(fixture());
    });

    it("round-trips opaque nested payload for an unknown architecture", () => {
        const unknown = fixture();
        const clips = unknown.clips as Record<string, unknown>[];
        const populatedClip = clips[0];
        const opaquePayload = {
            futureConditioning: {
                layers: [
                    {
                        kind: "motion-vector-field",
                        options: {
                            temporalScale: 1.25,
                            preserveOcclusion: true,
                        },
                    },
                ],
            },
            futureStagePayloads: {
                "stage-0": {
                    schedule: [0, 0.25, 1],
                    vendorExtension: "leave-this-verbatim",
                },
            },
        };
        populatedClip.architectureHint = "future-video";
        populatedClip.modelProfileId = "future-video-v1";
        populatedClip.architecturePayload = opaquePayload;
        for (const [index, stage] of (
            populatedClip.stages as Record<string, unknown>[]
        ).entries()) {
            stage.model = `removed-video-stage-${index}.safetensors`;
            stage.modelProfileId = `removed-video-profile-${index}`;
        }

        const decoded = decodeStoredDocument(
            JSON.stringify(unknown),
            {
                width: 1024,
                height: 1024,
                fps: 24,
            },
            normalizationEnvironment(),
        );
        expect(decoded).not.toBeNull();
        if (!decoded) {
            return;
        }
        expect(decoded.clips[0].architecturePayload).toEqual(opaquePayload);
        const serialized = serializeStateForStorage(
            createRootConfig(decoded.dims, decoded.clips, decoded.audioTracks),
        );
        const decodedAgain = decodeStoredDocument(
            serialized,
            {
                width: 1024,
                height: 1024,
                fps: 24,
            },
            normalizationEnvironment(),
        );
        expect(decodedAgain).not.toBeNull();
        if (!decodedAgain) {
            return;
        }
        expect(decodedAgain.clips[0].architecturePayload).toEqual(
            opaquePayload,
        );
        const serializedAgain = serializeStateForStorage(
            createRootConfig(
                decodedAgain.dims,
                decodedAgain.clips,
                decodedAgain.audioTracks,
            ),
        );

        expect(serializedAgain).toBe(serialized);
        const roundTripped = JSON.parse(serialized) as {
            clips: {
                architectureHint: string;
                modelProfileId: string;
                architecturePayload: Record<string, unknown> | null;
                stages: {
                    model: string;
                    modelProfileId: string;
                    icLoraStrengths: number[];
                }[];
                icLoras: {
                    driveSource: string;
                    driveData: string;
                    driveMediaKinds: string[];
                    controlType: string;
                    hdr: boolean;
                    driveMedia: { data: string; fileName: string } | null;
                }[];
            }[];
        };
        expect(roundTripped.clips[0]).toMatchObject({
            architectureHint: "future-video",
            modelProfileId: "future-video-v1",
            architecturePayload: opaquePayload,
            stages: [
                {
                    model: "removed-video-stage-0.safetensors",
                    modelProfileId: "removed-video-profile-0",
                },
                {
                    model: "removed-video-stage-1.safetensors",
                    modelProfileId: "removed-video-profile-1",
                },
            ],
        });
        expect(roundTripped.clips[0].icLoras[0]).toEqual(
            expect.objectContaining({
                driveSource: "Upload",
                driveData: "visual",
                driveMediaKinds: ["video"],
                controlType: "canny",
                hdr: true,
                driveMedia: {
                    data: "data:video/mp4;base64,QUJD",
                    fileName: "drive.mp4",
                },
            }),
        );
        expect(roundTripped.clips[0].stages[0].icLoraStrengths).toEqual([0.8]);
    });
});
