import { afterEach, beforeEach, describe, expect, it } from "@jest/globals";
import {
    clampPxPerSecond,
    computeFitPxPerSecond,
    computeRegionLayout,
    DEFAULT_PX_PER_SECOND,
    MAX_PX_PER_SECOND,
    MIN_PX_PER_SECOND,
    renderTimeline,
    waveBarHeights,
    zoomAnchorScrollLeft,
    zoomAnchorTime,
} from "./timelineView";
import type { Clip } from "./types";

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
});

describe("computeRegionLayout", () => {
    it("lays clips left-to-right with cumulative offsets", () => {
        const layout = computeRegionLayout(
            [makeClip(2, 1, 0), makeClip(3, 2, 1)],
            {
                pxPerSecond: 10,
                minWidthPx: 0,
            },
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
            minWidthPx: 40,
        });
        expect(layout[0].widthPx).toBe(40);
    });

    it("flags skipped clips", () => {
        const layout = computeRegionLayout([makeClip(1, 1, 0, true)], {
            pxPerSecond: 10,
            minWidthPx: 0,
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
        expect(body.querySelectorAll(".vst-region")).toHaveLength(0);
    });

    it("renders a bare clip (no refs/stages/lora) with fallbacks", () => {
        const clip = makeClip(2, 0, 0);
        expect(() => renderTimeline(body, [clip])).not.toThrow();
        expect(body.querySelector(".vst-keys")).toBeNull();
        expect(body.querySelector(".vst-badge-audio")?.textContent).toBe(
            "Native",
        );
        expect(body.querySelector(".vst-badge-lora")).toBeNull();
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
        expect(thumb?.style.backgroundImage).toContain(
            "data:image/png;base64,QQ==",
        );
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
        const thumb = body.querySelector<HTMLElement>(".vst-region-thumb");
        // getImageOutPrefix is absent in tests, so the src is the root-relative path.
        expect(thumb?.style.backgroundImage).toContain(
            "inputs/VideoStages/abc123.png",
        );
    });

    it("renders no thumbnail when no ref has an uploaded image", () => {
        renderTimeline(body, [makeClip(2, 1, 0)]);
        expect(body.querySelector(".vst-region-thumb")).toBeNull();
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

    it("always renders an editable audio segment per clip, muting Native ones", () => {
        // The lane is always present so every clip (even Native) is clickable
        // to set/change its audio source.
        renderTimeline(body, [makeClip(2, 1, 0)]);
        const lane = body.querySelector(".vst-track-audio");
        expect(lane).not.toBeNull();
        const nativeSeg = lane?.querySelector(".vst-audio-clip");
        expect(nativeSeg).not.toBeNull();
        expect(nativeSeg?.classList.contains("vst-audio-native")).toBe(true);
        expect(nativeSeg?.getAttribute("role")).toBe("button");
        // Native segments show a call-to-action hint, not a waveform.
        expect(nativeSeg?.querySelector(".vst-audio-wave")).toBeNull();
        expect(nativeSeg?.querySelector(".vst-audio-hint")).not.toBeNull();
        expect(nativeSeg?.querySelector(".vst-audio-label")?.textContent).toBe(
            "Native",
        );

        const withAudio = {
            ...makeClip(2, 1, 0),
            audioSource: "Upload",
        } as unknown as Clip;
        renderTimeline(body, [makeClip(2, 1, 0), withAudio]);
        const segments = body
            .querySelector(".vst-track-audio")
            ?.querySelectorAll(".vst-audio-clip");
        expect(segments).toHaveLength(2);
        // Clip 0 is Native (muted, no wave); clip 1 is Upload (wave + label).
        expect(segments?.[0].classList.contains("vst-audio-native")).toBe(true);
        const uploadSeg = segments?.[1];
        expect(uploadSeg?.getAttribute("data-clip-idx")).toBe("1");
        expect(uploadSeg?.classList.contains("vst-audio-native")).toBe(false);
        expect(
            uploadSeg?.querySelectorAll(".vst-audio-wave span").length,
        ).toBeGreaterThan(0);
        expect(uploadSeg?.querySelector(".vst-audio-label")?.textContent).toBe(
            "Upload",
        );
    });

    it("marks sub-12px regions as tiny so CSS can collapse their interiors", () => {
        renderTimeline(body, [makeClip(0.1, 1, 0), makeClip(5, 1, 0)], {
            pxPerSecond: 44,
        });
        const regions = body.querySelectorAll(".vst-region");
        expect(regions[0].classList.contains("vst-region-tiny")).toBe(true);
        expect(regions[1].classList.contains("vst-region-tiny")).toBe(false);
    });

    it("renders the info readout with a labeled total and a live-updatable selection slot", () => {
        renderTimeline(body, [makeClip(2, 1, 0)]);
        const readout = body.querySelector("[data-vst-readout]");
        expect(readout?.textContent).toContain("2s total");
        // The selection slot always exists (the orchestrator pokes it on click-select) but hides empty.
        expect(
            body.querySelector<HTMLElement>("[data-vst-readout-sel]")?.hidden,
        ).toBe(true);

        renderTimeline(body, [makeClip(2, 1, 0)], { selectedIndex: 0 });
        const sel = body.querySelector<HTMLElement>("[data-vst-readout-sel]");
        expect(sel?.hidden).toBe(false);
        expect(sel?.textContent).toBe("clip 0");
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

    it("surfaces skipped-stage counts in the stage chip tooltip", () => {
        const clip = {
            duration: 2,
            refs: [],
            stages: [{}, { skipped: true }, {}],
        } as unknown as Clip;
        renderTimeline(body, [clip]);
        const chip = body.querySelector(".vst-chip");
        expect(chip?.getAttribute("title")).toBe("Stages: 3 (1 skipped)");
        expect(chip?.textContent).toBe("▤ 3");
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
