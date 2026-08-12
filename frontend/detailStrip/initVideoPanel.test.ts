import { afterEach, describe, expect, it, jest } from "@jest/globals";

import { initVideoFixture } from "../__test_helpers__/clipFixtures";
import {
    detailStripHarness,
    fieldByLabel,
} from "../__test_helpers__/detailStrip";
import { lastSavedClips } from "../__test_helpers__/dom";
import { MEDIA_SOURCE_PREVIOUS_CLIP } from "../generatedMediaSource";
import { setSelection } from "../selection";
import type { Clip } from "../types";
import { closeTrimModal } from "./trimModal";

describe("source video trim", () => {
    const h = detailStripHarness();

    const SOURCE = initVideoFixture({
        fileName: "beach.mp4",
        fps: 30,
        durationSeconds: 12.4,
        startSeconds: 2.1,
        lengthSeconds: 4.2,
    });

    const openPanel = (source = SOURCE): void => {
        h.setup([{ duration: 4.2, stages: [{}], initVideo: source }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
    };

    const edgeInput = (label: string): HTMLInputElement => {
        const input =
            fieldByLabel(label).querySelector<HTMLInputElement>("input");
        if (!input) {
            throw new Error(`no input for ${label}`);
        }
        return input;
    };

    const openTrim = (): void => {
        const launch = document.querySelector<HTMLButtonElement>(
            "[data-vst-open-trim]",
        );
        if (!launch) {
            throw new Error("trim launcher missing");
        }
        launch.click();
        const player = document.querySelector<HTMLVideoElement>(
            ".vst-trim-modal-player",
        );
        if (player) {
            player.pause = jest.fn();
            player.load = jest.fn();
        }
    };

    const modalField = (name: "in" | "out" | "duration"): HTMLInputElement => {
        const input = document.querySelector<HTMLInputElement>(
            `[data-vst-trim-field="${name}"]`,
        );
        if (!input) {
            throw new Error(`${name} modal field missing`);
        }
        return input;
    };

    const applyModalField = (name: "in" | "out", value: string): void => {
        modalField(name).value = value;
        modalField(name).dispatchEvent(new Event("input", { bubbles: true }));
        document
            .querySelector<HTMLButtonElement>("[data-vst-trim-apply]")
            ?.click();
    };

    const savedSource = (): Clip["initVideo"] =>
        lastSavedClips<Clip[]>(h.saveSpy)[0].initVideo;

    afterEach(closeTrimModal);

    it("offers the previous clip output as Clip 1+ source footage", () => {
        h.setup([
            { duration: 3.2, stages: [{}] },
            { duration: 4.2, stages: [{}] },
        ]);
        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
        const source = document.querySelector<HTMLSelectElement>(
            ".vst-init-video-source",
        );

        expect(
            Array.from(source?.options ?? []).map(
                (option) => option.textContent,
            ),
        ).toContain("Previous Clip Output");

        if (!source) {
            throw new Error("source selector missing");
        }
        source.value = MEDIA_SOURCE_PREVIOUS_CLIP;
        source.dispatchEvent(new Event("change", { bubbles: true }));

        expect(lastSavedClips<Clip[]>(h.saveSpy)[1]).toMatchObject({
            duration: 3.2,
            initVideo: {
                source: MEDIA_SOURCE_PREVIOUS_CLIP,
                startSeconds: 0,
                lengthSeconds: 3.2,
            },
        });
    });

    it("shows the stored start and length as the in and out limits", () => {
        openPanel();
        openTrim();

        expect(modalField("in").value).toBe("2.1");
        expect(modalField("out").value).toBe("6.3");
        expect(modalField("duration").value).toBe("4.2");
    });

    it("keeps editable In and Out fields in the sidebar", () => {
        openPanel();

        expect(edgeInput("In (s)").value).toBe("2.1");
        expect(edgeInput("Out (s)").value).toBe("6.3");

        jest.useFakeTimers();
        const input = edgeInput("In (s)");
        input.value = "3.1";
        input.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);

        expect(savedSource()).toMatchObject({
            startSeconds: 3.1,
            lengthSeconds: 3.2,
        });

        const preview = document.querySelector<HTMLVideoElement>(
            ".vst-sidebar-video-preview",
        );
        if (!preview) {
            throw new Error("sidebar preview missing");
        }
        preview.currentTime = 2;
        preview.dispatchEvent(new Event("seeking"));
        expect(preview.currentTime).toBe(3.1);
    });

    it("shows the selected video in the sidebar without a second remove button", () => {
        openPanel();

        const preview = document.querySelector<HTMLVideoElement>(
            ".vst-sidebar-video-preview",
        );
        expect(preview).not.toBeNull();
        expect(preview?.src).toContain("data:video/mp4");
        expect(document.body.textContent).not.toContain("Remove source video");
        expect(
            document.querySelector<HTMLButtonElement>(".vst-audio-upload-clear")
                ?.hidden,
        ).toBe(false);
    });

    it("releases the sidebar player before rebuilding the panel", () => {
        openPanel();
        const preview = document.querySelector<HTMLVideoElement>(
            ".vst-sidebar-video-preview",
        );
        if (!preview) {
            throw new Error("sidebar preview missing");
        }
        preview.pause = jest.fn();
        preview.load = jest.fn();

        h.strip.render();

        expect(preview.pause).toHaveBeenCalledTimes(1);
        expect(preview.load).toHaveBeenCalledTimes(1);
        expect(preview.hasAttribute("src")).toBe(false);
    });

    it("closes the trim modal when the detail strip is disposed", () => {
        openPanel();
        openTrim();

        h.disposeStrip();

        expect(document.querySelector(".vst-trim-modal")).toBeNull();
    });

    /**
     * The chosen semantic: the in point is a left limit, not a slide. The old
     * "Start (s)" field kept the length and moved the whole window instead.
     */
    it("moves the in point without moving the out point", () => {
        openPanel();
        openTrim();
        applyModalField("in", "3.1");

        expect(savedSource()).toMatchObject({
            startSeconds: 3.1,
            lengthSeconds: 3.2,
        });
    });

    it("moves the out point without moving the in point", () => {
        openPanel();
        openTrim();
        applyModalField("out", "9.1");

        expect(savedSource()).toMatchObject({
            startSeconds: 2.1,
            lengthSeconds: 7,
        });
    });

    it("resizes the clip to the trimmed length", () => {
        openPanel();
        openTrim();
        applyModalField("out", "9.1");

        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].duration).toBe(7);
    });

    it("draws the bar over the kept part of the source", () => {
        openPanel();
        openTrim();

        const window_ = document.querySelector<HTMLElement>(".vst-trim-window");
        if (!window_) {
            throw new Error("modal trim window missing");
        }
        // 2.1 s into a 12.4 s file, 4.2 s wide.
        expect(parseFloat(window_.style.left)).toBeCloseTo(16.94, 2);
        expect(parseFloat(window_.style.width)).toBeCloseTo(33.87, 2);
    });

    /**
     * Without a probed length the bar has no truthful scale — its window would
     * fill a track it can never move within. The numbers still work.
     */
    it("omits the bar when the source length is unknown", () => {
        openPanel(
            initVideoFixture({
                fileName: "unknown.mp4",
                durationSeconds: 0,
                startSeconds: 0,
                lengthSeconds: 4,
            }),
        );

        expect(document.querySelector(".vst-trim-window")).toBeNull();
        expect(edgeInput("Out (s)").value).toBe("4");
    });

    it("reports how much of the file the clip uses", () => {
        openPanel();

        expect(
            Array.from(
                document.querySelectorAll(".vst-detail-field-hint"),
            ).some((hint) =>
                hint.textContent?.includes("Uses 4.2 s of 12.4 s"),
            ),
        ).toBe(true);
        expect(document.querySelector("[data-vst-open-trim]")).not.toBeNull();
        expect(document.querySelector(".vst-detail .vst-trim")).toBeNull();
    });
});
