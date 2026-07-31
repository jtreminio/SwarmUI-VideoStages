import { deriveArchitectureDiagnostics } from "./architectures/diagnostics";
import { createCapabilityViewResolver } from "./architectures/policy";
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
    /** Backend-projected catalog. Without it, architecture-owned rules are not inferred. */
    catalog?: ArchitectureModelCatalog;
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
    context: AuthoringDiagnosticContext = {},
): AuthoringDiagnostic[] => {
    const diagnostics: AuthoringDiagnostic[] = [];
    const firstSkippedClip = clips.findIndex((clip) => clip.skipped === true);
    const authoredPrefix =
        firstSkippedClip < 0 ? clips : clips.slice(0, firstSkippedClip);
    const executable = executableClipIndexes(clips).map((clipIdx) => ({
        clip: clips[clipIdx],
        clipIdx,
    }));
    const capabilityViews = context.catalog
        ? createCapabilityViewResolver(context.catalog, {
              timelineClips: executable.map(({ clip }) => clip),
          })
        : null;
    if (context.catalog && capabilityViews) {
        diagnostics.push(
            ...deriveArchitectureDiagnostics(
                authoredPrefix,
                context.catalog,
                capabilityViews,
            ),
        );
    }

    for (const { clip, clipIdx } of executable) {
        const view = capabilityViews?.forClip(clip);
        const conditionalFeatures = [
            {
                feature: "audioReuse",
                persisted: clip.reuseAudio,
                severity: "warning",
            },
            {
                feature: "promptRelay",
                persisted: clip.promptWindows.length > 0,
                severity: "error",
            },
            {
                feature: "retake",
                persisted: clip.retake !== null,
                severity: "error",
            },
        ] as const;
        for (const check of conditionalFeatures) {
            const rule = view?.decision(check.feature).rule;
            if (check.persisted && rule) {
                diagnostics.push(
                    diagnostic(check.severity, rule.code, rule.reason, clipIdx),
                );
            }
        }
    }

    return diagnostics;
};
