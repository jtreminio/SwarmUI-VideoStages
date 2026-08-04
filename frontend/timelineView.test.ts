import { afterEach, beforeEach, describe, expect, it } from "@jest/globals";
import { testArchitectureCatalog } from "./__test_helpers__/architectureFixtures";
import { minimalClip, minimalRef } from "./__test_helpers__/clipFixtures";
import { createCapabilityViewResolver } from "./architectures/policy";
import { resolveTimelineTiming } from "./timelineTiming";
import { renderTimeline } from "./timelineView";
import {
    audioSegmentWaveBarHeights,
    clampPxPerSecond,
    computeFitPxPerSecond,
    computeRegionLayout,
    DEFAULT_PX_PER_SECOND,
    MAX_PX_PER_SECOND,
    MIN_PX_PER_SECOND,
    waveBarHeights,
    zoomAnchorScrollLeft,
    zoomAnchorTime,
} from "./timelineView/layout";
import { renderBoundarySeams } from "./timelineView/regionRenderer";
import {
    renderAudioTrackRow,
    renderPromptTrackRow,
} from "./timelineView/trackRows";
import type { AudioTrack, BoundaryOut, Clip } from "./types";

const audioTracks = (count: number): AudioTrack[] =>
    Array.from({ length: count }, (_, index) => ({
        id: `track-${index}`,
        source: {
            kind: "AceStepFun",
            reference: `audio${index}`,
            uploadedAudio: null,
        },
        spans: [
            {
                id: `span-${index}`,
                timelineStartSeconds: index * 0.5,
                timelineLengthSeconds: 1,
                sourceStartSeconds: 0,
            },
        ],
    }));

const boundaryClip = (boundaryOut: BoundaryOut = "cut"): Clip =>
    ({
        duration: 2,
        skipped: false,
        hue: 210,
        boundaryOut,
        stages: [],
        refs: [],
    }) as unknown as Clip;

const makeClip = (
    duration: number,
    stages: number,
    refs: number,
    skipped = false,
    hue = 210,
): Clip =>
    ({
        duration,
        skipped,
        hue,
        stages: Array.from({ length: stages }, () => ({})),
        refs: Array.from({ length: refs }, () => ({})),
    }) as unknown as Clip;

describe("zoom helpers", () => {
    it("clampPxPerSecond bounds to [MIN, MAX] and defaults non-finite", () => {
        expect(clampPxPerSecond(1)).toBe(MIN_PX_PER_SECOND);
        expect(clampPxPerSecond(99999)).toBe(MAX_PX_PER_SECOND);
        expect(clampPxPerSecond(50)).toBe(50);
        expect(clampPxPerSecond(Number.NaN)).toBe(DEFAULT_PX_PER_SECOND);
    });

    it("computeFitPxPerSecond fits total seconds into the container", () => {
        // (824 - 24) / 10 = 80 px/s
        expect(computeFitPxPerSecond(10, 824)).toBe(80);
    });

    it("computeFitPxPerSecond falls back for empty/zero-width timelines", () => {
        expect(computeFitPxPerSecond(0, 800)).toBe(DEFAULT_PX_PER_SECOND);
        expect(computeFitPxPerSecond(10, 0)).toBe(DEFAULT_PX_PER_SECOND);
    });

    it("computeFitPxPerSecond stays within the zoom bounds", () => {
        expect(computeFitPxPerSecond(1000, 800)).toBe(MIN_PX_PER_SECOND);
        expect(computeFitPxPerSecond(0.1, 800)).toBe(MAX_PX_PER_SECOND);
    });
});

describe("zoom anchor helpers", () => {
    it("zoomAnchorTime maps pointer x to time past the sticky header column", () => {
        // (268 + 0 - 168) / 50 = 2s
        expect(zoomAnchorTime(268, 0, 50)).toBe(2);
        // Scrolled: (268 + 100 - 168) / 50 = 4s
        expect(zoomAnchorTime(268, 100, 50)).toBe(4);
        // Pointer over the header column clamps to its right edge (time 0).
        expect(zoomAnchorTime(20, 0, 50)).toBe(0);
        expect(zoomAnchorTime(20, 100, 50)).toBe(2);
        // Degenerate zoom is safe.
        expect(zoomAnchorTime(268, 0, 0)).toBe(0);
    });

    it("zoomAnchorScrollLeft inverts zoomAnchorTime at the new zoom", () => {
        // 168 + 2*100 - 268 = 100
        expect(zoomAnchorScrollLeft(2, 100, 268)).toBe(100);
        // Pointer over the header anchors at the header edge: no spurious scroll at time 0.
        expect(zoomAnchorScrollLeft(0, 100, 20)).toBe(0);
        // Same-zoom round trip restores the original scrollLeft.
        expect(zoomAnchorScrollLeft(zoomAnchorTime(268, 40, 50), 50, 268)).toBe(
            40,
        );
    });
});

describe("waveBarHeights", () => {
    it("is deterministic: same inputs give identical bars across calls", () => {
        expect(waveBarHeights(3, 20)).toEqual(waveBarHeights(3, 20));
    });

    it("keeps every bar within the 20–100% band", () => {
        for (const h of waveBarHeights(7, 200)) {
            expect(h).toBeGreaterThanOrEqual(20);
            expect(h).toBeLessThanOrEqual(100);
        }
    });

    it("returns the requested count and guards degenerate counts", () => {
        expect(waveBarHeights(0, 12)).toHaveLength(12);
        expect(waveBarHeights(0, 0)).toEqual([]);
        expect(waveBarHeights(0, -5)).toEqual([]);
        expect(waveBarHeights(0, Number.NaN)).toEqual([]);
    });

    it("varies bars per clip so adjacent lanes don't render identically", () => {
        expect(waveBarHeights(0, 16)).not.toEqual(waveBarHeights(1, 16));
    });

    it("gives each audio segment a deterministic, clip-aware waveform", () => {
        expect(audioSegmentWaveBarHeights(2, 1, 20)).toEqual(
            audioSegmentWaveBarHeights(2, 1, 20),
        );
        expect(audioSegmentWaveBarHeights(2, 1, 20)).not.toEqual(
            audioSegmentWaveBarHeights(2, 2, 20),
        );
        expect(audioSegmentWaveBarHeights(2, 1, 20)).not.toEqual(
            audioSegmentWaveBarHeights(3, 1, 20),
        );
    });
});

