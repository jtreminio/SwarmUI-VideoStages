import {
    reconcileArchitectureIncomingIcLoraDrives,
    reconcileClipArchitectureIncomingIcLoraDrives,
} from "./architectures/behaviorRegistry";
import {
    modelIdentityFromCatalog,
    reconcileClipArchitectureIdentity,
} from "./architectures/clipIdentity";
import {
    planArchitectureConversion,
    resolveArchitectureRetarget,
} from "./architectures/conversion/plan";
import { forceCrossArchitectureCutsForConversion } from "./architectures/policy/boundaryPolicy";
import {
    addBefore,
    clone,
    failure,
    findClip,
    hasOwn,
    invalidNewEntity,
    success,
} from "./documentCommands/helpers";
import { LIST_ENTITIES } from "./documentCommands/listEntities";
import {
    applyListCommand,
    type ListOperation,
} from "./documentCommands/listReducer";
import type {
    CommandFailure,
    DocumentCommand,
    DocumentCommandContext,
    DocumentCommandResult,
} from "./documentCommands/types";
import type { CanonicalClip, CanonicalVideoStagesConfig } from "./types";

export type {
    CommandFailure,
    DocumentCommand,
    DocumentCommandContext,
    DocumentCommandResult,
} from "./documentCommands/types";

const list = (
    document: CanonicalVideoStagesConfig,
    entity: keyof typeof LIST_ENTITIES,
    operation: ListOperation,
    command: object,
    context: DocumentCommandContext,
): DocumentCommandResult =>
    applyListCommand(
        document,
        LIST_ENTITIES[entity],
        operation,
        command,
        context,
    );

const assertNever = (command: never): never => {
    throw new Error(`Unhandled document command: ${JSON.stringify(command)}`);
};

/**
 * Applies one stable-ID command to an isolated authoring snapshot.
 *
 * The input and command payload are never mutated. Missing owners, targets, or
 * ID-relative insertion points fail closed and return an unchanged clone.
 */
