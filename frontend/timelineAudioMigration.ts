import { isAceStepFunAudioSource } from "./audioSource";
import { createEntityId } from "./identity";
import type { AudioTrack, Clip } from "./types";
import { roundToTenth } from "./utils";

/**
 * One-way compatibility bridge from the old clip-local overlay model to
 * timeline-wide lanes. Clip base audio remains clip-owned; only overlays move.
 */
export const migrateClipAudioSegmentsToTimeline = (
    clips: Clip[],
    existingTracks: AudioTrack[],
): AudioTrack[] => {
    const tracks = [...existingTracks];
    let clipStart = 0;
    for (const clip of clips) {
        for (const segment of clip.audioSegments ?? []) {
            const source =
                typeof segment.source === "string" &&
                isAceStepFunAudioSource(segment.source)
                    ? {
                          kind: "AceStepFun" as const,
                          reference: segment.source,
                          uploadedAudio: null,
                      }
                    : {
                          kind: "Upload" as const,
                          reference:
                              typeof segment.source === "object"
                                  ? (segment.source?.fileName ?? "")
                                  : "",
                          uploadedAudio:
                              typeof segment.source === "object"
                                  ? segment.source
                                  : null,
                      };
            tracks.push({
                id: createEntityId("audio_track"),
                source,
                volume: segment.volume,
                spans: [
                    {
                        id: segment.id ?? createEntityId("audio_span"),
                        firstClipId: null,
                        lastClipId: null,
                        timelineStartSeconds: roundToTenth(
                            clipStart + segment.startSeconds,
                        ),
                        timelineLengthSeconds: roundToTenth(
                            segment.lengthSeconds,
                        ),
                        sourceStartSeconds: roundToTenth(
                            segment.trimStartSeconds,
                        ),
                        clipStartOffsetSeconds: null,
                        clipLengthSeconds: null,
                    },
                ],
            });
        }
        clip.audioSegments = [];
        clipStart += Math.max(0, clip.duration || 0);
    }
    return tracks;
};