describe("computeRegionLayout", () => {
    it("lays clips left-to-right with cumulative offsets", () => {
        const layout = computeRegionLayout(
            [makeClip(2, 1, 0), makeClip(3, 2, 1)],
            { pxPerSecond: 10 },
        );
        expect(layout).toHaveLength(2);
        expect(layout[0]).toMatchObject({
            index: 0,
            startSeconds: 0,
            durationSeconds: 2,
            startPx: 0,
            widthPx: 20,
            stageCount: 1,
            keyframeCount: 0,
        });
        expect(layout[1]).toMatchObject({
            index: 1,
            startSeconds: 2,
            startPx: 20,
            widthPx: 30,
            stageCount: 2,
            keyframeCount: 1,
        });
    });

    it("applies a minimum width so tiny clips stay visible", () => {
        const layout = computeRegionLayout([makeClip(0.1, 1, 0)], {
            pxPerSecond: 10,
        });
        expect(layout[0].widthPx).toBe(8);
    });

    it("flags skipped clips", () => {
        const layout = computeRegionLayout([makeClip(1, 1, 0, true)], {
            pxPerSecond: 10,
        });
        expect(layout[0].skipped).toBe(true);
    });

    it("returns an empty array for no clips", () => {
        expect(computeRegionLayout([])).toEqual([]);
    });

    it("null-guards a clip missing stages/refs instead of throwing", () => {
        const clip = { duration: 1 } as unknown as Clip;
        const layout = computeRegionLayout([clip]);
        expect(layout[0].stageCount).toBe(0);
        expect(layout[0].keyframeCount).toBe(0);
    });
});

describe("renderBoundarySeams", () => {
    const layoutFor = (clips: Clip[]) =>
        computeRegionLayout(clips, { pxPerSecond: 10 });

    it("renders one chip per interior seam and none after the last clip", () => {
        const clips = [boundaryClip(), boundaryClip(), boundaryClip()];
        const host = document.createElement("div");
        host.innerHTML = renderBoundarySeams(clips, layoutFor(clips));
        const chips = host.querySelectorAll("[data-vst-boundary-chip]");
        expect(chips).toHaveLength(2);
        expect(chips[0].getAttribute("data-left-clip-idx")).toBe("0");
        expect(chips[1].getAttribute("data-left-clip-idx")).toBe("1");
        expect(chips[0].getAttribute("title")).toContain("Click to edit.");
        expect(chips[0].getAttribute("title")).not.toContain("cycle");
    });

    it("emits nothing for a single clip", () => {
        const clips = [boundaryClip()];
        expect(renderBoundarySeams(clips, layoutFor(clips))).toBe("");
    });

    it("uses the per-mode glyph for each clip's outgoing boundary", () => {
        const clips = [
            boundaryClip("continue"),
            boundaryClip("crossfade"),
            boundaryClip("cut"),
        ];
        const host = document.createElement("div");
        host.innerHTML = renderBoundarySeams(clips, layoutFor(clips));
        const chips = Array.from(
            host.querySelectorAll<HTMLElement>("[data-vst-boundary-chip]"),
        );
        expect(chips[0].querySelector(".vst-boundary-glyph")?.textContent).toBe(
            "→",
        );
        expect(chips[0].classList.contains("vst-boundary-continue")).toBe(true);
        expect(chips[1].querySelector(".vst-boundary-glyph")?.textContent).toBe(
            "⤬",
        );
        expect(chips[1].classList.contains("vst-boundary-crossfade")).toBe(
            true,
        );
    });

    it("positions each seam chip at the following clip's start", () => {
        const clips = [boundaryClip(), boundaryClip()];
        const layouts = layoutFor(clips);
        const host = document.createElement("div");
        host.innerHTML = renderBoundarySeams(clips, layouts);
        const chip = host.querySelector<HTMLElement>(
            "[data-vst-boundary-chip]",
        );
        expect(chip?.style.left).toBe(`${layouts[1].startPx}px`);
    });
});

