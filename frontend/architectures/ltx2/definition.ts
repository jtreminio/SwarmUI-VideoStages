import type { VideoArchitectureDefinition } from "../types";

export const LTX2_ARCHITECTURE_ID = "ltx2";

const LTX23_MODEL_NAME =
    /(^|[/\\_. -])ltx(?:[/\\_. -]*v?2)?[/\\_. -]*3($|[/\\_. -])/i;

export const ltx2Architecture: VideoArchitectureDefinition = {
    id: LTX2_ARCHITECTURE_ID,
    label: "LTX Video 2.3",
    defaultProfileId: "ltx-2.3",
    capabilities: {
        architecture: [
            "generated-entry",
            "sourced-entry",
            "multi-stage",
            "native-audio",
            "decoded-output",
        ],
        clip: [
            "source-video",
            "prompts",
            "prompt-relay",
            "references",
            "retake",
            "audio-sources",
            "audio-segments",
        ],
        stage: [
            "image-input",
            "video-input",
            "pixel-upscale",
            "model-upscale",
            "latent-upscale",
            "latent-model-upscale",
            "lora",
            "ic-lora",
            "hdr",
            "frame-references",
        ],
        output: ["video", "attached-audio", "standalone-audio"],
        upscaleModes: ["pixel", "model", "latent", "latent-model"],
        entryModes: [
            "text-to-video",
            "image-to-video",
            "source-video",
            "refine-video",
        ],
        audioSourceKinds: ["Native", "Upload", "ControlNet", "AceStepFun"],
    },
    profiles: [
        {
            id: "ltx-2.3",
            label: "LTX Video 2.3",
            capabilities: [
                "sampler-selection",
                "scheduler-selection",
                "dimension-rules",
                "frame-rules",
                "normal-lora",
            ],
            rules: [],
        },
    ],
    boundaryRules: {
        cut: {
            support: "supported",
            code: "ltx2.boundary.cut",
            reason: "Decoded LTX clips can be joined with a hard cut.",
            scope: "boundary",
            entityId: null,
            constraints: null,
        },
        continue: {
            support: "conditional",
            code: "ltx2.boundary.continue",
            reason: "Continue requires adjacent LTX clips and a compatible generated target.",
            scope: "boundary",
            entityId: null,
            constraints: {
                sameArchitecture: true,
                targetRequiresGeneratedEntry: true,
                targetRequiresStage: true,
                targetDisallowsInitialReference: true,
                frameStep: 8,
                minFrames: 8,
                maxFrames: 48,
                defaultFrames: 8,
                continuityExtraFrames: 1,
            },
        },
        crossfade: {
            support: "conditional",
            code: "ltx2.boundary.crossfade",
            reason: "Crossfade currently uses the LTX-owned decoded transition path.",
            scope: "boundary",
            entityId: null,
            constraints: {
                sameArchitecture: true,
                targetRequiresGeneratedEntry: false,
                targetRequiresStage: false,
                targetDisallowsInitialReference: false,
                frameStep: 8,
                minFrames: 8,
                maxFrames: 48,
                defaultFrames: 8,
                continuityExtraFrames: 0,
            },
        },
    },
    rules: [
        {
            support: "conditional",
            code: "audio.reuse.requires_three_stages",
            reason: "Audio reuse needs at least three active stages: generate, capture, then reuse.",
            scope: "clip",
            entityId: null,
            constraints: {
                minimumActiveStages: 3,
                failureSeverity: "warning",
                failureEffect: "disable-feature",
            },
        },
        {
            support: "conditional",
            code: "prompt-relay-dynamic-length-unsupported",
            reason: "Prompt relay requires a fixed frame count and cannot be combined with audio-owned or ControlNet-owned clip length.",
            scope: "clip",
            entityId: null,
            constraints: { requiresFixedFrameCount: true },
        },
        {
            support: "conditional",
            code: "retake-frame-references-unsupported",
            reason: "Retake and frame references are mutually exclusive because guide merging would overwrite the retake mask.",
            scope: "stage",
            entityId: null,
            constraints: {
                mutuallyExclusive: ["retake", "frameReferences"],
            },
        },
        {
            support: "conditional",
            code: "retake-source-required",
            reason: "Retake requires a sourced clip or a global Refine Video source.",
            scope: "clip",
            entityId: null,
            constraints: {
                requiresAnyEntryMode: ["source-video", "refine-video"],
            },
        },
        {
            support: "conditional",
            code: "mixed-hdr-timeline-unsupported",
            reason: "HDR IC-LoRA activation must be uniform across the complete timeline.",
            scope: "architecture",
            entityId: null,
            constraints: {
                uniformTimelineFeature: "hdr",
                minimumTimelineClips: 2,
            },
        },
    ],
    resolveModelProfile: (model) => {
        const classId = `${model.modelClassId ?? ""}`.trim().toLowerCase();
        if (classId === "lightricks-ltx-video-2-3") {
            return "ltx-2.3";
        }
        return LTX23_MODEL_NAME.test(model.value) ? "ltx-2.3" : null;
    },
};
