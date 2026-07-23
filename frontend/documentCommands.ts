import { reconcileClipArchitectureIdentity } from "./architectures/clipIdentity";
import {
    planArchitectureConversion,
    resolveArchitectureRetarget,
} from "./architectures/conversion/plan";
import { forceCrossArchitectureCutsForConversion } from "./architectures/policy/boundaryPolicy";
import {
    ARCHITECTURE_CONVERSION,
    ARCHITECTURE_CONVERSION_WITH_SELECTION,
    addBefore,
    clone,
    combineImpacts,
    failure,
    findClip,
    findTrack,
    hasOwn,
    invalidNewEntity,
    MOVE_STRUCTURE,
    moveBefore,
    patchById,
    REMOVE_STRUCTURE,
    removeById,
    STRUCTURE,
    success,
    VALUE,
    VALUE_CAPABILITIES,
} from "./documentCommands/helpers";
import type {
    ChangeImpact,
    CommandFailure,
    DocumentCommand,
    DocumentCommandContext,
    DocumentCommandResult,
} from "./documentCommands/types";
import type { CanonicalClip, CanonicalVideoStagesConfig } from "./types";

export { reconcileClipArchitectureIdentity } from "./architectures/clipIdentity";
export type {
    ChangeImpact,
    CommandFailure,
    DocumentCommand,
    DocumentCommandContext,
    DocumentCommandResult,
} from "./documentCommands/types";

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
            const impacts: (readonly ChangeImpact[])[] = [];
            for (const child of command.commands) {
                const result = reduceDocumentCommand(current, child, context);
                if (!result.applied) {
                    return failure(
                        clone(source),
                        result.failure as CommandFailure,
                    );
                }
                current = result.document;
                impacts.push(result.impacts);
            }
            return success(current, combineImpacts(impacts));
        }
        case "root.patch": {
            Object.assign(document, clone(command.patch));
            return success(document, VALUE_CAPABILITIES);
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
            return success(document, STRUCTURE);
        }
        case "clip.remove": {
            if (!removeById(document.clips, command.clipId)) {
                return failure(document, "missing-target");
            }
            return success(document, REMOVE_STRUCTURE);
        }
        case "clip.move": {
            if (
                !moveBefore(
                    document.clips,
                    command.clipId,
                    command.beforeClipId,
                )
            ) {
                return failure(document, "missing-target");
            }
            return success(document, MOVE_STRUCTURE);
        }
        case "clip.patch": {
            if (
                hasOwn(command.patch, "architecture") ||
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
                hasOwn(command.patch, "sourceVideo") &&
                !reconcileClipArchitectureIdentity(
                    candidate,
                    context.architectureCatalog,
                )
            ) {
                return failure(document, "architecture-invariant");
            }
            document.clips[document.clips.indexOf(clip)] = candidate;
            return success(document, VALUE_CAPABILITIES);
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
            forceCrossArchitectureCutsForConversion(document.clips);
            return success(
                document,
                conversion.selectionAffected
                    ? ARCHITECTURE_CONVERSION_WITH_SELECTION
                    : ARCHITECTURE_CONVERSION,
            );
        }
        case "stage.add": {
            const clip = findClip(document, command.clipId);
            if (!clip) return failure(document, "missing-target");
            const invalid = invalidNewEntity(document, command.stage);
            if (invalid) return invalid;
            const candidate = clone(clip);
            if (
                !addBefore(
                    candidate.stages,
                    clone(command.stage),
                    command.beforeStageId,
                )
            ) {
                return failure(clone(source), "missing-target");
            }
            if (
                !reconcileClipArchitectureIdentity(
                    candidate,
                    context.architectureCatalog,
                )
            ) {
                return failure(document, "architecture-invariant");
            }
            document.clips[document.clips.indexOf(clip)] = candidate;
            return success(document, STRUCTURE);
        }
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
            return success(document, VALUE_CAPABILITIES);
        }
        case "stage.remove": {
            const clip = findClip(document, command.clipId);
            if (!clip) {
                return failure(document, "missing-target");
            }
            const candidate = clone(clip);
            if (!removeById(candidate.stages, command.stageId)) {
                return failure(document, "missing-target");
            }
            if (
                !reconcileClipArchitectureIdentity(
                    candidate,
                    context.architectureCatalog,
                )
            ) {
                return failure(document, "architecture-invariant");
            }
            document.clips[document.clips.indexOf(clip)] = candidate;
            return success(document, REMOVE_STRUCTURE);
        }
        case "stage.move": {
            const clip = findClip(document, command.clipId);
            if (!clip) {
                return failure(document, "missing-target");
            }
            const candidate = clone(clip);
            if (
                !moveBefore(
                    candidate.stages,
                    command.stageId,
                    command.beforeStageId,
                )
            ) {
                return failure(document, "missing-target");
            }
            if (
                !reconcileClipArchitectureIdentity(
                    candidate,
                    context.architectureCatalog,
                )
            ) {
                return failure(document, "architecture-invariant");
            }
            document.clips[document.clips.indexOf(clip)] = candidate;
            return success(document, MOVE_STRUCTURE);
        }
        case "stage.patch": {
            const clip = findClip(document, command.clipId);
            if (
                hasOwn(command.patch, "model") ||
                hasOwn(command.patch, "modelProfileId")
            ) {
                return failure(document, "architecture-invariant");
            }
            if (!clip) {
                return failure(document, "missing-target");
            }
            const candidate = clone(clip);
            if (!patchById(candidate.stages, command.stageId, command.patch)) {
                return failure(document, "missing-target");
            }
            if (
                !reconcileClipArchitectureIdentity(
                    candidate,
                    context.architectureCatalog,
                )
            ) {
                return failure(document, "architecture-invariant");
            }
            document.clips[document.clips.indexOf(clip)] = candidate;
            return success(document, VALUE_CAPABILITIES);
        }
        case "ref.add": {
            const clip = findClip(document, command.clipId);
            if (!clip) return failure(document, "missing-target");
            const invalid = invalidNewEntity(document, command.ref);
            if (invalid) return invalid;
            if (
                !addBefore(clip.refs, clone(command.ref), command.beforeRefId)
            ) {
                return failure(clone(source), "missing-target");
            }
            return success(document, STRUCTURE);
        }
        case "ref.remove": {
            const clip = findClip(document, command.clipId);
            return clip && removeById(clip.refs, command.refId)
                ? success(document, REMOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "ref.move": {
            const clip = findClip(document, command.clipId);
            return clip &&
                moveBefore(clip.refs, command.refId, command.beforeRefId)
                ? success(document, MOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "ref.patch": {
            const clip = findClip(document, command.clipId);
            return clip && patchById(clip.refs, command.refId, command.patch)
                ? success(document, VALUE_CAPABILITIES)
                : failure(document, "missing-target");
        }
        case "audio-segment.add": {
            const clip = findClip(document, command.clipId);
            if (!clip) return failure(document, "missing-target");
            const invalid = invalidNewEntity(document, command.segment);
            if (invalid) return invalid;
            if (
                !addBefore(
                    clip.audioSegments,
                    clone(command.segment),
                    command.beforeSegmentId,
                )
            ) {
                return failure(clone(source), "missing-target");
            }
            return success(document, STRUCTURE);
        }
        case "audio-segment.remove": {
            const clip = findClip(document, command.clipId);
            return clip && removeById(clip.audioSegments, command.segmentId)
                ? success(document, REMOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "audio-segment.move": {
            const clip = findClip(document, command.clipId);
            return clip &&
                moveBefore(
                    clip.audioSegments,
                    command.segmentId,
                    command.beforeSegmentId,
                )
                ? success(document, MOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "audio-segment.patch": {
            const clip = findClip(document, command.clipId);
            return clip &&
                patchById(clip.audioSegments, command.segmentId, command.patch)
                ? success(document, VALUE)
                : failure(document, "missing-target");
        }
        case "prompt-window.add": {
            const clip = findClip(document, command.clipId);
            if (!clip) return failure(document, "missing-target");
            const invalid = invalidNewEntity(document, command.window);
            if (invalid) return invalid;
            if (
                !addBefore(
                    clip.promptWindows,
                    clone(command.window),
                    command.beforeWindowId,
                )
            ) {
                return failure(clone(source), "missing-target");
            }
            return success(document, STRUCTURE);
        }
        case "prompt-window.remove": {
            const clip = findClip(document, command.clipId);
            return clip && removeById(clip.promptWindows, command.windowId)
                ? success(document, REMOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "prompt-window.move": {
            const clip = findClip(document, command.clipId);
            return clip &&
                moveBefore(
                    clip.promptWindows,
                    command.windowId,
                    command.beforeWindowId,
                )
                ? success(document, MOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "prompt-window.patch": {
            const clip = findClip(document, command.clipId);
            return clip &&
                patchById(clip.promptWindows, command.windowId, command.patch)
                ? success(document, VALUE)
                : failure(document, "missing-target");
        }
        case "retake.add": {
            const clip = findClip(document, command.clipId);
            if (!clip) return failure(document, "missing-target");
            if (clip.retake) {
                return failure(document, "retake-already-exists");
            }
            const invalid = invalidNewEntity(document, command.retake);
            if (invalid) return invalid;
            clip.retake = clone(command.retake);
            return success(document, STRUCTURE);
        }
        case "retake.remove": {
            const clip = findClip(document, command.clipId);
            if (!clip || clip.retake?.id !== command.retakeId) {
                return failure(document, "missing-target");
            }
            clip.retake = null;
            return success(document, REMOVE_STRUCTURE);
        }
        case "retake.patch": {
            const clip = findClip(document, command.clipId);
            if (!clip || clip.retake?.id !== command.retakeId) {
                return failure(document, "missing-target");
            }
            Object.assign(clip.retake, clone(command.patch), {
                id: command.retakeId,
            });
            return success(document, VALUE_CAPABILITIES);
        }
        case "audio-track.add": {
            const invalid = invalidNewEntity(document, command.track);
            if (invalid) return invalid;
            if (
                !addBefore(
                    document.audioTracks,
                    clone(command.track),
                    command.beforeTrackId,
                )
            ) {
                return failure(clone(source), "missing-target");
            }
            return success(document, STRUCTURE);
        }
        case "audio-track.remove":
            return removeById(document.audioTracks, command.trackId)
                ? success(document, REMOVE_STRUCTURE)
                : failure(document, "missing-target");
        case "audio-track.move":
            return moveBefore(
                document.audioTracks,
                command.trackId,
                command.beforeTrackId,
            )
                ? success(document, MOVE_STRUCTURE)
                : failure(document, "missing-target");
        case "audio-track.patch":
            return patchById(
                document.audioTracks,
                command.trackId,
                command.patch,
            )
                ? success(document, VALUE)
                : failure(document, "missing-target");
        case "audio-span.add": {
            const track = findTrack(document, command.trackId);
            if (!track) return failure(document, "missing-target");
            const invalid = invalidNewEntity(document, command.span);
            if (invalid) return invalid;
            if (
                !addBefore(
                    track.spans,
                    clone(command.span),
                    command.beforeSpanId,
                )
            ) {
                return failure(clone(source), "missing-target");
            }
            return success(document, STRUCTURE);
        }
        case "audio-span.remove": {
            const track = findTrack(document, command.trackId);
            return track && removeById(track.spans, command.spanId)
                ? success(document, REMOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "audio-span.move": {
            const track = findTrack(document, command.trackId);
            return track &&
                moveBefore(track.spans, command.spanId, command.beforeSpanId)
                ? success(document, MOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "audio-span.patch": {
            const track = findTrack(document, command.trackId);
            return track &&
                patchById(track.spans, command.spanId, command.patch)
                ? success(document, VALUE)
                : failure(document, "missing-target");
        }
        default:
            return assertNever(command);
    }
};