describe("renderTimeline (DOM)", () => {
    let body: HTMLElement;

    beforeEach(() => {
        body = document.createElement("div");
        document.body.appendChild(body);
    });

    afterEach(() => {
        body.remove();
        document.body.innerHTML = "";
    });

    it("shows the empty message when there are no clips", () => {
        renderTimeline(body, []);
        expect(body.querySelector(".vst-empty")).not.toBeNull();
        expect(body.querySelector(".vst-empty")?.textContent).toContain(
            "No clips yet",
        );
        expect(body.querySelector(".vst-empty-hint")?.textContent).toBe(
            "Use the button below to start building your sequence.",
        );
        expect(body.querySelectorAll(".vst-region")).toHaveLength(0);
    });

    it("renders a bare clip (no refs/stages/lora) with fallbacks", () => {
        const clip = makeClip(2, 0, 0);
        expect(() => renderTimeline(body, [clip])).not.toThrow();
        expect(body.querySelector(".vst-keys")).toBeNull();
        // The audio badge was dropped (redundant with the Audio track).
        expect(body.querySelector(".vst-badge-audio")).toBeNull();
        // A clip with no stages has no model badge.
        expect(body.querySelector(".vst-badge-model")).toBeNull();
        expect(body.querySelector(".vst-badge-lora")).toBeNull();
    });

    it("renders a model badge (stage 0's model) at the clip bottom-left", () => {
        const clip = {
            duration: 2,
            refs: [],
            stages: [{ model: "path/to/wan-2.2.safetensors" }],
        } as unknown as Clip;
        renderTimeline(body, [clip]);
        const badge = body.querySelector<HTMLElement>(".vst-badge-model");
        expect(badge).not.toBeNull();
        expect(badge?.textContent).toBe("wan-2.2");
        expect(badge?.getAttribute("data-clip-idx")).toBe("0");
        expect(badge?.getAttribute("title")).toContain(
            "path/to/wan-2.2.safetensors",
        );
    });

    it("omits the resize grip for a clip whose length is derived from audio/ControlNet", () => {
        const normal = makeClip(2, 1, 0);
        const audioLen = {
            ...makeClip(2, 1, 0),
            clipLengthFromAudio: true,
        } as unknown as Clip;
        const cnLen = {
            ...makeClip(2, 1, 0),
            clipLengthFromControlNet: true,
        } as unknown as Clip;
        renderTimeline(body, [normal, audioLen, cnLen]);
        const regions = body.querySelectorAll(".vst-region");
        expect(regions).toHaveLength(3);
        expect(regions[0].querySelector(".vst-region-resize")).not.toBeNull();
        expect(regions[1].querySelector(".vst-region-resize")).toBeNull();
        expect(regions[2].querySelector(".vst-region-resize")).toBeNull();
    });

    it("renders a uniform time grid plus one seam mark per interior boundary and an end tick", () => {
        renderTimeline(body, [makeClip(2, 1, 0), makeClip(3, 1, 0)]);
        expect(body.querySelectorAll(".vst-tick-grid").length).toBeGreaterThan(
            0,
        );
        expect(body.querySelectorAll(".vst-tick-seam")).toHaveLength(1);
        expect(body.querySelectorAll(".vst-tick-end")).toHaveLength(1);

        renderTimeline(body, [makeClip(1, 1, 0)]);
        expect(body.querySelectorAll(".vst-tick-seam")).toHaveLength(0);
        expect(body.querySelectorAll(".vst-tick-end")).toHaveLength(1);
    });

    it("renders a footage thumbnail from an uploaded ref image (inline data)", () => {
        const clip = {
            duration: 2,
            stages: [{}],
            refs: [{ uploadedImage: { data: "data:image/png;base64,QQ==" } }],
        } as unknown as Clip;
        renderTimeline(body, [clip]);
        const thumb = body.querySelector<HTMLElement>(".vst-region-thumb");
        expect(thumb).not.toBeNull();
        // Single ref -> one-cell filmstrip aligned left.
        expect(thumb?.dataset.cells).toBe("1");
        const start = body.querySelector<HTMLElement>(
            ".vst-region-thumb-start",
        );
        expect(start?.style.backgroundImage).toContain(
            "data:image/png;base64,QQ==",
        );
        expect(body.querySelector(".vst-region-thumb-end")).toBeNull();
    });

    it("renders a thumbnail from an inputs/ path (re-edited media, served via View)", () => {
        const clip = {
            duration: 2,
            stages: [{}],
            refs: [
                { uploadedImage: { data: "inputs/VideoStages/abc123.png" } },
            ],
        } as unknown as Clip;
        renderTimeline(body, [clip]);
        const start = body.querySelector<HTMLElement>(
            ".vst-region-thumb-start",
        );
        // getImageOutPrefix is absent in tests, so the src is the root-relative path.
        expect(start?.style.backgroundImage).toContain(
            "inputs/VideoStages/abc123.png",
        );
    });

    it("assigns thumbnail URLs through CSSOM without allowing style injection", () => {
        const clip = {
            duration: 2,
            stages: [{}],
            refs: [
                {
                    uploadedImage: {
                        data: "inputs/preview';color:red;.png",
                    },
                },
            ],
        } as unknown as Clip;
        renderTimeline(body, [clip]);
        const start = body.querySelector<HTMLElement>(
            ".vst-region-thumb-start",
        );
        expect(start?.style.color).toBe("");
        expect(start?.getAttribute("data-vst-background-image")).toBeNull();
        expect(body.querySelectorAll(".vst-region-thumb-start")).toHaveLength(
            1,
        );
    });

    it("renders a 2-cell filmstrip with an end cell for a from-end ref", () => {
        const clip = {
            duration: 2,
            stages: [{}],
            refs: [
                {
                    frame: 1,
                    fromEnd: false,
                    uploadedImage: { data: "data:image/png;base64,AA==" },
                },
                {
                    frame: 1,
                    fromEnd: true,
                    uploadedImage: { data: "data:image/png;base64,BB==" },
                },
            ],
        } as unknown as Clip;
        renderTimeline(body, [clip]);
        const thumb = body.querySelector<HTMLElement>(".vst-region-thumb");
        expect(thumb?.dataset.cells).toBe("2");
        expect(
            body.querySelector<HTMLElement>(".vst-region-thumb-start")?.style
                .backgroundImage,
        ).toContain("AA==");
        const end = body.querySelector<HTMLElement>(".vst-region-thumb-end");
        expect(end?.style.backgroundImage).toContain("BB==");
    });

    it("uses the highest-frame ref as the end cell when it differs from the earliest", () => {
        const clip = {
            duration: 2,
            stages: [{}],
            refs: [
                {
                    frame: 40,
                    fromEnd: false,
                    uploadedImage: { data: "data:image/png;base64,HI==" },
                },
                {
                    frame: 1,
                    fromEnd: false,
                    uploadedImage: { data: "data:image/png;base64,LO==" },
                },
            ],
        } as unknown as Clip;
        renderTimeline(body, [clip]);
        expect(
            body.querySelector<HTMLElement>(".vst-region-thumb-start")?.style
                .backgroundImage,
        ).toContain("LO==");
        expect(
            body.querySelector<HTMLElement>(".vst-region-thumb-end")?.style
                .backgroundImage,
        ).toContain("HI==");
    });

    it("thumbnail layer has no pointer events (never intercepts region gestures)", () => {
        const clip = {
            duration: 2,
            stages: [{}],
            refs: [{ uploadedImage: { data: "data:image/png;base64,QQ==" } }],
        } as unknown as Clip;
        renderTimeline(body, [clip]);
        const thumb = body.querySelector<HTMLElement>(".vst-region-thumb");
        // pointer-events:none lives in CSS; assert the marker class + aria-hidden here.
        expect(thumb?.getAttribute("aria-hidden")).toBe("true");
        expect(thumb?.classList.contains("vst-region-thumb")).toBe(true);
    });

    it("renders no thumbnail when no ref has an uploaded image", () => {
        renderTimeline(body, [makeClip(2, 1, 0)]);
        expect(body.querySelector(".vst-region-thumb")).toBeNull();
    });

    it("renders a hatched retake overlay with a range label when the clip has a retake", () => {
        const clip = {
            duration: 10,
            stages: [{}],
            refs: [],
            retake: { startSeconds: 2, lengthSeconds: 3, strength: 1 },
        } as unknown as Clip;
        renderTimeline(body, [clip]);
        const overlay = body.querySelector<HTMLElement>(
            ".vst-retake[data-clip-idx='0']",
        );
        expect(overlay).not.toBeNull();
        expect(overlay?.style.left).toBe("20%");
        expect(overlay?.style.width).toBe("30%");
        expect(overlay?.querySelector(".vst-retake-label")?.textContent).toBe(
            "RETAKE 2–5 s",
        );
        expect(body.querySelectorAll("[data-vst-retake-edge]")).toHaveLength(2);
    });

    it("renders no retake overlay when the clip has none", () => {
        renderTimeline(body, [makeClip(2, 1, 0)]);
        expect(body.querySelector(".vst-retake")).toBeNull();
    });

    it("shades the clip region over the retake window, like the major prompt under relay windows", () => {
        const clip = {
            duration: 10,
            stages: [{}],
            refs: [],
            retake: { startSeconds: 2, lengthSeconds: 3, strength: 1 },
        } as unknown as Clip;
        renderTimeline(body, [clip]);
        const shade = body.querySelector<HTMLElement>(
            ".vst-region .vst-region-off",
        );
        expect(shade).not.toBeNull();
        expect(shade?.style.left).toBe("20%");
        expect(shade?.style.width).toBe("30%");
    });

    it("renders no region shade when the clip has no retake", () => {
        renderTimeline(body, [makeClip(2, 1, 0)]);
        expect(body.querySelector(".vst-region-off")).toBeNull();
    });

    it("wires the + Clip affordance in both the topbar and the empty state", () => {
        let added = 0;
        const onAddClip = () => {
            added++;
        };
        renderTimeline(body, [], { onAddClip });
        const emptyAdd = body.querySelector<HTMLButtonElement>(
            ".vst-empty [data-vst-add-clip]",
        );
        expect(emptyAdd).not.toBeNull();
        emptyAdd?.click();
        expect(added).toBe(1);

        renderTimeline(body, [makeClip(2, 1, 0)], { onAddClip });
        body.querySelector<HTMLButtonElement>(
            ".vst-topbar-tools [data-vst-add-clip]",
        )?.click();
        expect(added).toBe(2);
    });

    it("stamps the live zoom on the body for gesture commits", () => {
        renderTimeline(body, [makeClip(2, 1, 0)], { pxPerSecond: 100 });
        expect(body.dataset.vstPps).toBe("100");
    });

    it("reflects the enabled state on the Enable switch and reports toggles", () => {
        const seen: boolean[] = [];
        renderTimeline(body, [makeClip(2, 1, 0)], {
            enabled: false,
            onToggleEnabled: (value) => seen.push(value),
        });
        const input = body.querySelector<HTMLInputElement>("[data-vst-enable]");
        if (!input) {
            throw new Error("Enable switch not rendered");
        }
        expect(input.checked).toBe(false);

        // The handler reads back true when the user flips it on.
        input.checked = true;
        input.dispatchEvent(new Event("change"));
        expect(seen).toEqual([true]);
    });

    it("renders the Enable switch checked and the topbar undimmed by default", () => {
        renderTimeline(body, [makeClip(2, 1, 0)]);
        expect(
            body.querySelector<HTMLInputElement>("[data-vst-enable]")?.checked,
        ).toBe(true);
        expect(
            body
                .querySelector(".vst-topbar")
                ?.classList.contains("vst-topbar-disabled"),
        ).toBe(false);
    });

    it("renders unlabeled minor ticks between the labeled grid ticks", () => {
        renderTimeline(body, [makeClip(2, 1, 0), makeClip(3, 1, 0)]);
        expect(body.querySelectorAll(".vst-tick-minor").length).toBeGreaterThan(
            0,
        );
        expect(
            body.querySelector(".vst-tick-minor .vst-tick-label"),
        ).toBeNull();
    });

    it("renders the zoom slider and % label anchored to the default zoom", () => {
        renderTimeline(body, [makeClip(2, 1, 0)], {
            pxPerSecond: DEFAULT_PX_PER_SECOND,
        });
        const slider = body.querySelector<HTMLInputElement>(
            "[data-vst-zoom-slider]",
        );
        expect(slider).not.toBeNull();
        expect(slider?.value).toBe(String(Math.round(DEFAULT_PX_PER_SECOND)));
        expect(body.querySelector("[data-vst-zoom-pct]")?.textContent).toBe(
            "100%",
        );
    });

    it("commits slider zoom on change and live-updates only the % label on input", () => {
        const zoomed: number[] = [];
        renderTimeline(body, [makeClip(2, 1, 0)], {
            onZoomSlider: (v) => zoomed.push(v),
        });
        const slider = body.querySelector<HTMLInputElement>(
            "[data-vst-zoom-slider]",
        );
        if (!slider) {
            throw new Error("slider missing");
        }
        slider.value = "88";
        slider.dispatchEvent(new Event("input"));
        expect(zoomed).toEqual([]);
        expect(body.querySelector("[data-vst-zoom-pct]")?.textContent).toBe(
            "200%",
        );
        slider.dispatchEvent(new Event("change"));
        expect(zoomed).toEqual([88]);
    });

    it("wires the undo/redo toolbar buttons", () => {
        let undos = 0;
        let redos = 0;
        renderTimeline(body, [makeClip(2, 1, 0)], {
            onUndo: () => {
                undos++;
            },
            onRedo: () => {
                redos++;
            },
        });
        body.querySelector<HTMLButtonElement>("[data-vst-undo]")?.click();
        body.querySelector<HTMLButtonElement>("[data-vst-redo]")?.click();
        expect(undos).toBe(1);
        expect(redos).toBe(1);
    });

    it("stamps each region's --clip-hue from the clip's own persistent hue", () => {
        renderTimeline(body, [
            makeClip(1, 1, 0, false, 40),
            makeClip(1, 1, 0, false, 200),
            makeClip(1, 1, 0, false, 300),
        ]);
        const regions = body.querySelectorAll<HTMLElement>(".vst-region");
        const hueOf = (el: HTMLElement): string | undefined =>
            el.getAttribute("style")?.match(/--clip-hue:(hsl\([^)]*\))/i)?.[1];
        expect(hueOf(regions[0])).toBe("hsl(40 65% 55%)");
        expect(hueOf(regions[1])).toBe("hsl(200 65% 55%)");
        expect(hueOf(regions[2])).toBe("hsl(300 65% 55%)");
    });

    it("always renders an editable audio segment per clip, tinted per source", () => {
        // The lane is always present so every clip (even Native) is clickable
        // to set/change its audio source.
        renderTimeline(body, [makeClip(2, 1, 0)]);
        const lane = body.querySelector(".vst-track-audio");
        expect(lane).not.toBeNull();
        const nativeSeg = lane?.querySelector(".vst-audio-clip");
        expect(nativeSeg).not.toBeNull();
        expect(nativeSeg?.classList.contains("vst-audio-native")).toBe(true);
        expect(nativeSeg?.classList.contains("vst-audio-kind-native")).toBe(
            true,
        );
        expect(nativeSeg?.getAttribute("role")).toBe("button");
        // Every source kind renders the full-width fake waveform.
        expect(
            nativeSeg?.querySelectorAll(".vst-audio-wave span").length,
        ).toBeGreaterThan(0);
        expect(nativeSeg?.querySelector(".vst-audio-hint")).not.toBeNull();
        expect(nativeSeg?.querySelector(".vst-audio-label")?.textContent).toBe(
            "Native",
        );

        const withAudio = {
            ...makeClip(2, 1, 0),
            audioSource: "Upload",
        } as unknown as Clip;
        const withAce = {
            ...makeClip(2, 1, 0),
            audioSource: "audio1",
        } as unknown as Clip;
        renderTimeline(body, [makeClip(2, 1, 0), withAudio, withAce]);
        const segments = body
            .querySelector(".vst-track-audio")
            ?.querySelectorAll(".vst-audio-clip");
        expect(segments).toHaveLength(3);
        // Clip 0 Native, clip 1 Upload, clip 2 AceStepFun — distinct tints.
        expect(segments?.[0].classList.contains("vst-audio-kind-native")).toBe(
            true,
        );
        const uploadSeg = segments?.[1];
        expect(uploadSeg?.getAttribute("data-clip-idx")).toBe("1");
        expect(uploadSeg?.classList.contains("vst-audio-native")).toBe(false);
        expect(uploadSeg?.classList.contains("vst-audio-kind-upload")).toBe(
            true,
        );
        expect(segments?.[2].classList.contains("vst-audio-kind-ace")).toBe(
            true,
        );
        expect(
            uploadSeg?.querySelectorAll(".vst-audio-wave span").length,
        ).toBeGreaterThan(0);
        expect(uploadSeg?.querySelector(".vst-audio-label")?.textContent).toBe(
            "Upload",
        );
    });

    it("renders deterministic waveforms and cycling tones for timeline lanes", () => {
        const clip = minimalClip({ duration: 6 });
        const html = renderAudioTrackRow(
            [clip],
            computeRegionLayout([clip]),
            undefined,
            audioTracks(6),
        );
        const host = document.createElement("div");
        host.innerHTML = html;
        const segments = host.querySelectorAll<HTMLElement>(".vst-audio-seg");

        expect(segments).toHaveLength(6);
        expect(segments[0].classList.contains("vst-audio-seg-tone-0")).toBe(
            true,
        );
        expect(segments[4].classList.contains("vst-audio-seg-tone-4")).toBe(
            true,
        );
        expect(segments[5].classList.contains("vst-audio-seg-tone-0")).toBe(
            true,
        );
        expect(
            segments[0].querySelectorAll(".vst-audio-seg-wave span"),
        ).toHaveLength(40);
        expect(segments[0].querySelector(".vst-audio-label")?.textContent).toBe(
            "audio0",
        );
        expect(
            segments[0].querySelector(".vst-audio-seg-wave")?.innerHTML,
        ).not.toBe(segments[1].querySelector(".vst-audio-seg-wave")?.innerHTML);
    });

    it("uses aligned generated width when deciding whether a region is tiny", () => {
        renderTimeline(
            body,
            [minimalClip({ duration: 0.1 }), minimalClip({ duration: 5 })],
            {
                pxPerSecond: 44,
                capabilities: createCapabilityViewResolver(
                    testArchitectureCatalog(),
                ),
            },
        );
        const regions = body.querySelectorAll(".vst-region");
        expect(regions[0].classList.contains("vst-region-tiny")).toBe(false);
        expect(regions[1].classList.contains("vst-region-tiny")).toBe(false);
    });

    it("renders the info readout with a labeled total and a live-updatable selection slot", () => {
        renderTimeline(body, [makeClip(2, 1, 0)]);
        const readout = body.querySelector("[data-vst-readout]");
        expect(readout?.textContent).toContain("2.0s output");
        expect(readout?.textContent).toContain("2.0s authored · 0.0s joins");
        // The selection slot always exists (the orchestrator pokes it on click-select) but hides empty.
        expect(
            body.querySelector<HTMLElement>("[data-vst-readout-sel]")?.hidden,
        ).toBe(true);

        renderTimeline(body, [makeClip(2, 1, 0)], { selectedIndex: 0 });
        const sel = body.querySelector<HTMLElement>("[data-vst-readout-sel]");
        expect(sel?.hidden).toBe(false);
        expect(sel?.textContent).toBe("clip 0");
    });

    it("keeps skipped tail cards on the authored ruler and marks the output endpoint", () => {
        renderTimeline(body, [
            makeClip(2, 1, 0),
            makeClip(3, 1, 0, true),
            makeClip(4, 1, 0),
        ]);

        expect(
            body.querySelector(".vst-tick-end .vst-tick-label")?.textContent,
        ).toBe("9s");
        expect(
            body.querySelector(".vst-tick-output .vst-tick-label")?.textContent,
        ).toBe("2s output");
        expect(body.querySelector("[data-vst-readout]")?.textContent).toContain(
            "2.0s output",
        );
    });

    it("places the Continue handle before the target clip", () => {
        const clips = [
            minimalClip({
                duration: 3,
                boundaryOut: "continue",
                boundaryOutOverlap: 24,
            }),
            minimalClip({ duration: 3 }),
        ];
        renderTimeline(body, clips, {
            fps: 24,
            pxPerSecond: 100,
            capabilities: createCapabilityViewResolver(
                testArchitectureCatalog(),
            ),
        });

        const regions = body.querySelectorAll<HTMLElement>(".vst-region");
        expect(Number.parseFloat(regions[0].style.left)).toBeCloseTo(0);
        expect(Number.parseFloat(regions[0].style.width)).toBeCloseTo(
            3 * 100 - 2,
        );
        expect(Number.parseFloat(regions[1].style.left)).toBeCloseTo(3 * 100);
        expect(Number.parseFloat(regions[1].style.width)).toBeCloseTo(
            (73 / 24) * 100 - 2,
        );
        const overlap = body.querySelector<HTMLElement>(
            ".vst-boundary-overlap",
        );
        expect(Number.parseFloat(overlap?.style.left ?? "")).toBeCloseTo(
            (3 - 25 / 24) * 100,
        );
        expect(Number.parseFloat(overlap?.style.width ?? "")).toBeCloseTo(
            (25 / 24) * 100,
        );
        expect(overlap?.textContent).toBe("");
        expect(Number.parseFloat(overlap?.style.left ?? "")).toBeLessThan(
            Number.parseFloat(regions[0].style.left) +
                Number.parseFloat(regions[0].style.width),
        );
        expect(
            Number.parseFloat(overlap?.style.left ?? "") +
                Number.parseFloat(overlap?.style.width ?? ""),
        ).toBeCloseTo(Number.parseFloat(regions[1].style.left));
        expect(
            Number.parseFloat(regions[0].style.left) +
                Number.parseFloat(regions[0].style.width),
        ).toBeLessThanOrEqual(Number.parseFloat(regions[1].style.left));
        expect(
            Number.parseFloat(
                body.querySelector<HTMLElement>(".vst-tick-end")?.style.left ??
                    "",
            ),
        ).toBeCloseTo((145 / 24) * 100);
        const boundaryChip = body.querySelector<HTMLElement>(
            "[data-vst-boundary-chip]",
        );
        expect(Number.parseFloat(boundaryChip?.style.left ?? "")).toBeCloseTo(
            3 * 100,
        );
        expect(boundaryChip?.dataset.vstBoundaryDensity).toBe("full");
        expect(
            boundaryChip?.querySelector(".vst-boundary-kind")?.textContent,
        ).toBe("Continue");
        expect(
            boundaryChip?.querySelector(".vst-boundary-duration")?.textContent,
        ).toBe("1.0s");
        expect(boundaryChip?.textContent).not.toContain("−");
        expect(regions[1].title).toContain(
            "4.0s generated · 3.0s timeline · 1.0s handle",
        );
        expect(
            Array.from(regions).map(
                (region) =>
                    region.querySelector(".vst-region-dur")?.textContent,
            ),
        ).toEqual(["3s", "3s"]);
        expect(
            Array.from(body.querySelectorAll(".vst-tick-label")).filter(
                (label) => label.textContent === "5s",
            ),
        ).toHaveLength(1);
        expect(body.querySelector("[data-vst-readout]")?.textContent).toContain(
            "6.0s output",
        );
        expect(body.querySelector("[data-vst-readout]")?.textContent).toContain(
            "6.0s authored · +1.0s handle · −1.0s shared",
        );
    });

    it("progressively discloses the join capsule as its rendered span narrows", () => {
        const clips = [
            minimalClip({
                duration: 3,
                boundaryOut: "continue",
                boundaryOutOverlap: 24,
            }),
            minimalClip({ duration: 3 }),
        ];
        const options = {
            fps: 24,
            capabilities: createCapabilityViewResolver(
                testArchitectureCatalog(),
            ),
        };
        const densityAt = (pxPerSecond: number): string | undefined => {
            renderTimeline(body, clips, { ...options, pxPerSecond });
            return body.querySelector<HTMLElement>("[data-vst-boundary-chip]")
                ?.dataset.vstBoundaryDensity;
        };

        expect(densityAt(100)).toBe("full");
        expect(densityAt(55)).toBe("compact");
        expect(densityAt(20)).toBe("icon");

        const capsuleMarkup = body.querySelector<HTMLElement>(
            "[data-vst-boundary-chip]",
        );
        expect(
            capsuleMarkup?.querySelector(".vst-boundary-kind")?.textContent,
        ).toBe("Continue");
        expect(
            capsuleMarkup?.querySelector(".vst-boundary-duration")?.textContent,
        ).toBe("1.0s");
        expect(capsuleMarkup?.dataset.vstBoundaryHasDuration).toBe("true");
    });

    it("shows a budget-forced cut as the effective boundary", () => {
        const clips = [
            minimalClip({
                duration: 0.1,
                boundaryOut: "continue",
                boundaryOutOverlap: 24,
            }),
            minimalClip({ duration: 0.1 }),
        ];
        renderTimeline(body, clips, {
            fps: 24,
            capabilities: createCapabilityViewResolver(
                testArchitectureCatalog(),
            ),
        });

        const chip = body.querySelector<HTMLElement>(
            "[data-vst-boundary-chip]",
        );
        expect(chip?.dataset.effectiveBoundary).toBe("cut");
        expect(chip?.classList.contains("vst-boundary-cut")).toBe(true);
        expect(chip?.classList.contains("vst-boundary-fallback")).toBe(true);
        expect(chip?.title).toContain("effective Cut");
        expect(chip?.getAttribute("aria-label")).toContain("effective Cut");
        expect(body.querySelector(".vst-boundary-overlap")).toBeNull();
    });

    it("labels aligned regions with their generated frame count", () => {
        renderTimeline(
            body,
            [minimalClip({ duration: 3 }), minimalClip({ duration: 3 })],
            { fps: 24, unit: "frames" },
        );

        expect(body.querySelector<HTMLElement>(".vst-region")?.title).toContain(
            "73f",
        );
    });

    it("bounds the audio waveform bar count (min 8, cap 400)", () => {
        const tiny = {
            ...makeClip(0.1, 1, 0),
            audioSource: "Upload",
        } as unknown as Clip;
        const huge = {
            ...makeClip(60, 1, 0),
            audioSource: "Upload",
        } as unknown as Clip;
        renderTimeline(body, [tiny, huge], {
            pxPerSecond: DEFAULT_PX_PER_SECOND,
        });
        const waves = body.querySelectorAll(".vst-audio-clip .vst-audio-wave");
        // 8px min-width region → floor(8/5.5)=1 → floored up to 8 bars.
        expect(waves[0].querySelectorAll("span")).toHaveLength(8);
        // 60s * 44px/s = 2640px → 480 raw bars → capped at 400.
        expect(waves[1].querySelectorAll("span")).toHaveLength(400);
    });

    it("renders a retake lane under each region, holding the retake window", () => {
        const withRetake = {
            duration: 4,
            refs: [],
            stages: [{}],
            retake: { startSeconds: 1, lengthSeconds: 2, strength: 1 },
        } as unknown as Clip;
        renderTimeline(body, [withRetake, makeClip(2, 1, 0)]);
        const lanes = body.querySelectorAll(".vst-retake-lane");
        expect(lanes).toHaveLength(2);
        expect(lanes[0].hasAttribute("data-vst-retake-add")).toBe(false);
        expect(lanes[0].hasAttribute("data-vst-retake-full")).toBe(true);
        expect(lanes[1].hasAttribute("data-vst-retake-add")).toBe(true);
        // The retake window lives in the LANE, not inside the region.
        const retake = body.querySelector(".vst-retake");
        expect(retake).not.toBeNull();
        expect(retake?.closest(".vst-retake-lane")).not.toBeNull();
        expect(retake?.closest(".vst-region")).toBeNull();
        // The retake-less clip's lane is empty.
        expect(lanes[1].querySelector(".vst-retake")).toBeNull();
    });

    it("renders one editable chip per stage and NO add chip (adding lives in the dock)", () => {
        const clip = {
            duration: 2,
            refs: [],
            stages: [{}, { skipped: true }, {}],
        } as unknown as Clip;
        renderTimeline(body, [clip]);
        const chips = Array.from(
            body.querySelectorAll<HTMLElement>(".vst-stage-chip"),
        );
        expect(chips).toHaveLength(3);
        expect(chips.map((c) => c.textContent)).toEqual(["S0", "⊘ S1", "S2"]);
        // The skipped stage (index 1) carries the skipped modifier.
        expect(chips[1].classList.contains("vst-stage-chip-skipped")).toBe(
            true,
        );
        expect(chips[1].getAttribute("title")).toContain("(skipped)");
        expect(chips[0].classList.contains("vst-stage-chip-skipped")).toBe(
            false,
        );
        // Stage 0 is permanent; later stages carry the delete gesture.
        expect(chips[0].getAttribute("title")).toContain("click to edit");
        expect(chips[0].getAttribute("title")).not.toContain(
            "Shift+click to delete",
        );
        expect(chips[1].getAttribute("title")).toContain(
            "Shift+click to delete",
        );
        expect(body.querySelector("[data-vst-stage-add]")).toBeNull();
    });

    it("marks the first clip as permanent in its controls and tooltip", () => {
        renderTimeline(body, [makeClip(2, 1, 0), makeClip(2, 1, 0)]);
        const regions = body.querySelectorAll<HTMLElement>(".vst-region");
        const skips = body.querySelectorAll<HTMLButtonElement>(
            '[data-vst-region-action="skip"]',
        );

        expect(regions[0].title).not.toContain("Shift+click to delete");
        expect(regions[1].title).toContain("Shift+click to delete");
        expect(regions[0].querySelector(".vst-region-controls")).toBeNull();
        expect(skips).toHaveLength(1);
        expect(
            regions[1].querySelector('[data-vst-region-action="skip"]'),
        ).not.toBeNull();
        expect(skips[0].disabled).toBe(false);
    });

    it("draws a bare arrow marker on the clip and shows the ref image on the References-track thumbnail", () => {
        const clip = {
            duration: 2,
            stages: [{}],
            refs: [
                {
                    frame: 10,
                    source: "Upload",
                    uploadedImage: { data: "data:image/png;base64,QQ==" },
                },
            ],
        } as unknown as Clip;
        renderTimeline(body, [clip]);
        // The on-clip marker is now a plain arrow: it carries no thumbnail.
        const dot = body.querySelector<HTMLElement>(".vst-key .vst-key-dot");
        expect(dot).not.toBeNull();
        expect(dot?.getAttribute("style") ?? "").not.toContain("data:image");
        // The image lives on the References-track thumbnail below the clip.
        const thumb = body.querySelector<HTMLElement>(
            ".vst-track-refs .vst-refs-thumb",
        );
        expect(thumb?.style.backgroundImage).toContain(
            "data:image/png;base64,QQ==",
        );
    });

    it("places a grid-padded tail reference at the generated endpoint", () => {
        const clip = minimalClip({
            duration: 1.05,
            refs: [minimalRef({ frame: 33 })],
        });
        renderTimeline(body, [clip], {
            fps: 24,
            capabilities: createCapabilityViewResolver(
                testArchitectureCatalog(),
            ),
        });

        expect(body.querySelector<HTMLElement>(".vst-key")?.style.left).toBe(
            "100%",
        );
        expect(
            body.querySelector<HTMLElement>(".vst-refs-mark")?.style.left,
        ).toBe("100%");
    });

    it("keeps generated marker geometry when a later authored card prevents output compaction", () => {
        const generated = minimalClip({
            duration: 1.05,
            refs: [minimalRef({ frame: 17 })],
        });
        const dormant = minimalClip({ skipped: true });
        const capabilities = createCapabilityViewResolver(
            testArchitectureCatalog(),
        );
        const timing = resolveTimelineTiming(
            [generated, dormant],
            24,
            capabilities,
        );
        const layouts = computeRegionLayout([generated, dormant], {
            pxPerSecond: 10,
            timing,
        });
        expect(timing.outputGeometryAvailable).toBe(false);
        expect(layouts[0].generatedDurationSeconds).toBe(33 / 24);
        expect(layouts[0].timelineDurationSeconds).toBe(1.05);
        expect(layouts[1].startSeconds).toBe(1.05);

        renderTimeline(body, [generated, dormant], {
            fps: 24,
            capabilities,
        });

        expect(body.querySelector<HTMLElement>(".vst-key")?.style.left).toBe(
            "51.51515151515152%",
        );
        expect(
            body.querySelector<HTMLElement>(".vst-refs-mark")?.style.left,
        ).toBe("51.51515151515152%");
    });

    it("renders the References track between the video and audio tracks", () => {
        renderTimeline(body, [makeClip(2, 1, 1)]);
        const rows = Array.from(
            body.querySelectorAll<HTMLElement>(".vst-track-row"),
        ).map((el) => el.className);
        const videoIdx = rows.findIndex((c) => c.includes("vst-track-video"));
        const refsIdx = rows.findIndex((c) => c.includes("vst-track-refs"));
        const audioIdx = rows.findIndex((c) => c.includes("vst-track-audio"));
        expect(videoIdx).toBeGreaterThanOrEqual(0);
        expect(refsIdx).toBe(videoIdx + 1);
        expect(audioIdx).toBe(refsIdx + 1);
        expect(body.querySelector(".vst-track-refs .vst-head-tags")).toBeNull();
    });

    it("renders one References mark per ref, flagging primary (cover) and from-end refs", () => {
        const clip = {
            duration: 4,
            stages: [{}],
            refs: [
                { frame: 1, source: "Refiner", fromEnd: false },
                { frame: 6, source: "Base", fromEnd: true },
            ],
        } as unknown as Clip;
        renderTimeline(body, [clip]);
        const marks = body.querySelectorAll(
            '.vst-track-refs .vst-refs-mark[data-vst-ref="thumb"]',
        );
        expect(marks).toHaveLength(2);
        expect(marks[0].className).toContain("vst-refs-primary");
        expect(marks[0].className).not.toContain("vst-refs-fromend");
        expect(marks[1].className).toContain("vst-refs-fromend");
        expect(marks[1].className).not.toContain("vst-refs-primary");
        // The marker face shows the signed frame ("R 1"; "R -N" for from-end).
        expect(marks[0].querySelector(".vst-refs-ph")?.textContent).toBe("R 1");
        expect(marks[1].querySelector(".vst-refs-ph")?.textContent).toBe(
            "R -6",
        );
        expect(marks[0].getAttribute("aria-label")).toBe(
            "Edit reference 0 (Refiner)",
        );
        expect(marks[1].getAttribute("aria-label")).toBe(
            "Edit reference 1 (Base, from end)",
        );
    });

    it("edge-aligns refs within a few frames of a clip boundary, centers the rest", () => {
        const clip = {
            duration: 4,
            stages: [{}],
            refs: [
                { frame: 2, source: "Refiner", fromEnd: false }, // near start
                { frame: 2, source: "Base", fromEnd: true }, // near end
                { frame: 40, source: "Refiner", fromEnd: false }, // mid-clip
            ],
        } as unknown as Clip;
        renderTimeline(body, [clip]);
        const marks = body.querySelectorAll<HTMLElement>(
            '.vst-track-refs .vst-refs-mark[data-vst-ref="thumb"]',
        );
        expect(marks[0].querySelector(".vst-refs-ph")?.textContent).toBe("R 2");
        expect(marks[1].querySelector(".vst-refs-ph")?.textContent).toBe(
            "R -2",
        );
        expect(marks[0].className).toContain("vst-refs-align-start");
        expect(marks[1].className).toContain("vst-refs-align-end");
        expect(marks[2].className).not.toContain("vst-refs-align");
    });

    it("keeps the frame label over an uploaded image via a footer chip", () => {
        const clip = {
            duration: 4,
            stages: [{}],
            refs: [
                {
                    frame: 3,
                    source: "Upload",
                    uploadedImage: { data: "data:image/png;base64,QQ==" },
                },
            ],
        } as unknown as Clip;
        renderTimeline(body, [clip]);
        const thumb = body.querySelector<HTMLElement>(
            ".vst-track-refs .vst-refs-thumb",
        );
        expect(thumb?.className).toContain("vst-refs-has-image");
        expect(thumb?.style.backgroundImage).toContain(
            "data:image/png;base64,QQ==",
        );
        expect(thumb?.querySelector(".vst-refs-ph")?.textContent).toBe("R 3");
    });

    it("exposes a clickable add-lane per clip in the References track", () => {
        renderTimeline(body, [makeClip(2, 1, 0), makeClip(3, 1, 0)]);
        const lanes = body.querySelectorAll(
            ".vst-track-refs .vst-refs-lane[data-vst-ref-add]",
        );
        expect(lanes).toHaveLength(2);
        expect(lanes[0].getAttribute("data-clip-idx")).toBe("0");
        expect(lanes[1].getAttribute("data-clip-idx")).toBe("1");
    });
});

