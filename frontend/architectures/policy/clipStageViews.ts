import type { Clip, Stage } from "../../types";
import {
    CONDITIONAL_RULE_CODES,
    conditionalRule,
    evaluateConditionalRule,
} from "../conditionalRules";
import type {
    ArchitectureCatalogEntryDto,
    CapabilityRuleDecision,
} from "../types";
import { architectureReason, noArchitectureReason } from "./featureValues";
import type {
    AuthoringFeature,
    CapabilityDecision,
    ClipCapabilityView,
    StageCapabilityView,
} from "./types";

type ArchitectureLookup = ReadonlyMap<string, ArchitectureCatalogEntryDto>;

const conditionalRuleFor = (
    clip: Clip,
    feature: AuthoringFeature,
    descriptor: ArchitectureCatalogEntryDto,
): CapabilityRuleDecision | undefined => {
    const code =
        feature === "promptRelay"
            ? CONDITIONAL_RULE_CODES.promptRelayRequiresFixedLength
            : feature === "audioReuse"
              ? CONDITIONAL_RULE_CODES.audioReuseRequiresStages
              : null;
    if (!code) return undefined;
    const rule = conditionalRule(descriptor.rules, code);
    return rule && evaluateConditionalRule(rule, { clip }) ? rule : undefined;
};

const scopedFeatureSupport = (
    feature: AuthoringFeature,
    descriptor: ArchitectureCatalogEntryDto,
    profileId: string,
): boolean => {
    const capability = descriptor.capabilities;
    const profile = descriptor.profiles.find((entry) => entry.id === profileId);
    switch (feature) {
        case "multiStage":
            return capability.architecture.includes("multi-stage");
        case "sourceVideo":
            return capability.clip.includes("source-video");
        case "frameReferences":
            return (
                capability.clip.includes("references") &&
                capability.stage.includes("frame-references")
            );
        case "retake":
            return capability.clip.includes("retake");
        case "majorPrompt":
            return capability.clip.includes("prompts");
        case "promptRelay":
            return capability.clip.includes("prompt-relay");
        case "clipAudio":
        case "audioReuse":
            return capability.clip.includes("audio-sources");
        case "stageLoras":
            return (
                capability.stage.includes("lora") &&
                (profile === undefined ||
                    profile.capabilities.includes("normal-lora"))
            );
        case "icLora":
            return capability.stage.includes("ic-lora");
        case "hdr":
            return capability.stage.includes("hdr");
        case "upscale":
            return capability.upscaleModes.length > 0;
    }
};

export const createClipStageCapabilityViews = (
    architectureById: ArchitectureLookup,
): {
    forClip(clip: Clip): ClipCapabilityView;
    forStage(clip: Clip, stage: Stage): StageCapabilityView;
} => {
    const forClip = (clip: Clip): ClipCapabilityView => {
        const descriptor = architectureById.get(clip.architecture);
        const label =
            descriptor?.label ??
            (clip.architecture === "none"
                ? "source-only clips"
                : `unknown architecture '${clip.architecture}'`);
        const decision = (feature: AuthoringFeature): CapabilityDecision => {
            if (!descriptor) {
                return {
                    supported: false,
                    reason: noArchitectureReason(feature),
                    rule: null,
                };
            }
            const conditionalRule = conditionalRuleFor(
                clip,
                feature,
                descriptor,
            );
            const supported =
                scopedFeatureSupport(
                    feature,
                    descriptor,
                    clip.modelProfileId,
                ) && !conditionalRule;
            return {
                supported,
                reason: supported
                    ? ""
                    : (conditionalRule?.reason ??
                      architectureReason(label, feature)),
                rule: conditionalRule ?? null,
            };
        };
        return {
            architectureId: clip.architecture,
            architectureLabel: label,
            known: descriptor !== undefined,
            audioSourceKinds: descriptor?.capabilities.audioSourceKinds ?? [],
            decision,
            authoringState: (feature, persisted) => {
                const result = decision(feature);
                return {
                    ...result,
                    visible: result.supported || persisted,
                    enabled: result.supported,
                };
            },
        };
    };

    const forStage = (clip: Clip, stage: Stage): StageCapabilityView => {
        const view = forClip(clip);
        const descriptor = architectureById.get(clip.architecture);
        const profile = descriptor?.profiles.find(
            (entry) => entry.id === stage.modelProfileId,
        );
        const decision = (
            feature: "stageLoras" | "upscale" | "sampler" | "scheduler",
        ): CapabilityDecision => {
            if (feature === "stageLoras" && descriptor) {
                const supported =
                    descriptor.capabilities.stage.includes("lora") &&
                    profile?.capabilities.includes("normal-lora") === true;
                return {
                    supported,
                    reason: supported
                        ? ""
                        : `Stage LoRAs require normal-LoRA support in ${descriptor.label}.`,
                    rule: null,
                };
            }
            if (feature === "sampler" || feature === "scheduler") {
                const required =
                    feature === "sampler"
                        ? "sampler-selection"
                        : "scheduler-selection";
                const supported =
                    profile?.capabilities.includes(required) === true;
                return {
                    supported,
                    reason: supported
                        ? ""
                        : `${feature === "sampler" ? "Sampler" : "Scheduler"} selection is not supported by this model profile.`,
                    rule: null,
                };
            }
            return view.decision(feature);
        };
        return {
            upscaleModes: descriptor?.capabilities.upscaleModes ?? [],
            decision,
            authoringState: (feature, persisted) => {
                const result = decision(feature);
                return {
                    ...result,
                    visible: result.supported || persisted,
                    enabled: result.supported,
                };
            },
        };
    };

    return { forClip, forStage };
};
