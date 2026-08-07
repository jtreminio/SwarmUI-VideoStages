import { deriveArchitectureDiagnostics } from "./architectures/diagnostics";
import type { CapabilityViewResolver } from "./architectures/policy/types";
import { activePrefix, executableClipIndexes } from "./clipSemantics";
import type { Clip } from "./types";

export type AuthoringDiagnosticSeverity = "warning" | "error";

export interface AuthoringDiagnostic {
    severity: AuthoringDiagnosticSeverity;
    code: string;
    message: string;
    clipIdx?: number;
}

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
    capabilities: CapabilityViewResolver,
): AuthoringDiagnostic[] => {
    const diagnostics: AuthoringDiagnostic[] = [];
    const authoredPrefix = activePrefix(clips);
    const executable = executableClipIndexes(clips).map((clipIdx) => ({
        clip: clips[clipIdx],
        clipIdx,
    }));
    diagnostics.push(
        ...deriveArchitectureDiagnostics(authoredPrefix, capabilities),
    );

    for (const { clip, clipIdx } of executable) {
        const retake = capabilities.forClip(clip).decision("retake");
        if (clip.retake !== null && retake.code) {
            diagnostics.push(
                diagnostic("error", retake.code, retake.reason, clipIdx),
            );
        }
    }

    return diagnostics;
};
