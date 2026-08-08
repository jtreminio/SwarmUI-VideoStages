import { describe, expect, it, jest } from "@jest/globals";
import { resetArchitectureCatalogForTests } from "../__test_helpers__/architectureCatalog";
import {
    testArchitectureCapabilities,
    testArchitectureCatalog,
    testArchitectureCatalogDto,
} from "../__test_helpers__/architectureFixtures";
import {
    type ClipFixture,
    commitNumber,
    crumbText,
    detailStripHarness,
    fieldByLabel,
    minorRows,
    RETAKE_SOURCE,
    refRow,
    retakeFieldByLabel,
    sliderNumberByLabel,
} from "../__test_helpers__/detailStrip";
import { lastSavedClips, mountPromptBox } from "../__test_helpers__/dom";
import { loadAuthoritativeArchitectureCatalog } from "../architectures/catalog";
import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import * as persistence from "../persistence/repository";
import { getSelection, setSelection } from "../selection";
import type { Clip } from "../types";

describe("detail strip draft queue", () => {
    const h = detailStripHarness();

    // Emulate the prod render trigger a save produces: a rebuild WOULD be
    // visible here if the value-only hint leaked, because the node would swap.
    const wireLiveRenders = (): void => {
        persistence
            .getTimelineStore()
            .subscribe((_state, meta) => h.strip.render(meta));
    };

    it("live-applies a discrete model command through the document store", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const select =
            fieldByLabel("Model").querySelector<HTMLSelectElement>("select");
        if (!select) {
            throw new Error("model select missing");
        }
        select.value = "ltx-2.3-alt.safetensors";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        expect(persistence.getClips()[0].stages[0].model).toBe(
            "ltx-2.3-alt.safetensors",
        );
    });

    it("debounces a continuous slider change and flushes it through saveClips", () => {
        h.setup([{ duration: 4, stages: [{ steps: 8 }] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        jest.useFakeTimers();
        const steps = sliderNumberByLabel("Steps");
        steps.value = "14";
        steps.dispatchEvent(new Event("input", { bubbles: true }));
        expect(h.saveSpy).not.toHaveBeenCalled();
        jest.advanceTimersByTime(200);
        expect(h.saveSpy).toHaveBeenCalledTimes(1);
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].steps).toBe(14);
    });

    it("drops a pending change when the carrier went stale", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const select =
            fieldByLabel("Model").querySelector<HTMLSelectElement>("select");
        if (!select) {
            throw new Error("model select missing");
        }
        // Something else mutates the carrier: the token is now stale.
        mountPromptBox("changed by someone else");
        select.value = "ltx-2.3-alt.safetensors";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        expect(h.saveSpy).not.toHaveBeenCalled();
    });

    // In production a value save commits through the store, whose notification
    // drives videoStagesTimeline.renderAll(meta) → detailStrip.render(meta)
    // SYNCHRONOUSLY. A rebuild there would innerHTML-wipe the dock and drop the
    // caret; the value primitives mark their saves valueOnly, which arrives as
    // meta.hint === "value-only" and holds the dock DOM.
    describe("value-only commits keep the dock DOM", () => {
        it("keeps focus and the same node on a first Begin/End change, and repaints", () => {
            h.setup([
                {
                    duration: 12,
                    stages: [{}],
                    windows: [
                        { start: 1, duration: 2, prompt: "w0" },
                        { start: 6, duration: 2, prompt: "w1" },
                    ],
                },
            ]);
            wireLiveRenders();
            setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
            const end = minorRows()[0].querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="minor-0-end"]',
            );
            if (!end) {
                throw new Error("end input missing");
            }
            commitNumber(end, "4");
            // The exact same input node is still in the DOM and still focused —
            // never rebuilt out from under the caret.
            expect(
                minorRows()[0].querySelector(
                    'input[data-vst-focus-key="minor-0-end"]',
                ),
            ).toBe(end);
            expect(document.activeElement).toBe(end);
            // The data was written and the commit notification fired (the
            // timeline repaint driver).
            expect(
                lastSavedClips<Clip[]>(h.saveSpy)[0].promptWindows[0].duration,
            ).toBe(3);
            expect(h.refreshSpy).toHaveBeenCalled();
            // The value-derived breadcrumb was synced WITHOUT a rebuild.
            expect(crumbText()).toBe("Relay 1–4s · Clip 0");
        });

        it("keeps focus and the same node on a first Duration change", () => {
            h.setup([{ duration: 4, stages: [{}] }]);
            wireLiveRenders();
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const dur =
                fieldByLabel("Duration (s)").querySelector<HTMLInputElement>(
                    "input",
                );
            if (!dur) {
                throw new Error("duration input missing");
            }
            commitNumber(dur, "6");
            expect(fieldByLabel("Duration (s)").querySelector("input")).toBe(
                dur,
            );
            expect(document.activeElement).toBe(dur);
            expect(lastSavedClips<Clip[]>(h.saveSpy)[0].duration).toBe(6);
        });

        it("keeps focus and the same node on a first Steps change", () => {
            h.setup([{ duration: 4, stages: [{ steps: 8 }] }]);
            wireLiveRenders();
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const steps = sliderNumberByLabel("Steps");
            commitNumber(steps, "14");
            expect(sliderNumberByLabel("Steps")).toBe(steps);
            expect(document.activeElement).toBe(steps);
            expect(lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].steps).toBe(
                14,
            );
        });

        it("keeps incoming Continue numbering after a reference value edit", async () => {
            const catalog = testArchitectureCatalog();
            catalog.architectures[0].capabilities =
                testArchitectureCapabilities({
                    features: [
                        ...testArchitectureCapabilities().features,
                        "clipReferences",
                    ],
                });
            const constraints =
                catalog.architectures[0].boundaryRules.continue.constraints;
            if (!constraints) {
                throw new Error("continue constraints missing");
            }
            constraints.continueMode = "reference";
            constraints.continuityExtraFrames = 0;
            resetArchitectureCatalogForTests();
            setVideoStagesHostBridgeForTests({
                ...createDefaultVideoStagesHostBridge(),
                requestJson: async () => testArchitectureCatalogDto(catalog),
            });
            await loadAuthoritativeArchitectureCatalog();
            h.setup([
                {
                    duration: 4,
                    boundaryOut: "continue",
                    stages: [{}],
                },
                {
                    duration: 4,
                    stages: [{}],
                    references: [{ kind: "video", mediaScale: 1 }],
                },
            ]);
            wireLiveRenders();
            setSelection({ kind: "clip-ref", clipIdx: 1, referenceIdx: 0 });
            expect(crumbText()).toBe("<Video 2> · Clip 1");
            const scale =
                fieldByLabel(
                    "Reference scale",
                ).querySelector<HTMLSelectElement>("select");
            if (!scale) {
                throw new Error("reference scale missing");
            }
            scale.value = "0.5";
            scale.dispatchEvent(new Event("change", { bubbles: true }));

            expect(crumbText()).toBe("<Video 2> · Clip 1");
            expect(
                lastSavedClips<Clip[]>(h.saveSpy)[1].references[0].mediaScale,
            ).toBe(0.5);

            document
                .querySelector<HTMLElement>(".vst-clip-ref-join-tab")
                ?.click();
            expect(getSelection()).toEqual({
                kind: "boundary-ref",
                leftClipIdx: 0,
            });
            expect(crumbText()).toBe(
                "<Video 1> (from Join with Clip 0) · Clip 1",
            );
            const joinScale =
                fieldByLabel(
                    "Reference scale",
                ).querySelector<HTMLSelectElement>("select");
            const soundtrack = fieldByLabel(
                "Include soundtrack",
            ).querySelector<HTMLInputElement>('input[type="checkbox"]');
            if (!joinScale || !soundtrack) {
                throw new Error("incoming reference controls missing");
            }
            joinScale.value = "0.25";
            joinScale.dispatchEvent(new Event("change", { bubbles: true }));
            soundtrack.checked = false;
            soundtrack.dispatchEvent(new Event("change", { bubbles: true }));

            expect(getSelection()).toEqual({
                kind: "boundary-ref",
                leftClipIdx: 0,
            });
            expect(crumbText()).toBe(
                "<Video 1> (from Join with Clip 0) · Clip 1",
            );
            expect(lastSavedClips<Clip[]>(h.saveSpy)[0]).toMatchObject({
                boundaryOutReferenceScale: 0.25,
                boundaryOutReferenceIncludeSoundtrack: false,
            });
            const stored = JSON.parse(
                document.querySelector<HTMLTextAreaElement>(
                    "#input_videostages",
                )?.value ?? "{}",
            );
            expect(stored.clips[0]).toMatchObject({
                boundaryOutReferenceScale: 0.25,
                boundaryOutReferenceIncludeSoundtrack: false,
            });
            expect(stored.clips[1].references).toHaveLength(1);
            expect(stored.clips[1].references[0]).toMatchObject({
                kind: "video",
                mediaScale: 0.5,
            });
            expect(
                document.querySelector(
                    ".vst-detail-join-ref-editor .vst-detail-field-label",
                ),
            ).not.toBeNull();
        });

        it("syncs the upscale-method gate live without rebuilding the method select", () => {
            h.setup([{ duration: 4, stages: [{}, { upscale: 2 }] }]);
            wireLiveRenders();
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
            const method =
                fieldByLabel("Upscale Method").querySelector<HTMLSelectElement>(
                    "select",
                );
            const upscale = sliderNumberByLabel("Upscale");
            expect(method?.disabled).toBe(false);
            commitNumber(upscale, "1");
            // Same select node (no rebuild) but now disabled by the live gate.
            expect(fieldByLabel("Upscale Method").querySelector("select")).toBe(
                method,
            );
            expect(method?.disabled).toBe(true);
        });

        it("still REBUILDS on a structure-affecting commit (ref source → Upload)", () => {
            h.setup([
                {
                    duration: 4,
                    stages: [{}],
                    frameRefs: [{ source: "", frame: 1 }],
                },
            ]);
            wireLiveRenders();
            setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
            const before = refRow(0).querySelector<HTMLSelectElement>("select");
            expect(refRow(0).querySelector(".vst-audio-upload")).toBeNull();
            const select = refRow(0).querySelector<HTMLSelectElement>("select");
            if (!select) {
                throw new Error("ref source select missing");
            }
            select.value = "Upload";
            select.dispatchEvent(new Event("change", { bubbles: true }));
            // Structure changed: the upload row appeared and the panel rebuilt
            // (the select is a fresh node).
            expect(refRow(0).querySelector(".vst-audio-upload")).not.toBeNull();
            expect(refRow(0).querySelector("select")).not.toBe(before);
        });

        it("fully rebuilds on an external (non-flush) render — the handshake never leaks", () => {
            h.setup([{ duration: 4, stages: [{}] }]);
            wireLiveRenders();
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const before = fieldByLabel("Duration (s)").querySelector("input");
            // An external carrier change arriving as a plain render (no flush in
            // flight) must rebuild the dock, replacing the node.
            h.strip.render();
            expect(
                fieldByLabel("Duration (s)").querySelector("input"),
            ).not.toBe(before);
        });
    });

    // A value-only commit skips the dock rebuild, so for a field whose commit
    // mutator applies a clamp its static min/max can't express (a relay
    // window's neighbour bound; a segment/retake length capped by its start),
    // buildClampedNumber's readBack corrects the DISPLAYED value in place after
    // the flush — same node, focus intact, no rebuild.
    describe("contextual-clamp write-back", () => {
        it("re-displays a relay Begin clamped by the neighbouring window", () => {
            h.setup([
                {
                    duration: 10,
                    stages: [{}],
                    windows: [
                        { start: 0, duration: 3, prompt: "w0" },
                        { start: 5, duration: 3, prompt: "w1" },
                    ],
                },
            ]);
            wireLiveRenders();
            setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 1 });
            const begin = minorRows()[0].querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="minor-1-begin"]',
            );
            if (!begin) {
                throw new Error("begin input missing");
            }
            commitNumber(begin, "1");
            // Stored begin clamped to the neighbour bound (W0 ends at 3); the
            // input is corrected to the stored value, not the typed 1.
            expect(
                lastSavedClips<Clip[]>(h.saveSpy)[0].promptWindows[1].start,
            ).toBe(3);
            const after = minorRows()[0].querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="minor-1-begin"]',
            );
            expect(after).toBe(begin); // same node — no rebuild
            expect(after?.value).toBe("3"); // shows the clamped stored value
            expect(document.activeElement).toBe(begin); // focus intact
            expect(crumbText()).toBe("Relay 3–8s · Clip 0");
        });

        it("re-displays a relay End clamped to the minimum duration", () => {
            h.setup([
                {
                    duration: 10,
                    stages: [{}],
                    windows: [{ start: 5, duration: 3, prompt: "w0" }],
                },
            ]);
            wireLiveRenders();
            setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
            const end = minorRows()[0].querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="minor-0-end"]',
            );
            if (!end) {
                throw new Error("end input missing");
            }
            commitNumber(end, "0.3");
            // End can't come inside start + min-duration (5 + 0.25 = 5.25); the
            // gesture rounds seconds to 0.1 (roundSeconds: Math.round(2.5)=3), so
            // the stored end is 5.3 — either way, NOT the typed 0.3.
            const w = lastSavedClips<Clip[]>(h.saveSpy)[0].promptWindows[0];
            expect(w.start).toBe(5);
            expect(w.duration).toBe(0.3);
            const after = minorRows()[0].querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="minor-0-end"]',
            );
            expect(after).toBe(end);
            expect(after?.value).toBe("5.3");
            expect(document.activeElement).toBe(end);
        });

        it("re-displays a retake Length capped by its start", () => {
            h.setup([
                {
                    duration: 10,
                    stages: [{}],
                    retake: {
                        startSeconds: 8,
                        lengthSeconds: 1,
                        strength: 1,
                    },
                    initVideo: RETAKE_SOURCE,
                },
            ]);
            wireLiveRenders();
            setSelection({ kind: "retake", clipIdx: 0 });
            const len = retakeFieldByLabel(
                "Length (s)",
            ).querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="retake-length"]',
            );
            if (!len) {
                throw new Error("retake length input missing");
            }
            commitNumber(len, "5");
            expect(
                lastSavedClips<Clip[]>(h.saveSpy)[0].retake?.lengthSeconds,
            ).toBe(2);
            const after = retakeFieldByLabel(
                "Length (s)",
            ).querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="retake-length"]',
            );
            expect(after).toBe(len);
            expect(after?.value).toBe("2");
            expect(document.activeElement).toBe(len);
        });

        it("does NOT write back mid-typing (no flush, no clamp, keeps typed text)", () => {
            h.setup([
                {
                    duration: 10,
                    stages: [{}],
                    retake: {
                        startSeconds: 8,
                        lengthSeconds: 1,
                        strength: 1,
                    },
                    initVideo: RETAKE_SOURCE,
                },
            ]);
            wireLiveRenders();
            setSelection({ kind: "retake", clipIdx: 0 });
            jest.useFakeTimers();
            const len = retakeFieldByLabel(
                "Length (s)",
            ).querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="retake-length"]',
            );
            if (!len) {
                throw new Error("retake length input missing");
            }
            len.focus();
            len.value = "5";
            // Only an `input` event (still typing): the flush is HELD while the
            // number field owns focus, so no save and no write-back fire.
            len.dispatchEvent(new Event("input", { bubbles: true }));
            expect(h.saveSpy).not.toHaveBeenCalled();
            expect(len.value).toBe("5"); // typed text untouched
            jest.advanceTimersByTime(200);
            // The debounce timer was never armed (typing deferral), so still no
            // save until a blur/change flush.
            expect(h.saveSpy).not.toHaveBeenCalled();
            expect(len.value).toBe("5");
        });

        it("does NOT rewrite a non-clamped field (Steps) that was already valid", () => {
            h.setup([{ duration: 4, stages: [{ steps: 8 }] }]);
            wireLiveRenders();
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const steps = sliderNumberByLabel("Steps");
            commitNumber(steps, "14");
            // Steps has no readBack, so nothing forces its display — it keeps the
            // value the user committed (in range), same node, focus intact.
            expect(sliderNumberByLabel("Steps")).toBe(steps);
            expect(steps.value).toBe("14");
            expect(lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].steps).toBe(
                14,
            );
            expect(document.activeElement).toBe(steps);
        });
    });

    it("persists both fields when two debounced sliders change within one window", () => {
        h.setup([{ duration: 4, stages: [{ steps: 8 }] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        jest.useFakeTimers();
        const steps = sliderNumberByLabel("Steps");
        steps.value = "14";
        steps.dispatchEvent(new Event("input", { bubbles: true }));
        const cfg = sliderNumberByLabel("CFG Scale");
        cfg.value = "9";
        cfg.dispatchEvent(new Event("input", { bubbles: true }));
        expect(h.saveSpy).not.toHaveBeenCalled();
        jest.advanceTimersByTime(200);
        // A single coalesced write carries BOTH edits — no silent revert.
        const stage = lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0];
        expect(stage.steps).toBe(14);
        expect(stage.cfgScale).toBe(9);
    });

    it("flushes a pending edit exactly once when the selection switches mid-window", () => {
        h.setup([
            { duration: 4, stages: [{ steps: 8 }] },
            { duration: 4, stages: [{ steps: 8 }] },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        jest.useFakeTimers();
        const steps = sliderNumberByLabel("Steps");
        steps.value = "14";
        steps.dispatchEvent(new Event("input", { bubbles: true }));

        // Switching clips before the debounce elapses must flush the edit.
        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
        expect(h.saveSpy).toHaveBeenCalledTimes(1);
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].steps).toBe(14);

        // The re-render's synthetic slider input must not schedule a spurious
        // write that fires later.
        jest.advanceTimersByTime(200);
        expect(h.saveSpy).toHaveBeenCalledTimes(1);
    });

    it("does not write when a selection change merely re-renders the strip", () => {
        h.setup([{ duration: 4, stages: [{ steps: 8 }] }]);
        jest.useFakeTimers();
        // Selecting a clip builds native sliders; enableSlidersIn fires
        // synthetic input events which must NOT schedule a commit.
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        jest.advanceTimersByTime(200);
        expect(h.saveSpy).not.toHaveBeenCalled();
    });

    describe("slider drag", () => {
        const rangeByLabel = (label: string): HTMLInputElement => {
            const box = Array.from(
                document.querySelectorAll<HTMLElement>(
                    ".vst-detail .vst-stage-slider",
                ),
            ).find(
                (el) =>
                    el.querySelector(".auto-input-name")?.textContent === label,
            );
            const input = box?.querySelector<HTMLInputElement>(
                "input.auto-slider-range",
            );
            if (!input) {
                throw new Error(`range not found: ${label}`);
            }
            return input;
        };

        // jsdom has no PointerEvent constructor; a bubbling generic Event with
        // the pointer type name reaches the document-level capture listeners and
        // carries the range as its target, which is all the latch reads.
        const pointer = (el: Element, type: string): void => {
            el.dispatchEvent(new Event(type, { bubbles: true }));
        };

        const refClip = (): ClipFixture => ({
            duration: 4,
            stages: [{ steps: 8 }],
            frameRefs: [{ source: "Base", frame: 1 }],
        });

        it("holds the debounced edit through a drag (no mid-drag save or rebuild)", () => {
            h.setup([refClip()]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            jest.useFakeTimers();
            const range = rangeByLabel("Keyframe 0");
            // pointerdown latches the drag; streamed inputs sync range → number
            // → our onChange (host enableSliderForBox wiring is live in tests).
            pointer(range, "pointerdown");
            for (const v of ["0.8", "0.6", "0.4"]) {
                range.value = v;
                range.dispatchEvent(new Event("input", { bubbles: true }));
            }
            // Well past the 200ms window: nothing is written and the range node
            // is NOT rebuilt out from under the drag gesture.
            jest.advanceTimersByTime(1000);
            expect(h.saveSpy).not.toHaveBeenCalled();
            expect(rangeByLabel("Keyframe 0")).toBe(range);
            jest.useRealTimers();
        });

        it("commits exactly once on pointer release", () => {
            h.setup([refClip()]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            jest.useFakeTimers();
            const range = rangeByLabel("Keyframe 0");
            pointer(range, "pointerdown");
            range.value = "0.4";
            range.dispatchEvent(new Event("input", { bubbles: true }));
            jest.advanceTimersByTime(1000);
            expect(h.saveSpy).not.toHaveBeenCalled();
            // Release flushes the held edit exactly once (one save, one repaint).
            pointer(range, "pointerup");
            expect(h.saveSpy).toHaveBeenCalledTimes(1);
            expect(
                lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0]
                    .frameRefStrengths[0],
            ).toBe(0.4);
            jest.advanceTimersByTime(1000);
            expect(h.saveSpy).toHaveBeenCalledTimes(1);
            jest.useRealTimers();
        });

        it("clears the latch on pointercancel (no stray hold afterward)", () => {
            h.setup([refClip()]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            jest.useFakeTimers();
            const range = rangeByLabel("Keyframe 0");
            pointer(range, "pointerdown");
            range.value = "0.4";
            range.dispatchEvent(new Event("input", { bubbles: true }));
            // Cancel clears the latch and flushes the queued edit.
            pointer(range, "pointercancel");
            expect(h.saveSpy).toHaveBeenCalledTimes(1);
            // With the latch cleared, a subsequent unfocused (non-gesture) slider
            // edit resumes its normal debounced flush — proving the latch is not
            // stuck set (an input while nothing in the dock has focus arms the
            // timer, which would stay held if sliderDragActive were still true).
            const steps = sliderNumberByLabel("Steps");
            steps.value = "12";
            steps.dispatchEvent(new Event("input", { bubbles: true }));
            jest.advanceTimersByTime(200);
            expect(h.saveSpy).toHaveBeenCalledTimes(2);
            jest.useRealTimers();
        });

        it("removes the document-level pointer listeners on dispose", () => {
            h.setup([refClip()]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const removeSpy = jest.spyOn(document, "removeEventListener");
            h.disposeStrip();
            const removed = removeSpy.mock.calls
                .filter((c) => c[2] === true)
                .map((c) => c[0]);
            expect(removed).toContain("pointerdown");
            expect(removed).toContain("pointerup");
            expect(removed).toContain("pointercancel");
        });
    });

    describe("defer-while-typing", () => {
        const blurOutOfDock = (el: HTMLElement): void => {
            el.dispatchEvent(
                new FocusEvent("focusout", {
                    bubbles: true,
                    relatedTarget: document.body,
                }),
            );
        };

        it("holds a major-prompt edit while focused and flushes on blur out", () => {
            h.setup([{ duration: 5, stages: [{}] }]);
            setSelection({ kind: "prompt-major", clipIdx: 0 });
            jest.useFakeTimers();
            const editor =
                document.querySelector<HTMLTextAreaElement>(
                    ".vst-detail-prompt",
                );
            if (!editor) {
                throw new Error("prompt textarea missing");
            }
            editor.focus();
            editor.value = "typed while focused";
            editor.dispatchEvent(new Event("input", { bubbles: true }));
            jest.advanceTimersByTime(1000);
            expect(h.saveSpy).not.toHaveBeenCalled();
            blurOutOfDock(editor);
            expect(h.saveSpy).toHaveBeenCalledTimes(1);
            expect(lastSavedClips<Clip[]>(h.saveSpy)[0].prompt).toBe(
                "typed while focused",
            );
            jest.useRealTimers();
        });

        it("flushes the held edit on dispose", () => {
            h.setup([{ duration: 5, stages: [{}] }]);
            setSelection({ kind: "prompt-major", clipIdx: 0 });
            jest.useFakeTimers();
            const editor =
                document.querySelector<HTMLTextAreaElement>(
                    ".vst-detail-prompt",
                );
            if (!editor) {
                throw new Error("prompt textarea missing");
            }
            editor.focus();
            editor.value = "typed then torn down";
            editor.dispatchEvent(new Event("input", { bubbles: true }));
            jest.advanceTimersByTime(1000);
            expect(h.saveSpy).not.toHaveBeenCalled();

            h.disposeStrip();
            expect(lastSavedClips<Clip[]>(h.saveSpy)[0].prompt).toBe(
                "typed then torn down",
            );
            jest.useRealTimers();
        });

        it("keeps holding when focus moves to another dock field, not out", () => {
            h.setup([{ duration: 5, stages: [{}] }]);
            setSelection({ kind: "prompt-major", clipIdx: 0 });
            jest.useFakeTimers();
            const editor =
                document.querySelector<HTMLTextAreaElement>(
                    ".vst-detail-prompt",
                );
            const sibling = document.querySelector<HTMLElement>(
                ".vst-detail-settings-button",
            );
            if (!editor || !sibling) {
                throw new Error("dock nodes missing");
            }
            editor.focus();
            editor.value = "held";
            editor.dispatchEvent(new Event("input", { bubbles: true }));
            // Focus moving to another element still INSIDE the dock keeps the
            // edit held (relatedTarget is in the dock).
            editor.dispatchEvent(
                new FocusEvent("focusout", {
                    bubbles: true,
                    relatedTarget: sibling,
                }),
            );
            expect(h.saveSpy).not.toHaveBeenCalled();
            jest.useRealTimers();
        });

        it("commits a number spinner change live even while the field is focused", () => {
            h.setup([{ duration: 5, stages: [{}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            jest.useFakeTimers();
            const dur =
                fieldByLabel("Duration (s)").querySelector<HTMLInputElement>(
                    "input",
                );
            if (!dur) {
                throw new Error("duration input missing");
            }
            dur.focus();
            // Typing is held (no timer) while focused...
            dur.value = "7";
            dur.dispatchEvent(new Event("input", { bubbles: true }));
            jest.advanceTimersByTime(1000);
            expect(h.saveSpy).not.toHaveBeenCalled();
            // ...but a `change` while still focused (spinner/Enter) commits live.
            dur.dispatchEvent(new Event("change", { bubbles: true }));
            expect(h.saveSpy).toHaveBeenCalled();
            expect(lastSavedClips<Clip[]>(h.saveSpy)[0].duration).toBe(7);
            jest.useRealTimers();
        });

        it("does NOT force focus back into a textarea the user tabbed away from", () => {
            h.setup([{ duration: 5, stages: [{}], prompt: "existing" }]);
            setSelection({ kind: "prompt-major", clipIdx: 0 });
            const editor =
                document.querySelector<HTMLTextAreaElement>(
                    ".vst-detail-prompt",
                );
            if (!editor) {
                throw new Error("prompt textarea missing");
            }
            editor.focus();
            editor.value = "edited then left";
            editor.dispatchEvent(new Event("input", { bubbles: true }));
            blurOutOfDock(editor);
            // A later refresh/render must NOT yank focus back into the prompt.
            h.strip.render();
            const after =
                document.querySelector<HTMLTextAreaElement>(
                    ".vst-detail-prompt",
                );
            expect(document.activeElement).not.toBe(editor); // old node gone
            expect(document.activeElement).not.toBe(after); // not re-grabbed
        });

        it("flushes the active relay before switching editors within the dock", () => {
            h.setup([
                {
                    duration: 12,
                    stages: [{}],
                    windows: [
                        { start: 1, duration: 2, prompt: "w0" },
                        { start: 5, duration: 2, prompt: "w1" },
                    ],
                },
            ]);
            setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
            const e0 = document.querySelector<HTMLTextAreaElement>(
                'textarea[data-vst-focus-key="minor-0"]',
            );
            if (!e0) {
                throw new Error("relay editor missing");
            }
            e0.focus();
            e0.value = "typing in zero";
            e0.dispatchEvent(new Event("input", { bubbles: true }));
            document
                .querySelectorAll<HTMLButtonElement>(".vst-relay-tab")[1]
                ?.click();
            expect(
                lastSavedClips<Clip[]>(h.saveSpy)[0].promptWindows[0].prompt,
            ).toBe("typing in zero");
            const e1After = document.querySelector<HTMLTextAreaElement>(
                'textarea[data-vst-focus-key="minor-1"]',
            );
            expect(document.activeElement).toBe(e1After);
        });

        it("flushes the held edit before a subsequent carrier read (Generate ordering)", () => {
            h.setup([{ duration: 5, stages: [{}] }]);
            setSelection({ kind: "prompt-major", clipIdx: 0 });
            const editor =
                document.querySelector<HTMLTextAreaElement>(
                    ".vst-detail-prompt",
                );
            if (!editor) {
                throw new Error("prompt textarea missing");
            }
            editor.focus();
            editor.value = "landscape at dusk";
            editor.dispatchEvent(new Event("input", { bubbles: true }));
            // Simulate the exact ordering a Generate click produces: focus leaves
            // the dock (focusout) BEFORE anything reads the carrier.
            let promptAtReadTime: string | null = null;
            const readCarrier = (): void => {
                promptAtReadTime =
                    document.querySelector<HTMLInputElement>("#input_prompt")
                        ?.value ?? null;
            };
            blurOutOfDock(editor);
            readCarrier();
            expect(h.saveSpy).toHaveBeenCalledTimes(1);
            expect(promptAtReadTime).toContain("landscape at dusk");
        });
    });
});
