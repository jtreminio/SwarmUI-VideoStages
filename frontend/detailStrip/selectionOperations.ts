import {
    AUDIO_SEGMENT_DEFAULT_LENGTH,
    AUDIO_SEGMENT_MIN_LENGTH,
    clamp,
    IC_LORA_STAGE_ALL,
    RETAKE_DEFAULT_DURATION,
    RETAKE_MIN_DURATION,
    RETAKE_STRENGTH_DEFAULT,
} from "../constants";
import {
    buildDefaultStage,
    reconcileIcLoraStage,
    removeRefAt,
} from "../normalization";
import { getDefaultStageModel, getRootDefaults } from "../rootDefaults";
import { setSelection } from "../selection";
import { parseIntAttr } from "../trackDomUtils";
import type { AudioSegment, Clip, TimelineSelection } from "../types";
import { roundToTenth } from "../utils";

const STAGE_SELECTOR = "[data-vst-stage]";
const MODEL_SELECTOR = "[data-vst-model]";
const INTERACTIVE_SELECTOR = `${STAGE_SELECTOR}, ${MODEL_SELECTOR}`;

type StructuralCommit = (
    apply: (clips: Clip[]) => TimelineSelection | "render" | null,
    options?: { rebuildAfterSelect?: boolean },
) => void;

export interface DetailSelectionOperations {
    deleteRefEntry(clipIdx: number, refIdx: number): void;
    deleteWindowEntry(clipIdx: number, windowIdx: number): void;
    createRetake(clipIdx: number): void;
    removeRetake(clipIdx: number): void;
    addAudioSegment(clipIdx: number): void;
    removeAudioSegment(clipIdx: number, segIdx: number): void;
    addStage(clipIdx: number): void;
    deleteStage(clipIdx: number, stageIdx: number): void;
    selectStage(clipIdx: number, stageIdx: number): void;
    onMouseDownCapture(event: MouseEvent): void;
    onClickCapture(event: MouseEvent): void;
    onKeyDownCapture(event: KeyboardEvent): void;
    onStripKeyDown(event: KeyboardEvent): void;
}