describe("track-head lane tags", () => {
    it("emits lane tags with data-driven active/muted states", () => {
        const clips = [
            minimalClip({
                duration: 5,
                promptWindows: [{ start: 1, duration: 2, prompt: "push in" }],
            }),
            minimalClip({ duration: 3 }),
        ];
        const layouts = computeRegionLayout(clips, { pxPerSecond: 60 });

        // Audio: laneCount = 3 -> Src, S0, S1 active; trailing "+" muted on
        // the blank add-lane, placed via the same lane-idx var the lanes use.
        const audio = renderAudioTrackRow(
            clips,
            layouts,
            undefined,
            audioTracks(2),
        );
        expect(audio).toContain("vst-head-tag-src");
        expect(audio).toContain(">S0<");
        expect(audio).toContain(">S1<");
        expect(audio).toMatch(
            /vst-head-tag-seg vst-head-tag-muted" style="--vst-audio-lane-idx:2"/,
        );

        const prompt = renderPromptTrackRow(clips, layouts, 60, "global");
        expect(prompt).toContain("vst-head-tag-major vst-head-tag-active");
        expect(prompt).toContain("vst-head-tag-relay vst-head-tag-active");
    });

    it("leaves the relay tag inactive when no clip has prompt windows", () => {
        const bare = [minimalClip({ duration: 3 })];
        const html = renderPromptTrackRow(
            bare,
            computeRegionLayout(bare, { pxPerSecond: 60 }),
            60,
            "global",
        );
        expect(html).toContain("vst-head-tag-relay");
        expect(html).not.toContain("vst-head-tag-relay vst-head-tag-active");
    });
});

