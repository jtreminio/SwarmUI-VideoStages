import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { minimalClip } from "../__test_helpers__/clipFixtures";
import {
    audioTrackIndicesForClipWindow,
    clipTimelineWindow,
} from "../documentQueries";
import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import { normalizeAudioTracks } from "../normalizationAudio";
import { serializeStateForStorage } from "../persistence/documentCodec";
import {
    getSelection,
    resetSelectionForTests,
    setSelection,
} from "../selection";
import type { AuthoringDocument, TimelineSelection } from "../types";
import { buildAudioTracksPanel } from "./audioTracksPanel";
import type { DetailStripContext } from "./context";
import { revealRepeaterKey } from "./renderShell";
import { closeTrimModal } from "./trimModal";

const config = (): AuthoringDocument => ({
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

describe("timeline-wide audio spans panel", () => {
    let state: AuthoringDocument;
    let host: HTMLElement;
    let ctx: DetailStripContext;

    const render = (): void => {
        const selected = getSelection();
        const selection: Extract<
            TimelineSelection,
            { kind: "none" | "audio-track" }
        > = selected.kind === "audio-track" ? selected : { kind: "none" };
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

    afterEach(() => {
        closeTrimModal();
        setVideoStagesHostBridgeForTests(null);
    });

    it("reveals a track selection through the key its own panel emits", () => {
        const section = buildAudioTracksPanel(ctx, state, {
            kind: "audio-track",
            trackIdx: 0,
        });

        expect(revealRepeaterKey({ kind: "audio-track", trackIdx: 0 })).toBe(
            section.dataset.vstRepeaterKey,
        );
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
        ).toContain("A1");

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

    it("renders an uploaded source and deletes the whole track lane", () => {
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

    it("probes and stores an uploaded audio file's duration", async () => {
        host.querySelector<HTMLButtonElement>(".vst-audio-track-add")?.click();
        const probePlayer = document.createElement("video");
        probePlayer.pause = jest.fn();
        probePlayer.load = jest.fn();
        Object.defineProperty(probePlayer, "duration", { value: 12.4 });
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            createInitVideoElement: () => probePlayer,
        });
        const input = host.querySelector<HTMLInputElement>("input.auto-file");
        if (!input) {
            throw new Error("audio picker missing");
        }
        input.dataset.filedata = "data:audio/wav;base64,AA==";
        input.dataset.filename = "score.wav";
        input.dispatchEvent(new Event("change", { bubbles: true }));
        probePlayer.dispatchEvent(new Event("loadedmetadata"));
        await Promise.resolve();

        expect(state.audioTracks?.[0].source).toMatchObject({
            uploadedAudio: {
                data: "data:audio/wav;base64,AA==",
                fileName: "score.wav",
            },
            mediaDurationSeconds: 12.4,
        });
    });

    it("opens the shared trim modal and applies its source range", () => {
        state.audioTracks = [
            {
                id: "track-upload",
                source: {
                    kind: "Upload",
                    reference: "score.wav",
                    uploadedAudio: {
                        data: "data:audio/wav;base64,AA==",
                        fileName: "score.wav",
                    },
                    mediaDurationSeconds: 12,
                },
                spans: [
                    {
                        id: "span-upload",
                        timelineStartSeconds: 0,
                        timelineLengthSeconds: 5,
                        sourceStartSeconds: 1,
                    },
                ],
            },
        ];
        render();

        host.querySelector<HTMLButtonElement>("[data-vst-open-trim]")?.click();
        const audio = document.querySelector<HTMLAudioElement>(
            ".vst-trim-modal-player",
        );
        expect(audio?.tagName).toBe("AUDIO");
        expect(
            document.querySelector<HTMLInputElement>(
                '[data-vst-trim-field="in"]',
            )?.value,
        ).toBe("1.0");
        const inInput = document.querySelector<HTMLInputElement>(
            '[data-vst-trim-field="in"]',
        );
        const outInput = document.querySelector<HTMLInputElement>(
            '[data-vst-trim-field="out"]',
        );
        if (!audio || !inInput || !outInput) {
            throw new Error("audio trim controls missing");
        }
        audio.pause = jest.fn();
        audio.load = jest.fn();
        inInput.value = "2";
        inInput.dispatchEvent(new Event("input", { bubbles: true }));
        outInput.value = "5";
        outInput.dispatchEvent(new Event("input", { bubbles: true }));
        document
            .querySelector<HTMLButtonElement>("[data-vst-trim-apply]")
            ?.click();

        expect(state.audioTracks?.[0].spans[0]).toMatchObject({
            sourceStartSeconds: 2,
            timelineLengthSeconds: 3,
        });
    });

    it("offers trim for an older uploaded track without probed duration", () => {
        state.audioTracks = [
            {
                id: "track-upload",
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
                        timelineStartSeconds: 0,
                        timelineLengthSeconds: 5,
                        sourceStartSeconds: 1,
                    },
                ],
            },
        ];
        render();

        expect(host.querySelector("[data-vst-open-trim]")).not.toBeNull();
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
                    mediaDurationSeconds: 12.4,
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
                source: { mediaDurationSeconds?: number };
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
        expect(raw.audioTracks[0].source.mediaDurationSeconds).toBe(12.4);
    });

    /** Legacy multi-span tracks become independently editable lanes. */
    const twoSpanTrack = (): unknown => ({
        id: "track-multi",
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
                id: "span-first",
                timelineStartSeconds: 0,
                timelineLengthSeconds: 1,
                sourceStartSeconds: 0,
            },
            {
                id: "span-second",
                timelineStartSeconds: 3,
                timelineLengthSeconds: 2,
                sourceStartSeconds: 5,
            },
        ],
    });

    it("gives every span of a multi-span track its own authorable lane", () => {
        state.audioTracks = normalizeAudioTracks([twoSpanTrack()]);

        expect(
            state.audioTracks.map((track) => [
                track.id,
                track.spans.map((span) => span.id),
            ]),
        ).toEqual([
            ["track-multi:0", ["span-first"]],
            ["track-multi:1", ["span-second"]],
        ]);
        render();
        const tabs = Array.from(host.querySelectorAll(".vst-audio-track-tab"));
        expect(tabs).toHaveLength(2);
        expect(tabs[0].textContent).toContain("A1");
        expect(tabs[1].textContent).toContain("A2");

        const raw = JSON.parse(serializeStateForStorage(state)) as {
            audioTracks: Array<{
                id: string;
                volume: number;
                spans: Array<Record<string, unknown>>;
            }>;
        };
        expect(
            raw.audioTracks.map((track) => [track.id, track.spans.length]),
        ).toEqual([
            ["track-multi:0", 1],
            ["track-multi:1", 1],
        ]);
        expect(raw.audioTracks[1].volume).toBe(0.5);
        expect(raw.audioTracks[1].spans[0]).toMatchObject({
            timelineStartSeconds: 3,
            timelineLengthSeconds: 2,
            sourceStartSeconds: 5,
            projection: {
                firstClipId: "clip-b",
                lastClipId: "clip-b",
                clipStartOffsetSeconds: 0,
                clipEndOffsetSeconds: 2,
            },
        });
    });

    it("edits each span lane independently", () => {
        state.audioTracks = normalizeAudioTracks([twoSpanTrack()]);
        setSelection({ kind: "audio-track", trackIdx: 1 });
        render();

        const trim = fieldControl<HTMLInputElement>(
            host,
            "Trim start (s)",
            "input",
        );
        trim.value = "7";
        trim.dispatchEvent(new Event("input", { bubbles: true }));

        expect(state.audioTracks?.[1].spans[0].sourceStartSeconds).toBe(7);
        expect(state.audioTracks?.[0].spans[0].sourceStartSeconds).toBe(0);
    });

    it("deleting one span lane leaves the other spans intact", () => {
        state.audioTracks = normalizeAudioTracks([twoSpanTrack()]);
        setSelection({ kind: "audio-track", trackIdx: 0 });
        render();

        host.querySelector<HTMLButtonElement>(
            ".vst-audio-track-delete",
        )?.click();

        expect(
            state.audioTracks?.map((track) => track.spans.map((s) => s.id)),
        ).toEqual([["span-second"]]);
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

    it("shows only timeline spans intersecting the selected clip window", () => {
        const track = (
            id: string,
            start: number,
            length: number,
        ): NonNullable<AuthoringDocument["audioTracks"]>[number] => ({
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
        ).toEqual(["A2", "A3"]);
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