export const createDetailSelectionOperations = (
    structuralCommit: StructuralCommit,
): DetailSelectionOperations => {
    const commitRemoval = (
        remove: (clips: Clip[]) => number | null,
        index: number,
        neighbour: (index: number) => TimelineSelection,
        fallback: TimelineSelection,
    ): void =>
        structuralCommit((clips) => {
            const remaining = remove(clips);
            if (remaining === null) {
                return null;
            }
            return remaining > 0
                ? neighbour(Math.min(index, remaining - 1))
                : fallback;
        });

    const deleteRefEntry = (clipIdx: number, refIdx: number): void => {
        commitRemoval(
            (clips) => {
                const clip = clips[clipIdx];
                return clip && removeRefAt(clip, refIdx)
                    ? clip.refs.length
                    : null;
            },
            refIdx,
            (index) => ({ kind: "ref", clipIdx, refIdx: index }),
            { kind: "clip", clipIdx, stageIdx: 0 },
        );
    };

    const deleteWindowEntry = (clipIdx: number, windowIdx: number): void => {
        commitRemoval(
            (clips) => {
                const windows = clips[clipIdx]?.promptWindows;
                if (!windows || windowIdx < 0 || windowIdx >= windows.length) {
                    return null;
                }
                windows.splice(windowIdx, 1);
                return windows.length;
            },
            windowIdx,
            (index) => ({
                kind: "prompt-minor",
                clipIdx,
                windowIdx: index,
            }),
            { kind: "prompt-major", clipIdx },
        );
    };

    const createRetake = (clipIdx: number): void => {
        structuralCommit((clips) => {
            const clip = clips[clipIdx];
            if (!clip || clip.retake) {
                return null;
            }
            const clipDuration = Math.max(0, clip.duration || 0);
            clip.retake = {
                startSeconds: 0,
                lengthSeconds: Math.max(
                    RETAKE_MIN_DURATION,
                    Math.min(
                        RETAKE_DEFAULT_DURATION,
                        clipDuration || RETAKE_DEFAULT_DURATION,
                    ),
                ),
                strength: RETAKE_STRENGTH_DEFAULT,
            };
            return { kind: "retake", clipIdx };
        });
    };

    const removeRetake = (clipIdx: number): void => {
        structuralCommit((clips) => {
            const clip = clips[clipIdx];
            if (!clip?.retake) {
                return null;
            }
            clip.retake = null;
            return { kind: "clip", clipIdx, stageIdx: 0 };
        });
    };

    const addAudioSegment = (clipIdx: number): void => {
        structuralCommit((clips) => {
            const clip = clips[clipIdx];
            if (!clip) {
                return null;
            }
            const clipDuration = Math.max(0, clip.duration || 0);
            if (clipDuration < AUDIO_SEGMENT_MIN_LENGTH) {
                return null;
            }
            const segment: AudioSegment = {
                source: null,
                startSeconds: 0,
                trimStartSeconds: 0,
                lengthSeconds: roundToTenth(
                    Math.min(AUDIO_SEGMENT_DEFAULT_LENGTH, clipDuration),
                ),
            };
            clip.audioSegments = [...(clip.audioSegments ?? []), segment];
            return {
                kind: "audio-segment",
                clipIdx,
                segIdx: clip.audioSegments.length - 1,
            };
        });
    };

    const removeAudioSegment = (clipIdx: number, segmentIdx: number): void => {
        commitRemoval(
            (clips) => {
                const clip = clips[clipIdx];
                if (!clip?.audioSegments?.[segmentIdx]) {
                    return null;
                }
                clip.audioSegments = clip.audioSegments.filter(
                    (_, index) => index !== segmentIdx,
                );
                return clip.audioSegments.length;
            },
            segmentIdx,
            (index) => ({
                kind: "audio-segment",
                clipIdx,
                segIdx: index,
            }),
            { kind: "audio", clipIdx },
        );
    };

    const selectStage = (clipIdx: number, stageIdx: number): void => {
        setSelection({ kind: "clip", clipIdx, stageIdx });
    };

    const addStage = (clipIdx: number): void => {
        structuralCommit(
            (clips) => {
                const clip = clips[clipIdx];
                if (!clip) {
                    return null;
                }
                const last = clip.stages[clip.stages.length - 1] ?? null;
                clip.stages.push(
                    buildDefaultStage(
                        getRootDefaults,
                        getDefaultStageModel,
                        last,
                        clip.refs.length,
                    ),
                );
                return {
                    kind: "clip",
                    clipIdx,
                    stageIdx: clip.stages.length - 1,
                };
            },
            { rebuildAfterSelect: true },
        );
    };

    const deleteStage = (clipIdx: number, stageIdx: number): void => {
        structuralCommit(
            (clips) => {
                const clip = clips[clipIdx];
                if (
                    !clip ||
                    clip.stages.length <= 1 ||
                    stageIdx < 0 ||
                    stageIdx >= clip.stages.length
                ) {
                    return null;
                }
                clip.stages.splice(stageIdx, 1);
                for (const entry of clip.icLoras) {
                    if (entry.stage === stageIdx) {
                        entry.stage = IC_LORA_STAGE_ALL;
                    } else if (entry.stage > stageIdx) {
                        entry.stage -= 1;
                    }
                    reconcileIcLoraStage(entry, !!clip.sourceVideo);
                }
                return {
                    kind: "clip",
                    clipIdx,
                    stageIdx: clamp(stageIdx, 0, clip.stages.length - 1),
                };
            },
            { rebuildAfterSelect: true },
        );
    };

    const handleActivation = (target: Element, shiftKey: boolean): void => {
        const stageChip = target.closest(STAGE_SELECTOR);
        if (stageChip instanceof HTMLElement) {
            const clipIdx = parseIntAttr(stageChip, "data-clip-idx");
            const stageIdx = parseIntAttr(stageChip, "data-stage-idx");
            if (clipIdx === null || stageIdx === null) {
                return;
            }
            if (shiftKey) {
                deleteStage(clipIdx, stageIdx);
            } else {
                selectStage(clipIdx, stageIdx);
            }
            return;
        }
        const modelBadge = target.closest(MODEL_SELECTOR);
        if (modelBadge instanceof HTMLElement) {
            const clipIdx = parseIntAttr(modelBadge, "data-clip-idx");
            if (clipIdx !== null) {
                selectStage(clipIdx, 0);
            }
        }
    };

    return {
        deleteRefEntry,
        deleteWindowEntry,
        createRetake,
        removeRetake,
        addAudioSegment,
        removeAudioSegment,
        addStage,
        deleteStage,
        selectStage,
        onMouseDownCapture: (event) => {
            if (
                event.target instanceof Element &&
                event.target.closest(INTERACTIVE_SELECTOR)
            ) {
                event.stopPropagation();
            }
        },
        onClickCapture: (event) => {
            if (
                !(event.target instanceof Element) ||
                !event.target.closest(INTERACTIVE_SELECTOR)
            ) {
                return;
            }
            event.stopPropagation();
            handleActivation(event.target, event.shiftKey);
        },
        onKeyDownCapture: (event) => {
            if (event.key !== "Enter" && event.key !== " ") {
                return;
            }
            if (
                !(event.target instanceof Element) ||
                !event.target.closest(INTERACTIVE_SELECTOR)
            ) {
                return;
            }
            event.preventDefault();
            event.stopPropagation();
            handleActivation(event.target, event.shiftKey);
        },
        onStripKeyDown: (event) => {
            if (
                event.key !== "Escape" ||
                (event.target instanceof Element &&
                    event.target.closest(".sui-popover"))
            ) {
                return;
            }
            event.preventDefault();
            event.stopPropagation();
            setSelection({ kind: "none" });
        },
    };
};
