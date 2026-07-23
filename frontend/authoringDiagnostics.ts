import type { Clip, IcLora } from "./types";

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
}

export const activeStageCount = (clip: Pick<Clip, "stages">): number =>
    clip.stages.filter((stage) => stage.skipped !== true).length;

export const isAudioReuseEligible = (clip: Pick<Clip, "stages">): boolean =>
    activeStageCount(clip) >= 3;

const isExecutableClip = (clip: Clip): boolean =>
    clip.skipped !== true &&
    (clip.sourceVideo !== null || activeStageCount(clip) > 0);

const isHdrIcLora = (entry: IcLora): boolean =>
    `${entry.preset ?? ""}`.trim().toLowerCase() === "hdr" ||
    `${entry.lora ?? ""}`.toLowerCase().includes("ic-lora-hdr");

const clipHasActiveHdr = (clip: Clip): boolean =>
    clip.icLoras.some(
        (entry) =>
            isHdrIcLora(entry) &&
            clip.stages.some(
                (stage, rawStageIdx) =>
                    stage.skipped !== true &&
                    (entry.stage < 0 || entry.stage === rawStageIdx),
            ),
    );

const diagnostic = (
    severity: AuthoringDiagnosticSeverity,
    code: string,
    message: string,
    clipIdx?: number,
): AuthoringDiagnostic => ({ severity, code, message, clipIdx });

/**
 * Frontend projection of graph-independent authoring diagnostics already
 * enforced by the LTX backend plan, plus the parser's retake source precondition.
 */
export const deriveAuthoringDiagnostics = (
    clips: readonly Clip[],
    context: AuthoringDiagnosticContext = {},
): AuthoringDiagnostic[] => {
    const diagnostics: AuthoringDiagnostic[] = [];
    const executable = clips
        .map((clip, clipIdx) => ({ clip, clipIdx }))
        .filter(({ clip }) => isExecutableClip(clip));

    for (const { clip, clipIdx } of executable) {
        if (clip.reuseAudio && !isAudioReuseEligible(clip)) {
            diagnostics.push(
                diagnostic(
                    "warning",
                    "audio.reuse.requires_three_stages",
                    "Audio reuse needs at least three active stages: generate, capture, then reuse.",
                    clipIdx,
                ),
            );
        }

        if (
            clip.promptWindows.length > 0 &&
            (clip.clipLengthFromAudio || clip.clipLengthFromControlNet)
        ) {
            diagnostics.push(
                diagnostic(
                    "error",
                    "prompt-relay-dynamic-length-unsupported",
                    "Prompt relay cannot be combined with audio-owned or ControlNet-owned clip length because the relay schedule requires a fixed frame count.",
                    clipIdx,
                ),
            );
        }

        if (clip.retake && !clip.sourceVideo && !context.globalRefineMode) {
            diagnostics.push(
                diagnostic(
                    "warning",
                    "retake-source-required",
                    "Retake requires a source video on this clip or the Refine Video flow; normal generation will ignore it.",
                    clipIdx,
                ),
            );
        }

        if (
            clip.retake &&
            clip.refs.length > 0 &&
            (clip.sourceVideo !== null || context.globalRefineMode)
        ) {
            diagnostics.push(
                diagnostic(
                    "error",
                    "retake-frame-references-unsupported",
                    "A retake cannot run with frame references because guide merges would overwrite the retake mask.",
                    clipIdx,
                ),
            );
        }
    }

    if (executable.length > 1) {
        const hdr = executable.map(({ clip }) => clipHasActiveHdr(clip));
        if (hdr.some(Boolean) && hdr.some((value) => !value)) {
            diagnostics.push(
                diagnostic(
                    "error",
                    "mixed-hdr-timeline-unsupported",
                    "A multi-clip timeline cannot mix HDR IC-LoRA clips with non-HDR clips because final HDR conversion applies to the complete timeline.",
                ),
            );
        }
    }

    return diagnostics;
};
