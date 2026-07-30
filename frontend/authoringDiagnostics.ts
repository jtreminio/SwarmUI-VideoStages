import { clipHasActiveHdrForArchitecture } from "./architectures/behaviorRegistry";
import { architectureDescriptor } from "./architectures/catalog";
import {
    CONDITIONAL_RULE_CODES,
    conditionalRule,
    evaluateConditionalRule,
} from "./architectures/conditionalRules";
import {
    deriveArchitectureDiagnostics,
    effectiveArchitectureIdForClip,
} from "./architectures/diagnostics";
import type { ArchitectureModelCatalog } from "./architectures/types";
import { executableClipIndexes } from "./clipSemantics";
import type { Clip } from "./types";

export type AuthoringDiagnosticSeverity = "warning" | "error";

export interface AuthoringDiagnostic {
    severity: AuthoringDiagnosticSeverity;
    code: string;
    message: string;
    clipIdx?: number;
}

export interface AuthoringDiagnosticContext {
    /** True only while authoring an explicit global Refine Video invocation. */
    globalRefineMode?: boolean;
    /** Backend-projected catalog. Without it, architecture-owned rules are not inferred. */
    catalog?: ArchitectureModelCatalog;
    generatedEntryMode?: "text-to-video" | "image-to-video";
}

export { activeStageCount } from "./clipSemantics";

const diagnostic = (
    severity: AuthoringDiagnosticSeverity,
    code: string,
    message: string,
    clipIdx?: number,
): AuthoringDiagnostic => ({ severity, code, message, clipIdx });

/**
 * Frontend projection of graph-independent rules explicitly advertised by
 * each clip architecture. The backend plan remains authoritative.
 */
export const deriveAuthoringDiagnostics = (
    clips: readonly Clip[],
    context: AuthoringDiagnosticContext = {},
): AuthoringDiagnostic[] => {
    const diagnostics: AuthoringDiagnostic[] = [];
    const firstSkippedClip = clips.findIndex((clip) => clip.skipped === true);
    const authoredPrefix =
        firstSkippedClip < 0 ? clips : clips.slice(0, firstSkippedClip);
    if (context.catalog) {
        diagnostics.push(
            ...deriveArchitectureDiagnostics(
                authoredPrefix,
                context.catalog,
                context.generatedEntryMode,
            ),
        );
    }
    const executable = executableClipIndexes(clips).map((clipIdx) => ({
        clip: clips[clipIdx],
        clipIdx,
    }));
    const effectiveArchitectureId = (clip: Clip): string =>
        context.catalog
            ? effectiveArchitectureIdForClip(clip, context.catalog)
            : "unsupported";

    for (const { clip, clipIdx } of executable) {
        const descriptor = architectureDescriptor(
            context.catalog,
            effectiveArchitectureId(clip),
        );
        const rule = (code: Parameters<typeof conditionalRule>[1]) =>
            descriptor ? conditionalRule(descriptor.rules, code) : null;
        const reuseRule = rule(CONDITIONAL_RULE_CODES.audioReuseRequiresStages);
        if (
            reuseRule &&
            clip.reuseAudio &&
            evaluateConditionalRule(reuseRule, { clip })
        ) {
            diagnostics.push(
                diagnostic(
                    "warning",
                    reuseRule.code,
                    reuseRule.reason,
                    clipIdx,
                ),
            );
        }

        const relayRule = rule(
            CONDITIONAL_RULE_CODES.promptRelayRequiresFixedLength,
        );
        if (
            relayRule &&
            clip.promptWindows.length > 0 &&
            evaluateConditionalRule(relayRule, { clip })
        ) {
            diagnostics.push(
                diagnostic("error", relayRule.code, relayRule.reason, clipIdx),
            );
        }

        const retakeReferenceRule = rule(
            CONDITIONAL_RULE_CODES.retakeExcludesReferences,
        );
        const retakeSourceRule = rule(
            CONDITIONAL_RULE_CODES.retakeRequiresSource,
        );
        if (
            retakeSourceRule &&
            clip.retake &&
            evaluateConditionalRule(retakeSourceRule, {
                clip,
                globalRefineMode: context.globalRefineMode,
            })
        ) {
            diagnostics.push(
                diagnostic(
                    "error",
                    retakeSourceRule.code,
                    retakeSourceRule.reason,
                    clipIdx,
                ),
            );
        }
        if (
            retakeReferenceRule &&
            clip.retake &&
            evaluateConditionalRule(retakeReferenceRule, {
                clip,
                globalRefineMode: context.globalRefineMode,
            })
        ) {
            diagnostics.push(
                diagnostic(
                    "error",
                    retakeReferenceRule.code,
                    retakeReferenceRule.reason,
                    clipIdx,
                ),
            );
        }
    }

    const hdrRule = executable
        .map(({ clip }) =>
            context.catalog?.architectures
                .find((entry) => entry.id === effectiveArchitectureId(clip))
                ?.rules.find(
                    (rule) =>
                        rule.code === CONDITIONAL_RULE_CODES.uniformTimelineHdr,
                ),
        )
        .find((rule) => rule !== undefined);
    if (
        hdrRule &&
        evaluateConditionalRule(hdrRule, {
            timelineClips: executable.map(({ clip }) => clip),
            hasActiveHdr: (clip) =>
                clipHasActiveHdrForArchitecture(
                    clip,
                    effectiveArchitectureId(clip),
                ),
        })
    ) {
        diagnostics.push(diagnostic("error", hdrRule.code, hdrRule.reason));
    }

    return diagnostics;
};
