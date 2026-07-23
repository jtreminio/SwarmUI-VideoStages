import { beforeEach, describe, expect, it, jest } from "@jest/globals";
import { minimalClip } from "../__test_helpers__/clipFixtures";
import { normalizeAudioTracks } from "../normalization";
import { serializeStateForStorage } from "../persistence";
import type { VideoStagesConfig } from "../types";
import { buildAudioTracksPanel } from "./audioTracksPanel";
import type { DetailStripContext } from "./context";

const config = (): VideoStagesConfig => ({
    width: 1280,
    height: 720,
    fps: 24,
    dimsExplicit: false,
    fpsExplicit: false,
    clips: [
        minimalClip({ id: "clip-a" }),
        minimalClip({ id: "clip-b" }),
        minimalClip({ id: "clip-c" }),
    ],
    audioTracks: [],
});

const fieldControl = <T extends HTMLElement>(
    root: ParentNode,
    label: string,
    selector: string,
): T => {
    const field = Array.from(
        root.querySelectorAll<HTMLElement>(".vst-audio-field"),
    ).find(
        (entry) =>
            entry.querySelector(".vst-audio-field-label")?.textContent ===
            label,
    );
    const control = field?.querySelector<T>(selector);
    if (!control) {
        throw new Error(`missing ${label} control`);
    }
    return control;
};

