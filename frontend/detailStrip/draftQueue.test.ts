import { describe, expect, it, jest } from "@jest/globals";
import { resetArchitectureCatalogForTests } from "../__test_helpers__/architectureCatalog";
import {
    testArchitectureCapabilities,
    testArchitectureCatalog,
    testArchitectureCatalogDto,
} from "../__test_helpers__/architectureFixtures";
import { lastSavedClips, mountPromptBox } from "../__test_helpers__/dom";
import {
    type ClipFixture,
    commitNumber,
    editorHarness,
    RETAKE_SOURCE,
} from "../__test_helpers__/editor";
import { loadAuthoritativeArchitectureCatalog } from "../architectures/catalog";
import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import * as persistence from "../persistence/repository";
import { getSelection, setSelection } from "../selection";
import type { Clip } from "../types";

describe("editor draft queue", () => {
    const h = editorHarness();

    it("live-applies a discrete model command through the document store", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const select = h.field<HTMLSelectElement>("stages", "Model", "select");
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
        const steps = h.field("stages", "Steps", "input.auto-slider-number", 0);
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
        const select = h.field<HTMLSelectElement>("stages", "Model", "select");
        if (!select) {
            throw new Error("model select missing");
        }
        mountPromptBox("changed by someone else");
        select.value = "ltx-2.3-alt.safetensors";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        expect(h.saveSpy).not.toHaveBeenCalled();
    });

    describe("value-only commits keep the editor DOM", () => {
        it("keeps the relay input focused while saving its edited end", () => {
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
            setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
            const end = h
                .item("relay-prompts", 0)
                .querySelector<HTMLInputElement>(
                    'input[data-vst-focus-key$="/minor-0-end"]',
                );
            if (!end) {
                throw new Error("end input missing");
            }
            commitNumber(end, "4");
            expect(
                h
                    .item("relay-prompts", 0)
                    .querySelector('input[data-vst-focus-key$="/minor-0-end"]'),
            ).toBe(end);
            expect(document.activeElement).toBe(end);
            expect(
                lastSavedClips<Clip[]>(h.saveSpy)[0].promptWindows[0].duration,
            ).toBe(3);
            expect(h.refreshSpy).toHaveBeenCalled();
            expect(end.value).toBe("4");
        });

        it("keeps focus and the same node on a first Duration change", () => {
            h.setup([{ duration: 4, stages: [{}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const dur = h.field(
                "prompt",
                "Duration (s)",
                'input[type="number"]',
            );
            if (!dur) {
                throw new Error("duration input missing");
            }
            commitNumber(dur, "6");
            expect(
                h.field("prompt", "Duration (s)", 'input[type="number"]'),
            ).toBe(dur);
            expect(document.activeElement).toBe(dur);
            expect(lastSavedClips<Clip[]>(h.saveSpy)[0].duration).toBe(6);
        });

        it("keeps focus and the same node on a first Steps change", () => {
            h.setup([{ duration: 4, stages: [{ steps: 8 }] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const steps = h.field(
                "stages",
                "Steps",
                "input.auto-slider-number",
                0,
            );
            commitNumber(steps, "14");
            expect(
                h.field("stages", "Steps", "input.auto-slider-number", 0),
            ).toBe(steps);
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
            setSelection({ kind: "clip-ref", clipIdx: 1, referenceIdx: 0 });
            expect(
                Array.from(h.section("references").querySelectorAll("h4")).map(
                    (node) => node.textContent,
                ),
            ).toContain("<Video 2>");
            const scale = h.field<HTMLSelectElement>(
                "references",
                "Reference scale",
                "select",
                1,
            );
            if (!scale) {
                throw new Error("reference scale missing");
            }
            scale.value = "0.5";
            scale.dispatchEvent(new Event("change", { bubbles: true }));

            expect(
                Array.from(h.section("references").querySelectorAll("h4")).map(
                    (node) => node.textContent,
                ),
            ).toContain("<Video 2>");
            expect(
                lastSavedClips<Clip[]>(h.saveSpy)[1].references[0].mediaScale,
            ).toBe(0.5);

            setSelection({ kind: "boundary-ref", leftClipIdx: 0 });
            expect(getSelection()).toEqual({
                kind: "boundary-ref",
                leftClipIdx: 0,
            });
            expect(
                Array.from(h.section("references").querySelectorAll("h4")).map(
                    (node) => node.textContent,
                ),
            ).toContain("<Video 1> · From Clip 0");
            const joinScale = h.field<HTMLSelectElement>(
                "references",
                "Reference scale",
                "select",
                0,
            );
            const soundtrack = h.field(
                "references",
                "Include soundtrack",
                'input[type="checkbox"]',
            );
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
            expect(
                Array.from(h.section("references").querySelectorAll("h4")).map(
                    (node) => node.textContent,
                ),
            ).toContain("<Video 1> · From Clip 0");
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
                    '[data-testid="section-references"] .vst-detail-field-label',
                ),
            ).not.toBeNull();
        });

        it("syncs the upscale-method gate live without rebuilding the method select", () => {
            h.setup([{ duration: 4, stages: [{}, { upscale: 2 }] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
            const method = h.field<HTMLSelectElement>(
                "stages",
                "Upscale Method",
                "select",
                1,
            );
            const upscale = h.field(
                "stages",
                "Upscale",
                "input.auto-slider-number",
                1,
            );
            expect(method?.disabled).toBe(false);
            commitNumber(upscale, "1");
            expect(
                h.field<HTMLSelectElement>(
                    "stages",
                    "Upscale Method",
                    "select",
                    1,
                ),
            ).toBe(method);
            expect(method?.disabled).toBe(true);
        });

        it("renders upload controls after changing a keyframe source", () => {
            h.setup([
                {
                    duration: 4,
                    stages: [{}],
                    frameRefs: [{ source: "", frame: 1 }],
                },
            ]);
            setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
            const before = h
                .item("keyframes", 0)
                .querySelector<HTMLSelectElement>("select");
            expect(
                h.item("keyframes", 0).querySelector(".vst-audio-upload"),
            ).toBeNull();
            const select = h
                .item("keyframes", 0)
                .querySelector<HTMLSelectElement>("select");
            if (!select) {
                throw new Error("ref source select missing");
            }
            select.value = "Upload";
            select.dispatchEvent(new Event("change", { bubbles: true }));
            expect(
                h.item("keyframes", 0).querySelector(".vst-audio-upload"),
            ).not.toBeNull();
            expect(h.item("keyframes", 0).querySelector("select")).not.toBe(
                before,
            );
        });

        it("refreshes controls after an external render", () => {
            h.setup([{ duration: 4, stages: [{}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const before = h.field(
                "prompt",
                "Duration (s)",
                'input[type="number"]',
            );
            h.editor.refresh();
            expect(
                h.field("prompt", "Duration (s)", 'input[type="number"]'),
            ).not.toBe(before);
        });
    });

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
            setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 1 });
            const begin = h
                .item("relay-prompts", 1)
                .querySelector<HTMLInputElement>(
                    'input[data-vst-focus-key$="/minor-1-begin"]',
                );
            if (!begin) {
                throw new Error("begin input missing");
            }
            commitNumber(begin, "1");
            expect(
                lastSavedClips<Clip[]>(h.saveSpy)[0].promptWindows[1].start,
            ).toBe(3);
            const after = h
                .item("relay-prompts", 1)
                .querySelector<HTMLInputElement>(
                    'input[data-vst-focus-key$="/minor-1-begin"]',
                );
            expect(after).toBe(begin);
            expect(after?.value).toBe("3");
            expect(document.activeElement).toBe(begin);
            expect(begin.value).toBe("3");
        });

        it("re-displays a relay End clamped to the minimum duration", () => {
            h.setup([
                {
                    duration: 10,
                    stages: [{}],
                    windows: [{ start: 5, duration: 3, prompt: "w0" }],
                },
            ]);
            setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
            const end = h
                .item("relay-prompts", 0)
                .querySelector<HTMLInputElement>(
                    'input[data-vst-focus-key$="/minor-0-end"]',
                );
            if (!end) {
                throw new Error("end input missing");
            }
            commitNumber(end, "0.3");
            const w = lastSavedClips<Clip[]>(h.saveSpy)[0].promptWindows[0];
            expect(w.start).toBe(5);
            expect(w.duration).toBe(0.3);
            const after = h
                .item("relay-prompts", 0)
                .querySelector<HTMLInputElement>(
                    'input[data-vst-focus-key$="/minor-0-end"]',
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
            setSelection({ kind: "retake", clipIdx: 0 });
            const len = h.field(
                "retake",
                "Length (s)",
                'input[data-vst-focus-key$="/retake-length"]',
            );
            if (!len) {
                throw new Error("retake length input missing");
            }
            commitNumber(len, "5");
            expect(
                lastSavedClips<Clip[]>(h.saveSpy)[0].retake?.lengthSeconds,
            ).toBe(2);
            const after = h.field(
                "retake",
                "Length (s)",
                'input[data-vst-focus-key$="/retake-length"]',
            );
            expect(after).toBe(len);
            expect(after?.value).toBe("2");
            expect(document.activeElement).toBe(len);
        });

        it("holds typed text until the retake length is committed", () => {
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
            setSelection({ kind: "retake", clipIdx: 0 });
            jest.useFakeTimers();
            const len = h.field(
                "retake",
                "Length (s)",
                'input[data-vst-focus-key$="/retake-length"]',
            );
            if (!len) {
                throw new Error("retake length input missing");
            }
            len.focus();
            len.value = "5";
            len.dispatchEvent(new Event("input", { bubbles: true }));
            expect(h.saveSpy).not.toHaveBeenCalled();
            expect(len.value).toBe("5");
            jest.advanceTimersByTime(200);
            expect(h.saveSpy).not.toHaveBeenCalled();
            expect(len.value).toBe("5");
        });

        it("preserves valid step text and focus through the commit", () => {
            h.setup([{ duration: 4, stages: [{ steps: 8 }] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const steps = h.field(
                "stages",
                "Steps",
                "input.auto-slider-number",
                0,
            );
            commitNumber(steps, "14");
            expect(
                h.field("stages", "Steps", "input.auto-slider-number", 0),
            ).toBe(steps);
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
        const steps = h.field("stages", "Steps", "input.auto-slider-number", 0);
        steps.value = "14";
        steps.dispatchEvent(new Event("input", { bubbles: true }));
        const cfg = h.field(
            "stages",
            "CFG Scale",
            "input.auto-slider-number",
            0,
        );
        cfg.value = "9";
        cfg.dispatchEvent(new Event("input", { bubbles: true }));
        expect(h.saveSpy).not.toHaveBeenCalled();
        jest.advanceTimersByTime(200);
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
        const steps = h.field("stages", "Steps", "input.auto-slider-number", 0);
        steps.value = "14";
        steps.dispatchEvent(new Event("input", { bubbles: true }));

        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
        expect(h.saveSpy).toHaveBeenCalledTimes(1);
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].steps).toBe(14);

        jest.advanceTimersByTime(200);
        expect(h.saveSpy).toHaveBeenCalledTimes(1);
    });

    it("keeps selection-only rendering free of document writes", () => {
        h.setup([{ duration: 4, stages: [{ steps: 8 }] }]);
        jest.useFakeTimers();
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        jest.advanceTimersByTime(200);
        expect(h.saveSpy).not.toHaveBeenCalled();
    });

    describe("slider drag", () => {
        const rangeByLabel = (label: string): HTMLInputElement =>
            h.field("stages", label, "input.auto-slider-range", 0);

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
            pointer(range, "pointerdown");
            for (const v of ["0.8", "0.6", "0.4"]) {
                range.value = v;
                range.dispatchEvent(new Event("input", { bubbles: true }));
            }
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
            pointer(range, "pointercancel");
            expect(h.saveSpy).toHaveBeenCalledTimes(1);
            const steps = h.field(
                "stages",
                "Steps",
                "input.auto-slider-number",
                0,
            );
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
            h.disposeEditor();
            const removed = removeSpy.mock.calls
                .filter((c) => c[2] === true)
                .map((c) => c[0]);
            expect(removed).toContain("pointerdown");
            expect(removed).toContain("pointerup");
            expect(removed).toContain("pointercancel");
        });
    });

    describe("defer-while-typing", () => {
        const blurOutOfEditor = (el: HTMLElement): void => {
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
            blurOutOfEditor(editor);
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

            h.disposeEditor();
            expect(lastSavedClips<Clip[]>(h.saveSpy)[0].prompt).toBe(
                "typed then torn down",
            );
            jest.useRealTimers();
        });

        it("keeps holding when focus moves to another editor field, not out", () => {
            h.setup([{ duration: 5, stages: [{}] }]);
            setSelection({ kind: "prompt-major", clipIdx: 0 });
            jest.useFakeTimers();
            const editor =
                document.querySelector<HTMLTextAreaElement>(
                    ".vst-detail-prompt",
                );
            const sibling = document.querySelector<HTMLElement>(
                '[data-testid="nav-stages"]',
            );
            if (!editor || !sibling) {
                throw new Error("editor nodes missing");
            }
            editor.focus();
            editor.value = "held";
            editor.dispatchEvent(new Event("input", { bubbles: true }));
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
            const dur = h.field(
                "prompt",
                "Duration (s)",
                'input[type="number"]',
            );
            if (!dur) {
                throw new Error("duration input missing");
            }
            dur.focus();
            dur.value = "7";
            dur.dispatchEvent(new Event("input", { bubbles: true }));
            jest.advanceTimersByTime(1000);
            expect(h.saveSpy).not.toHaveBeenCalled();
            dur.dispatchEvent(new Event("change", { bubbles: true }));
            expect(h.saveSpy).toHaveBeenCalled();
            expect(lastSavedClips<Clip[]>(h.saveSpy)[0].duration).toBe(7);
            jest.useRealTimers();
        });

        it("preserves outside focus after a prompt edit and refresh", () => {
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
            const outside = document.createElement("button");
            document.body.append(outside);
            outside.focus();
            h.editor.refresh();
            expect(document.activeElement).toBe(outside);
            expect(lastSavedClips<Clip[]>(h.saveSpy)[0].prompt).toBe(
                "edited then left",
            );
        });

        it("persists independent drafts when moving between visible relay editors", () => {
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
                'textarea[data-vst-focus-key$="/minor-0"]',
            );
            if (!e0) {
                throw new Error("relay editor missing");
            }
            e0.focus();
            e0.value = "typing in zero";
            e0.dispatchEvent(new Event("input", { bubbles: true }));
            const next = document.querySelector<HTMLTextAreaElement>(
                'textarea[data-vst-focus-key$="/minor-1"]',
            );
            if (!next) throw new Error("second relay editor missing");
            next.focus();
            next.value = "typing in one";
            next.dispatchEvent(new Event("input", { bubbles: true }));
            h.editor.flushPending();
            expect(
                lastSavedClips<Clip[]>(h.saveSpy)[0].promptWindows[0].prompt,
            ).toBe("typing in zero");
            expect(
                lastSavedClips<Clip[]>(h.saveSpy)[0].promptWindows[1].prompt,
            ).toBe("typing in one");
            const e1After = document.querySelector<HTMLTextAreaElement>(
                'textarea[data-vst-focus-key$="/minor-1"]',
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
            let promptAtReadTime: string | null = null;
            const readCarrier = (): void => {
                promptAtReadTime =
                    document.querySelector<HTMLInputElement>("#input_prompt")
                        ?.value ?? null;
            };
            blurOutOfEditor(editor);
            readCarrier();
            expect(h.saveSpy).toHaveBeenCalledTimes(1);
            expect(promptAtReadTime).toContain("landscape at dusk");
        });
    });
});