describe("timeline-wide audio segment lanes", () => {
    it("renders one global lane whose window crosses clip seams", () => {
        const clips = [
            minimalClip({ duration: 3 }),
            minimalClip({ duration: 4 }),
        ];
        renderTimeline(document.body, clips, {
            pxPerSecond: 100,
            audioTracks: [
                {
                    id: "track-score",
                    volume: 0.75,
                    source: {
                        kind: "Upload",
                        reference: "score.wav",
                        uploadedAudio: null,
                    },
                    spans: [
                        {
                            id: "span-score",
                            timelineStartSeconds: 2,
                            timelineLengthSeconds: 4,
                            sourceStartSeconds: 1,
                        },
                    ],
                },
            ],
        });

        const lane = document.querySelector<HTMLElement>(
            '.vst-audio-seg-lane[data-track-idx="0"]',
        );
        const segment = lane?.querySelector<HTMLElement>(
            '.vst-audio-seg[data-track-idx="0"]',
        );
        expect(lane?.style.left).toBe("0px");
        expect(Number.parseFloat(lane?.style.width ?? "")).toBeCloseTo(
            (170 / 24) * 100,
        );
        expect(segment?.hasAttribute("data-clip-idx")).toBe(false);
        expect(Number.parseFloat(segment?.style.left ?? "")).toBeCloseTo(
            (2 / (170 / 24)) * 100,
            5,
        );
        expect(Number.parseFloat(segment?.style.width ?? "")).toBeCloseTo(
            (4 / (170 / 24)) * 100,
            5,
        );
        expect(document.querySelectorAll(".vst-audio-seg-lane")).toHaveLength(
            2,
        );
        expect(document.body.innerHTML).toContain(">S0<");
    });
});

