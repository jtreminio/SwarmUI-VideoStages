import { reconcileArchitectureIncomingIcLoraDrives } from "./architectures/behaviorRegistry";
import { reconcileClipArchitectureIdentity } from "./architectures/clipIdentity";
import type { AuthoringTransactionSnapshot } from "./authoringSnapshot";
import { applySkipSuffix } from "./clipSemantics";
import { MEDIA_SOURCE_PREVIOUS_CLIP } from "./generatedMediaSource";
import { applyAudioOnlyLeadBase } from "./normalizationClip";
import { applyClipDurationResize } from "./timelineEdit";
import type { BoundaryOut, Clip } from "./types";

export const SOURCE_VIDEO_RELATIONSHIP = "source-video";
export const SOURCE_AUDIO_RELATIONSHIP = "source-audio";

export type ClipRelationship =
    | BoundaryOut
    | typeof SOURCE_VIDEO_RELATIONSHIP
    | typeof SOURCE_AUDIO_RELATIONSHIP;

export const clipRelationship = (
    clips: readonly Clip[],
    leftClipIdx: number,
): ClipRelationship => {
    const left = clips[leftClipIdx];
    const right = clips[leftClipIdx + 1];
    if (!left || !right) {
        return left?.boundaryOut ?? "cut";
    }
    if (leftClipIdx === 0 && left.audioOnly === true) {
        return SOURCE_AUDIO_RELATIONSHIP;
    }
    if (right.initVideo?.source === MEDIA_SOURCE_PREVIOUS_CLIP) {
        return SOURCE_VIDEO_RELATIONSHIP;
    }
    return left.boundaryOut;
};

export const setPreviousClipVideoSource = (
    clips: Clip[],
    targetClipIdx: number,
    enabled: boolean,
    transaction: AuthoringTransactionSnapshot,
    fps: number,
): boolean => {
    const target = clips[targetClipIdx];
    const previous = clips[targetClipIdx - 1];
    if (!target || !previous) {
        return false;
    }
    if (enabled) {
        const duration = previous.duration;
        target.initVideo = {
            source: MEDIA_SOURCE_PREVIOUS_CLIP,
            data: "",
            fileName: null,
            fps,
            durationSeconds: duration,
            startSeconds: 0,
            lengthSeconds: duration,
        };
        applyClipDurationResize(target, duration, transaction.defaults, fps);
    } else if (target.initVideo?.source === MEDIA_SOURCE_PREVIOUS_CLIP) {
        target.initVideo = null;
    } else {
        return false;
    }
    reconcileClipArchitectureIdentity(target, transaction.capabilities.catalog);
    reconcileArchitectureIncomingIcLoraDrives(
        clips,
        transaction.generatedEntryMode,
        transaction.capabilities.catalog,
    );
    return true;
};

export const setAudioOnlyLead = (
    clips: Clip[],
    enabled: boolean,
    transaction: AuthoringTransactionSnapshot,
    fps: number,
): boolean => {
    const lead = clips[0];
    if (!lead) {
        return false;
    }
    lead.audioOnly = enabled;
    if (enabled) {
        applySkipSuffix(lead.stages, 1, true);
        setPreviousClipVideoSource(clips, 1, false, transaction, fps);
    }
    applyAudioOnlyLeadBase(clips);
    return true;
};
