import { beforeEach, describe, expect, it } from "@jest/globals";
import { minimalClip } from "../__test_helpers__/clipFixtures";
import {
    audioTrackIndicesForClipWindow,
    clipTimelineWindow,
} from "../documentQueries";
import { normalizeAudioTracks } from "../normalization";
import { serializeStateForStorage } from "../persistence";
import { getSelection, resetSelectionForTests } from "../selection";
import type { TimelineSelection, VideoStagesConfig } from "../types";
import { buildAudioTracksPanel } from "./audioTracksPanel";
import type { DetailStripContext } from "./context";
import { revealRepeaterKey } from "./renderShell";

const config = (): VideoStagesConfig => ({
    schemaVersion: 4,
    width: 1280,
    height: 720,
    fps: 24,
    dimsExplicit: false,
    clips: [
        minimalClip({ id: "clip-a", duration: 3 }),
        minimalClip({ id: "clip-b", duration: 4 }),
    ],
    audioTracks: [],
});

const fieldControl = <T extends HTMLElement>(
    root: ParentNode,
    label: string,
    selector: string,
): T => {
    const field = Array.from(
        root.querySelectorAll<HTMLElement>(".vst-detail-field"),
    ).find(
        (entry) =>
            entry.querySelector(".vst-detail-field-label")?.textContent ===
            label,
    );
    const control = field?.querySelector<T>(selector);
    if (!control) {
        throw new Error(`missing ${label} control`);
    }
    return control;
};