describe("planned multi-clip audio tracks panel", () => {
    let state: VideoStagesConfig;
    let host: HTMLElement;
    let ctx: DetailStripContext;
    let renderSpy: jest.Mock;

    beforeEach(() => {
        document.body.innerHTML = "";
        state = config();
        host = document.createElement("div");
        document.body.appendChild(host);
        renderSpy = jest.fn();
        const render = (): void => {
            renderSpy();
            host.replaceChildren(buildAudioTracksPanel(ctx, state));
        };
        ctx = {
            commitState: (mutate) => mutate(state),
            render,
        } as DetailStripContext;
        render();
    });

    it("adds, edits, spans, and deletes tracks through stable entity IDs", () => {
        expect(
            host.querySelector(".vst-audio-tracks-planned-warning")
                ?.textContent,
        ).toBe("Planned — runtime mixer not yet connected");

        host.querySelector<HTMLButtonElement>(".vst-audio-track-add")?.click();
        expect(state.audioTracks).toHaveLength(1);
        const trackId = state.audioTracks?.[0].id;
        expect(trackId).toMatch(/^audio_track_/);

        const trackRow = host.querySelector<HTMLElement>(".vst-audio-track");
        if (!trackRow) {
            throw new Error("track row missing");
        }
        const kind = fieldControl<HTMLSelectElement>(
            trackRow,
            "Source kind",
            "select",
        );
        kind.value = "AceStepFun";
        kind.dispatchEvent(new Event("change", { bubbles: true }));
        const reference = fieldControl<HTMLInputElement>(
            trackRow,
            "Source reference",
            "input",
        );
        reference.value = "audio2";
        reference.dispatchEvent(new Event("input", { bubbles: true }));

        trackRow
            .querySelector<HTMLButtonElement>(".vst-audio-track-add-span")
            ?.click();
        expect(state.audioTracks?.[0].spans).toHaveLength(1);
        const spanId = state.audioTracks?.[0].spans[0].id;
        expect(spanId).toMatch(/^audio_span_/);

        const spanRow = host.querySelector<HTMLElement>(
            ".vst-audio-track-span",
        );
        if (!spanRow) {
            throw new Error("span row missing");
        }
        const first = fieldControl<HTMLSelectElement>(
            spanRow,
            "First clip (inclusive)",
            "select",
        );
        first.value = "clip-a";
        first.dispatchEvent(new Event("change", { bubbles: true }));
        const last = fieldControl<HTMLSelectElement>(
            spanRow,
            "Last clip (inclusive)",
            "select",
        );
        last.value = "clip-c";
        last.dispatchEvent(new Event("change", { bubbles: true }));

        for (const [label, value] of [
            ["Timeline start (s)", "0.5"],
            ["Timeline length (s)", "4"],
            ["Source start (s)", "2"],
            ["Clip start offset (s)", "0.25"],
            ["Clip length (s)", "1.5"],
        ] as const) {
            const input = fieldControl<HTMLInputElement>(
                spanRow,
                label,
                "input",
            );
            input.value = value;
            input.dispatchEvent(new Event("change", { bubbles: true }));
        }

        expect(state.audioTracks?.[0]).toMatchObject({
            id: trackId,
            source: { kind: "AceStepFun", reference: "audio2" },
            spans: [
                {
                    id: spanId,
                    firstClipId: "clip-a",
                    lastClipId: "clip-c",
                    timelineStartSeconds: 0.5,
                    timelineLengthSeconds: 4,
                    sourceStartSeconds: 2,
                    clipStartOffsetSeconds: 0.25,
                    clipLengthSeconds: 1.5,
                },
            ],
        });

        spanRow
            .querySelector<HTMLButtonElement>(".vst-detail-instance-delete")
            ?.click();
        expect(state.audioTracks?.[0].spans).toEqual([]);
        host.querySelector<HTMLButtonElement>(
            ".vst-audio-track > .vst-detail-instance-head .vst-detail-instance-delete",
        )?.click();
        expect(state.audioTracks).toEqual([]);
        expect(renderSpy).toHaveBeenCalledTimes(5);
    });

    it("labels missing endpoints as open timeline bounds", () => {
        host.querySelector<HTMLButtonElement>(".vst-audio-track-add")?.click();
        host.querySelector<HTMLButtonElement>(
            ".vst-audio-track-add-span",
        )?.click();
        const spanRow = host.querySelector<HTMLElement>(
            ".vst-audio-track-span",
        ) as HTMLElement;

        const first = fieldControl<HTMLSelectElement>(
            spanRow,
            "First clip (inclusive)",
            "select",
        );
        const last = fieldControl<HTMLSelectElement>(
            spanRow,
            "Last clip (inclusive)",
            "select",
        );
        expect(first.options[0].textContent).toBe("Timeline start (open)");
        expect(last.options[0].textContent).toBe("Timeline end (open)");
    });

    it("shows preserved upload metadata while explaining planned-only behavior", () => {
        state.audioTracks = [
            {
                id: "track-upload",
                source: {
                    kind: "Upload",
                    reference: "",
                    uploadedAudio: {
                        data: "data:audio/wav;base64,AA==",
                        fileName: "score.wav",
                    },
                },
                spans: [],
            },
        ];
        ctx.render();

        expect(
            fieldControl<HTMLElement>(
                host,
                "Stored upload metadata",
                ".vst-audio-track-upload-metadata",
            ).textContent,
        ).toBe("score.wav");
        expect(host.textContent).toContain(
            "Planned — runtime mixer not yet connected",
        );
    });

    it("keeps stale clip references visible and intact while other fields change", () => {
        state.audioTracks = [
            {
                id: "track-pending",
                source: {
                    kind: "External",
                    reference: "",
                    uploadedAudio: null,
                },
                spans: [
                    {
                        id: "span-pending",
                        firstClipId: "missing-clip",
                        lastClipId: "clip-b",
                        timelineStartSeconds: 1,
                        timelineLengthSeconds: null,
                        sourceStartSeconds: 0,
                        clipStartOffsetSeconds: null,
                        clipLengthSeconds: null,
                    },
                ],
            },
        ];
        ctx.render();

        const spanRow = host.querySelector<HTMLElement>(
            ".vst-audio-track-span",
        ) as HTMLElement;
        const first = fieldControl<HTMLSelectElement>(
            spanRow,
            "First clip (inclusive)",
            "select",
        );
        expect(first.value).toBe("missing-clip");
        expect(first.selectedOptions[0].textContent).toContain("Missing clip");

        const reference = fieldControl<HTMLInputElement>(
            host,
            "Source reference",
            "input",
        );
        reference.value = "pending.wav";
        reference.dispatchEvent(new Event("input", { bubbles: true }));

        expect(state.audioTracks[0].spans[0]).toMatchObject({
            id: "span-pending",
            firstClipId: "missing-clip",
            lastClipId: "clip-b",
            timelineStartSeconds: 1,
            timelineLengthSeconds: null,
        });
    });

    it("round-trips panel-authored multi-clip spans through the root codec", () => {
        state.audioTracks = [
            {
                id: "track-score",
                source: {
                    kind: "AceStepFun",
                    reference: "audio1",
                    uploadedAudio: null,
                },
                spans: [
                    {
                        id: "span-score",
                        firstClipId: "clip-a",
                        lastClipId: "clip-c",
                        timelineStartSeconds: 0,
                        timelineLengthSeconds: 3.5,
                        sourceStartSeconds: 1,
                        clipStartOffsetSeconds: null,
                        clipLengthSeconds: null,
                    },
                ],
            },
        ];

        const raw = JSON.parse(serializeStateForStorage(state)) as {
            audioTracks: unknown;
        };
        expect(normalizeAudioTracks(raw.audioTracks)).toEqual(
            state.audioTracks,
        );
    });
});
