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
            skipped: false,
            hue: 210,
            boundaryOut: "continue",
            boundaryOutCarryAudio: true,
            boundaryOutReferenceScale: 0.5,
            boundaryOutReferenceIncludeSoundtrack: false,
            boundaryOutOverlap: 8,
            duration: 3,
            refFraming: "fit-green",
            audioSource: "Native",
            loras: [{ name: "style.safetensors" }],
            icLoras: [
                {
                    id: "ic-lora-0",
                    lora: "ic-lora-pose.safetensors",
                    preset: "pose",
                    driveSource: "Upload",
                    driveData: "visual",
                    driveMediaKinds: ["video"],
                    stage: 0,
                    strength: 0.9,
                    attentionStrength: 0.8,
                    controlType: "canny",
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
            initVideo: {
                data: "data:video/mp4;base64,REVG",
                fileName: "source.mp4",
                fps: 30,
                durationSeconds: 6,
                startSeconds: 1,
                lengthSeconds: 3,
            },
            references: [
                {
                    id: "clip-reference-0",
                    kind: "image",
                    source: "Upload",
                    uploadedMedia: {
                        data: "data:image/png;base64,QUJD",
                        fileName: "subject.png",
                    },
                    includeSoundtrack: false,
                    mediaDurationSeconds: 0,
                    drivesClipLength: false,
                    mediaScale: 1,
                },
                {
                    id: "clip-reference-1",
                    kind: "video",
                    source: "Upload",
                    uploadedMedia: {
                        data: "data:video/mp4;base64,REVG",
                        fileName: "motion.mp4",
                    },
                    includeSoundtrack: true,
                    mediaDurationSeconds: 4.5,
                    drivesClipLength: true,
                    mediaScale: 0.5,
                },
            ],
            frameRefs: [
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
                    frameRefStrengths: [0.6],
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
                    frameRefStrengths: [0.4],
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
            skipped: false,
            hue: 40,
            boundaryOut: "cut",
            boundaryOutCarryAudio: false,
            boundaryOutReferenceScale: 1,
            boundaryOutReferenceIncludeSoundtrack: true,
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
            initVideo: null,
            references: [],
            frameRefs: [],
            stages: [
                {
                    id: "stage-2",
                    skipped: false,
                    control: 0.5,
                    controlNetStrength: 0.8,
                    icLoraStrengths: [],
                    loraWeights: [],
                    frameRefStrengths: [],
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
});
