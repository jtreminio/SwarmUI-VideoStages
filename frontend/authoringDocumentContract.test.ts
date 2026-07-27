/// <reference types="node" />
import * as fs from "node:fs";
import * as path from "node:path";

import { beforeEach, describe, expect, it } from "@jest/globals";

import { mountSelect, mountVideoFps } from "./__test_helpers__/dom";
import {
    createRootConfig,
    decodeStoredDocument,
    serializeStateForStorage,
} from "./persistence/documentCodec";
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
            architecture: "ltx2",
            modelProfileId: "ltx-2.3",
            skipped: false,
            hue: 210,
            boundaryOut: "continue",
            boundaryOutCarryAudio: true,
            boundaryOutOverlap: 8,
            duration: 3,
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
            architecture: "ltx2",
            modelProfileId: "ltx-2.3",
            skipped: false,
            hue: 40,
            boundaryOut: "cut",
            boundaryOutCarryAudio: false,
            boundaryOutOverlap: 1,
            duration: 2,
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
        const decoded = decodeStoredDocument(JSON.stringify(fixture()), {
            width: 1024,
            height: 1024,
            fps: 24,
        });
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