describe("timeline-wide audio segments panel", () => {
    let state: VideoStagesConfig;
    let host: HTMLElement;
    let ctx: DetailStripContext;

    const render = (): void => {
        const selected = getSelection();
        const selection: Extract<
            TimelineSelection,
            { kind: "none" | "audio-track" | "audio-track-span" }
        > =
            selected.kind === "audio-track" ||
            selected.kind === "audio-track-span"
                ? selected
                : { kind: "none" };
        host.replaceChildren(buildAudioTracksPanel(ctx, state, selection));
    };

    beforeEach(() => {
        resetSelectionForTests();
        state = config();
        host = document.createElement("div");
        document.body.replaceChildren(host);
        ctx = {
            commitState: (mutate) => mutate(state),
            debouncedCommitState: (_key, mutate) => mutate(state),
            render,
        } as DetailStripContext;
        render();
    });

    it("reveals a span selection through the key its own panel emits", () => {
        const section = buildAudioTracksPanel(ctx, state, {
            kind: "audio-track-span",
            trackIdx: 0,
            spanIdx: 0,
        });

        expect(revealRepeaterKey({ kind: "audio-track", trackIdx: 0 })).toBe(
            section.dataset.vstRepeaterKey,
        );
        expect(
            revealRepeaterKey({
                kind: "audio-track-span",
                trackIdx: 0,
                spanIdx: 0,
            }),
        ).toBe(section.dataset.vstRepeaterKey);
    });

    it("adds one executable timeline lane and edits its source/window", () => {
        host.querySelector<HTMLButtonElement>(".vst-audio-track-add")?.click();

        expect(state.audioTracks).toHaveLength(1);
        expect(getSelection()).toEqual({ kind: "audio-track", trackIdx: 0 });
        expect(state.audioTracks?.[0]).toMatchObject({
            volume: 1,
            source: { kind: "Upload", uploadedAudio: null },
            spans: [
                {
                    timelineStartSeconds: 0,
                    timelineLengthSeconds: 2,
                    sourceStartSeconds: 0,
                },
            ],
        });
        expect(host.textContent).not.toContain("Planned");
        expect(
            host.querySelector(".vst-audio-track-tab")?.textContent,
        ).toContain("S0");

        expect(
            fieldControl<HTMLSelectElement>(host, "Source", "select").value,
        ).toBe("Upload");

        const start = fieldControl<HTMLInputElement>(
            host,
            "Timeline start (s)",
            "input",
        );
        start.value = "2.5";
        start.dispatchEvent(new Event("input", { bubbles: true }));
        const trim = fieldControl<HTMLInputElement>(
            host,
            "Trim start (s)",
            "input",
        );
        trim.value = "1";
        trim.dispatchEvent(new Event("input", { bubbles: true }));
        const length = fieldControl<HTMLInputElement>(
            host,
            "Length (s)",
            "input",
        );
        length.value = "3";
        length.dispatchEvent(new Event("input", { bubbles: true }));

        expect(state.audioTracks?.[0].spans[0]).toMatchObject({
            timelineStartSeconds: 2.5,
            timelineLengthSeconds: 3,
            sourceStartSeconds: 1,
        });
    });

    it("renders an uploaded source and deletes the whole segment lane", () => {
        state.audioTracks = [
            {
                id: "track-upload",
                volume: 0.5,
                source: {
                    kind: "Upload",
                    reference: "score.wav",
                    uploadedAudio: {
                        data: "data:audio/wav;base64,AA==",
                        fileName: "score.wav",
                    },
                },
                spans: [
                    {
                        id: "span-upload",
                        timelineStartSeconds: 1,
                        timelineLengthSeconds: 5,
                        sourceStartSeconds: 0,
                    },
                ],
            },
        ];
        render();
        expect(host.textContent).toContain("score.wav");

        host.querySelector<HTMLButtonElement>(
            ".vst-audio-track-delete",
        )?.click();
        expect(state.audioTracks).toEqual([]);
    });

    it("round-trips volume and the timeline window through the root codec", () => {
        state.audioTracks = [
            {
                id: "track-score",
                volume: 0.75,
                source: {
                    kind: "AceStepFun",
                    reference: "audio1",
                    uploadedAudio: null,
                },
                spans: [
                    {
                        id: "span-score",
                        timelineStartSeconds: 2,
                        timelineLengthSeconds: 4.5,
                        sourceStartSeconds: 1,
                    },
                ],
            },
        ];

        const raw = JSON.parse(serializeStateForStorage(state)) as {
            audioTracks: Array<{
                spans: Array<{ projection: Record<string, unknown> }>;
            }>;
        };
        expect(normalizeAudioTracks(raw.audioTracks)).toEqual(
            state.audioTracks,
        );
        expect(raw.audioTracks[0].spans[0].projection).toEqual({
            firstClipId: "clip-a",
            lastClipId: "clip-b",
            clipStartOffsetSeconds: 2,
            clipEndOffsetSeconds: 3.5,
        });
    });

    it("uses native Swarm slider wiring for volume", () => {
        state.audioTracks = [
            {
                id: "track-volume",
                volume: 1,
                source: {
                    kind: "AceStepFun",
                    reference: "audio0",
                    uploadedAudio: null,
                },
                spans: [
                    {
                        id: "span-volume",
                        timelineStartSeconds: 0,
                        timelineLengthSeconds: 2,
                        sourceStartSeconds: 0,
                    },
                ],
            },
        ];
        render();
        expect(host.querySelector("input.auto-slider-range")).not.toBeNull();
        expect(host.querySelector("input.auto-slider-number")).not.toBeNull();
    });

    it("shows only timeline segments intersecting the selected clip window", () => {
        const track = (
            id: string,
            start: number,
            length: number,
        ): NonNullable<VideoStagesConfig["audioTracks"]>[number] => ({
            id,
            volume: 1,
            source: {
                kind: "Upload",
                reference: "",
                uploadedAudio: null,
            },
            spans: [
                {
                    id: `span-${id}`,
                    timelineStartSeconds: start,
                    timelineLengthSeconds: length,
                    sourceStartSeconds: 0,
                },
            ],
        });
        state.audioTracks = [
            track("clip-a-only", 0, 1),
            track("crosses-boundary", 2, 2),
            track("clip-b-only", 3, 1),
            track("ends-at-boundary", 1, 2),
            track("starts-at-timeline-end", 7, 1),
        ];

        expect(audioTrackIndicesForClipWindow(state, 0)).toEqual([0, 1, 3]);
        expect(audioTrackIndicesForClipWindow(state, 1)).toEqual([1, 2]);

        const clipOneIndices = audioTrackIndicesForClipWindow(state, 1);
        host.replaceChildren(
            buildAudioTracksPanel(
                ctx,
                state,
                { kind: "none" },
                {
                    trackIndices: clipOneIndices,
                    clipWindow: clipTimelineWindow(state.clips, 1) ?? undefined,
                },
            ),
        );
        expect(
            Array.from(
                host.querySelectorAll<HTMLElement>(
                    ".vst-audio-track-tab .header-label",
                ),
            ).map((tab) => tab.textContent?.trim()),
        ).toEqual(["S1", "S2"]);
    });

    it("adds a segment from a clip audio panel inside that clip's window", () => {
        const window = clipTimelineWindow(state.clips, 1);
        host.replaceChildren(
            buildAudioTracksPanel(
                ctx,
                state,
                { kind: "none" },
                {
                    trackIndices: [],
                    clipWindow: window ?? undefined,
                },
            ),
        );

        host.querySelector<HTMLButtonElement>(".vst-audio-track-add")?.click();

        expect(state.audioTracks?.[0].spans[0]).toMatchObject({
            timelineStartSeconds: 3,
            timelineLengthSeconds: 2,
        });
    });
});