describe("authoring diagnostics", () => {
    it("renders backend-aligned messages above the timeline", () => {
        renderTimeline(document.body, [minimalClip()], {
            diagnostics: [
                {
                    severity: "error",
                    code: "retake-source-required",
                    message: "Retake requires an init-video clip.",
                    clipIdx: 0,
                },
            ],
        });
        const item = document.querySelector<HTMLElement>(
            '[data-vst-diagnostic="retake-source-required"]',
        );
        expect(item?.textContent).toContain("Clip 0:");
        expect(item?.textContent).toContain(
            "Retake requires an init-video clip.",
        );
        expect(item?.className).toContain("vst-diagnostic-error");
    });
});

describe("unsupported persisted timeline controls", () => {
    it("keeps repair targets operable while disabling empty creation lanes", () => {
        const catalog = testArchitectureCatalog();
        catalog.architectures[0].capabilities.features = [];
        // Audio sourcing is stated by the kinds, not by the feature list.
        catalog.architectures[0].capabilities.audioSourceKinds = ["Disabled"];
        const persisted = minimalClip({
            prompt: "persisted major",
            promptWindows: [
                {
                    prompt: "persisted relay",
                    start: 0,
                    duration: 1,
                },
            ],
            audioSource: "Upload",
            uploadedAudio: {
                data: "data:audio/wav;base64,AAAA",
                fileName: "persisted.wav",
            },
            refs: [minimalRef({ frame: 1 })],
            retake: {
                startSeconds: 0,
                lengthSeconds: 1,
                strength: 0.5,
            },
        });
        const empty = minimalClip();

        renderTimeline(document.body, [persisted, empty], {
            capabilities: createCapabilityViewResolver(catalog),
        });

        for (const selector of [
            '[data-vst-prompt="major"]',
            '[data-vst-prompt="minor"]',
            '[data-vst-audio="clip"]',
            '[data-vst-ref="thumb"]',
            "[data-vst-retake]",
        ]) {
            expect(
                document.querySelector(selector)?.hasAttribute("aria-disabled"),
            ).toBe(false);
        }
        expect(
            document
                .querySelector('.vst-refs-lane[data-clip-idx="0"]')
                ?.hasAttribute("aria-disabled"),
        ).toBe(false);
        expect(
            document
                .querySelector('.vst-retake-lane[data-clip-idx="0"]')
                ?.hasAttribute("aria-disabled"),
        ).toBe(false);
        expect(
            document
                .querySelector('.vst-refs-lane[data-clip-idx="1"]')
                ?.getAttribute("aria-disabled"),
        ).toBe("true");
        // Nothing to repair on the empty clip, so its retake lane is dropped outright.
        expect(
            document.querySelector('.vst-retake-lane[data-clip-idx="1"]'),
        ).toBeNull();
        const emptyAudio = document.querySelector(
            '.vst-audio-clip[data-clip-idx="1"]',
        );
        expect(emptyAudio?.getAttribute("aria-disabled")).toBe("true");
        expect(emptyAudio?.hasAttribute("role")).toBe(false);
        expect(emptyAudio?.hasAttribute("tabindex")).toBe(false);
        expect(emptyAudio?.getAttribute("aria-label")).toBe(
            "Audio unavailable for clip 1",
        );
    });
});