export const reduceDocumentCommand = (
    source: CanonicalVideoStagesConfig,
    command: DocumentCommand,
    context: DocumentCommandContext = { architectureCatalog: null },
): DocumentCommandResult => {
    const document = clone(source);

    switch (command.type) {
        case "batch": {
            let current = document;
            for (const child of command.commands) {
                const result = reduceDocumentCommand(current, child, context);
                if (!result.applied) {
                    return failure(
                        clone(source),
                        result.failure as CommandFailure,
                    );
                }
                current = result.document;
            }
            return success(current);
        }
        case "root.patch": {
            Object.assign(document, clone(command.patch));
            return success(document);
        }
        case "clip.add": {
            const invalid = invalidNewEntity(document, command.clip);
            if (invalid) return invalid;
            const addedClip = clone(command.clip);
            if (
                !reconcileClipArchitectureIdentity(
                    addedClip,
                    context.architectureCatalog,
                )
            ) {
                return failure(document, "architecture-invariant");
            }
            if (!addBefore(document.clips, addedClip, command.beforeClipId)) {
                return failure(clone(source), "missing-target");
            }
            return success(document);
        }
        case "clip.remove":
            return list(document, "clip", "remove", command, context);
        case "clip.toggle-skip": {
            const clipIndex = document.clips.findIndex(
                (clip) => clip.id === command.clipId,
            );
            const clip = document.clips[clipIndex];
            if (!clip) {
                return failure(document, "missing-target");
            }
            if (clipIndex === 0 && clip.skipped !== true) {
                return failure(document, "invalid-operation");
            }
            const skipped = !clip.skipped;
            const firstSkipped = document.clips.findIndex(
                (candidate) => candidate.skipped === true,
            );
            const start = skipped ? clipIndex : Math.max(0, firstSkipped);
            for (let index = start; index < document.clips.length; index++) {
                document.clips[index].skipped = skipped;
            }
            reconcileArchitectureIncomingIcLoraDrives(
                document.clips,
                context.generatedEntryMode ?? "text-to-video",
                context.architectureCatalog,
            );
            return success(document);
        }
        case "clip.move":
            return list(document, "clip", "move", command, context);
        case "clip.patch": {
            if (
                hasOwn(command.patch, "architectureHint") ||
                hasOwn(command.patch, "modelProfileId")
            ) {
                return failure(document, "architecture-invariant");
            }
            const clip = findClip(document, command.clipId);
            if (!clip) {
                return failure(document, "missing-target");
            }
            const candidate = clone(clip);
            Object.assign(candidate, clone(command.patch), { id: clip.id });
            if (
                hasOwn(command.patch, "initVideo") &&
                !reconcileClipArchitectureIdentity(
                    candidate,
                    context.architectureCatalog,
                )
            ) {
                return failure(document, "architecture-invariant");
            }
            document.clips[document.clips.indexOf(clip)] = candidate;
            return success(document);
        }
        case "clip.convert-architecture": {
            const clipIndex = document.clips.findIndex(
                (clip) => clip.id === command.clipId,
            );
            const clip = document.clips[clipIndex];
            const target = command.target;
            if (!clip) {
                return failure(document, "missing-target");
            }
            const conversion = planArchitectureConversion(
                clip,
                target,
                context.architectureCatalog,
            );
            if (!conversion) {
                return failure(document, "invalid-architecture-conversion");
            }
            const converted = conversion.clip as CanonicalClip;
            if (
                !reconcileClipArchitectureIdentity(
                    converted,
                    context.architectureCatalog,
                )
            ) {
                return failure(document, "invalid-architecture-conversion");
            }
            document.clips[clipIndex] = converted;
            forceCrossArchitectureCutsForConversion(
                document.clips,
                context.architectureCatalog,
            );
            reconcileClipArchitectureIncomingIcLoraDrives(
                document.clips,
                clipIndex,
                context.generatedEntryMode ?? "text-to-video",
                context.architectureCatalog,
            );
            return success(document);
        }
        case "stage.add":
            return list(document, "stage", "add", command, context);
        case "stage.retarget-model": {
            const clip = findClip(document, command.clipId);
            const stage = clip?.stages.find(
                (candidate) => candidate.id === command.stageId,
            );
            if (!clip || !stage) {
                return failure(document, "missing-target");
            }
            const target = resolveArchitectureRetarget(
                command.target,
                context.architectureCatalog,
            );
            if (!target) {
                return failure(document, "architecture-invariant");
            }
            const ownerArchitectureId = modelIdentityFromCatalog(
                context.architectureCatalog,
                clip.stages[0]?.model ?? "",
            )?.architectureId;
            if (target.architectureId !== ownerArchitectureId) {
                return failure(document, "architecture-invariant");
            }
            const candidate = clone(clip);
            const candidateStage = candidate.stages.find(
                (entry) => entry.id === command.stageId,
            );
            if (!candidateStage) {
                return failure(document, "missing-target");
            }
            candidateStage.model = target.model;
            candidateStage.modelProfileId = target.modelProfileId;
            if (
                !reconcileClipArchitectureIdentity(
                    candidate,
                    context.architectureCatalog,
                )
            ) {
                return failure(document, "architecture-invariant");
            }
            document.clips[document.clips.indexOf(clip)] = candidate;
            return success(document);
        }
        case "stage.remove":
            return list(document, "stage", "remove", command, context);
        case "stage.toggle-skip": {
            const clip = findClip(document, command.clipId);
            const stageIndex =
                clip?.stages.findIndex(
                    (stage) => stage.id === command.stageId,
                ) ?? -1;
            const stage = clip?.stages[stageIndex];
            if (!clip || !stage) {
                return failure(document, "missing-target");
            }
            if (stageIndex === 0 && stage.skipped !== true) {
                return failure(document, "invalid-operation");
            }
            const skipped = !stage.skipped;
            const firstSkipped = clip.stages.findIndex(
                (candidate) => candidate.skipped === true,
            );
            const start = skipped ? stageIndex : Math.max(0, firstSkipped);
            for (let index = start; index < clip.stages.length; index++) {
                clip.stages[index].skipped = skipped;
            }
            if (
                !reconcileClipArchitectureIdentity(
                    clip,
                    context.architectureCatalog,
                )
            ) {
                return failure(clone(source), "architecture-invariant");
            }
            reconcileArchitectureIncomingIcLoraDrives(
                document.clips,
                context.generatedEntryMode ?? "text-to-video",
                context.architectureCatalog,
            );
            return success(document);
        }
        case "stage.move":
            return list(document, "stage", "move", command, context);
        case "stage.patch":
            return list(document, "stage", "patch", command, context);
        case "ref.add":
            return list(document, "ref", "add", command, context);
        case "ref.remove":
            return list(document, "ref", "remove", command, context);
        case "ref.move":
            return list(document, "ref", "move", command, context);
        case "ref.patch":
            return list(document, "ref", "patch", command, context);
        case "prompt-window.add":
            return list(document, "promptWindow", "add", command, context);
        case "prompt-window.remove":
            return list(document, "promptWindow", "remove", command, context);
        case "prompt-window.move":
            return list(document, "promptWindow", "move", command, context);
        case "prompt-window.patch":
            return list(document, "promptWindow", "patch", command, context);
        case "retake.add": {
            const clip = findClip(document, command.clipId);
            if (!clip) return failure(document, "missing-target");
            if (clip.retake) {
                return failure(document, "retake-already-exists");
            }
            const invalid = invalidNewEntity(document, command.retake);
            if (invalid) return invalid;
            clip.retake = clone(command.retake);
            return success(document);
        }
        case "retake.remove": {
            const clip = findClip(document, command.clipId);
            if (!clip || clip.retake?.id !== command.retakeId) {
                return failure(document, "missing-target");
            }
            clip.retake = null;
            return success(document);
        }
        case "retake.patch": {
            const clip = findClip(document, command.clipId);
            if (!clip || clip.retake?.id !== command.retakeId) {
                return failure(document, "missing-target");
            }
            Object.assign(clip.retake, clone(command.patch), {
                id: command.retakeId,
            });
            return success(document);
        }
        case "audio-track.add":
            return list(document, "audioTrack", "add", command, context);
        case "audio-track.remove":
            return list(document, "audioTrack", "remove", command, context);
        case "audio-track.move":
            return list(document, "audioTrack", "move", command, context);
        case "audio-track.patch":
            return list(document, "audioTrack", "patch", command, context);
        case "audio-span.add":
            return list(document, "audioSpan", "add", command, context);
        case "audio-span.remove":
            return list(document, "audioSpan", "remove", command, context);
        case "audio-span.move":
            return list(document, "audioSpan", "move", command, context);
        case "audio-span.patch":
            return list(document, "audioSpan", "patch", command, context);
        default:
            return assertNever(command);
    }
};
