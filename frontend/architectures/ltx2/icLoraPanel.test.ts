import { describe, expect, it, jest } from "@jest/globals";
import { initVideoFixture } from "../../__test_helpers__/clipFixtures";
import {
    committedClips,
    detail,
    detailStripHarness,
    fieldByLabel,
    modelGlobals,
    swarmGlobals,
} from "../../__test_helpers__/detailStrip";
import { lastSavedClips } from "../../__test_helpers__/dom";
import { setSelection } from "../../selection";
import type { Clip } from "../../types";
import { IC_LORA_AUTO } from "./icLoraPresets";

describe("detail strip IC-LoRA panel", () => {
    const h = detailStripHarness();

    const icLoraSelect = (label: string): HTMLSelectElement => {
        const select =
            fieldByLabel(label).querySelector<HTMLSelectElement>("select");
        if (!select) {
            throw new Error(`IC-LoRA ${label} select missing`);
        }
        return select;
    };

    const changeIcLoraSelect = (label: string, value: string): void => {
        const select = icLoraSelect(label);
        select.value = value;
        select.dispatchEvent(new Event("change", { bubbles: true }));
    };

    const controlNetLabels = (): (string | null)[] =>
        Array.from(
            document.querySelectorAll<HTMLElement>(
                ".vst-detail .auto-input-name",
            ),
        ).map((el) => el.textContent);

    it("hides IC-LoRA strengths when the clip has no IC-LoRAs", () => {
        h.setup([{ duration: 4, stages: [{}], controlNetLora: "" }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(document.querySelector(".vst-stage-iclora-strength")).toBeNull();
    });

    it("shows a zero-based strength for each IC-LoRA in the stage", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [
                    {
                        lora: "some-cnet-lora",
                        driveSource: "ControlNet 1",
                        driveData: "visual",
                    },
                    {
                        lora: "some-other-cnet-lora",
                        driveSource: "ControlNet 2",
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(controlNetLabels()).toEqual(
            expect.arrayContaining(["some-cnet-lora", "some-other-cnet-lora"]),
        );
        const strengthInputs = Array.from(
            document.querySelectorAll<HTMLInputElement>(
                ".vst-stage-iclora-strength-row input.lora-weight-input",
            ),
        );
        expect(strengthInputs).toHaveLength(2);
        for (const input of strengthInputs) {
            const label = input
                .closest(".vst-stage-iclora-strength-row")
                ?.querySelector<HTMLLabelElement>("label");
            expect(input.id).not.toBe("");
            expect(label?.htmlFor).toBe(input.id);
        }
        expect(
            document.querySelector(
                ".vst-stage-iclora-strength-row input.auto-slider-range",
            ),
        ).toBeNull();
        const subsectionHeaders = Array.from(
            document.querySelectorAll<HTMLElement>(
                ".vst-detail-subsection-crumb",
            ),
        );
        expect(subsectionHeaders.map((header) => header.textContent)).toEqual(
            expect.arrayContaining(["IC-LoRA Guide Strengths"]),
        );
        for (const header of subsectionHeaders) {
            expect(header.classList.contains("vst-detail-crumb")).toBe(true);
            expect(header.getAttribute("role")).toBe("heading");
            expect(header.getAttribute("aria-level")).toBe("4");
        }
    });

    it("labels IC-LoRA guide strengths by preset, or by model for Custom", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [
                    {
                        lora: IC_LORA_AUTO,
                        preset: "deblur",
                        driveData: "visual",
                    },
                    {
                        lora: "LTX-2/custom-guide.safetensors",
                        preset: "custom",
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });

        expect(controlNetLabels()).toEqual(
            expect.arrayContaining(["Deblur", "custom-guide.safetensors"]),
        );
        expect(controlNetLabels()).not.toContain(IC_LORA_AUTO);
    });

    it("persists IC-LoRA strengths independently by zero-based entry index", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [
                    { lora: "first.safetensors", driveData: "visual" },
                    { lora: "second.safetensors", driveData: "visual" },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        jest.useFakeTimers();
        const second =
            fieldByLabel("second.safetensors").querySelector<HTMLInputElement>(
                "input",
            );
        if (!second) throw new Error("IC-LoRA strength input missing");
        second.value = "0.3";
        second.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(
            lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].icLoraStrengths,
        ).toEqual([1, 0.3]);
    });

    it("adds an IC-LoRA entry with defaults via the add button", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const addBtn = document.querySelector<HTMLButtonElement>(
            ".vst-detail-add-iclora",
        );
        if (!addBtn) {
            throw new Error("add IC-LoRA button missing");
        }
        addBtn.click();
        const clips = lastSavedClips<Clip[]>(h.saveSpy);
        expect(clips[0].icLoras).toHaveLength(1);
        expect(clips[0].icLoras[0]).toEqual({
            lora: IC_LORA_AUTO,
            preset: "union-control",
            driveSource: "Upload",
            driveData: "visual",
            driveMediaKinds: ["image", "video"],
            stage: -1,
            strength: 1,
            attentionStrength: 1,
            controlType: "depth",
            driveMedia: null,
        });
        expect(clips[0].stages[0].icLoraStrengths).toEqual([1]);
        expect(document.querySelector(".vst-detail-iclora")).not.toBeNull();
        expect(controlNetLabels()).not.toContain("LoRA");
        expect(controlNetLabels()).toContain("Drive Media");
    });

    it("add IC-LoRA starts on a curated preset and hides its internal model", () => {
        h.setup(
            [{ duration: 4, stages: [{}] }],
            ["(None)", "lora-x.safetensors"],
        );
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const addBtn = document.querySelector<HTMLButtonElement>(
            ".vst-detail-add-iclora",
        );
        if (!addBtn) {
            throw new Error("add IC-LoRA button missing");
        }
        addBtn.click();
        const clips = lastSavedClips<Clip[]>(h.saveSpy);
        expect(clips[0].icLoras).toHaveLength(1);
        expect(clips[0].icLoras[0].lora).toBe(IC_LORA_AUTO);
        expect(clips[0].icLoras[0].preset).toBe("union-control");
        expect(controlNetLabels()).not.toContain("LoRA");
        // The row survives the rebuild (the original bug: it vanished).
        expect(document.querySelectorAll(".vst-detail-iclora")).toHaveLength(1);
    });

    it("applying a preset selects its [AUTO] weights and seeds its settings", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const presetSelect =
            fieldByLabel("Preset").querySelector<HTMLSelectElement>("select");
        if (!presetSelect) {
            throw new Error("preset select missing");
        }
        presetSelect.value = "union-control";
        presetSelect.dispatchEvent(new Event("change", { bubbles: true }));
        const clips = lastSavedClips<Clip[]>(h.saveSpy);
        expect(clips[0].icLoras[0].preset).toBe("union-control");
        expect(clips[0].icLoras[0].lora).toBe(IC_LORA_AUTO);
        expect(clips[0].icLoras[0].controlType).toBe("depth");
        expect(clips[0].icLoras[0].strength).toBe(1);
        expect(clips[0].icLoras[0].driveMediaKinds).toEqual(["image", "video"]);
        expect(controlNetLabels()).not.toContain("LoRA");
    });

    it("shows the Control select only for Custom and Union Control presets", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const labels = (): (string | null)[] =>
            Array.from(
                document.querySelectorAll(
                    ".vst-detail-iclora .vst-detail-field-label",
                ),
            ).map((el) => el.textContent);
        // Custom (no preset) could be a third-party control LoRA, so Control shows.
        expect(labels()).toContain("Control");
        changeIcLoraSelect("Preset", "deblur");
        expect(labels()).not.toContain("Control");
        changeIcLoraSelect("Preset", "union-control");
        expect(labels()).toContain("Control");
    });

    it("gives LipDub Upload or Incoming audio without visual-guide controls", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        preset: "lipdub",
                        driveData: "audio",
                        stage: 1,
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });

        const row = document.querySelector<HTMLElement>(".vst-detail-iclora");
        const driveMedia = Array.from(
            row?.querySelectorAll<HTMLElement>(".vst-detail-field") ?? [],
        ).find(
            (field) =>
                field.querySelector(".vst-detail-field-label")?.textContent ===
                "Drive Media",
        );
        const input =
            driveMedia?.querySelector<HTMLInputElement>('input[type="file"]');

        expect(input?.accept).toBe("audio/*,video/*");
        const labels = Array.from(
            row?.querySelectorAll(".vst-detail-field-label") ?? [],
        ).map((label) => label.textContent);
        expect(labels).not.toEqual(
            expect.arrayContaining(["Attention", "Control", "Drive data"]),
        );
        expect(labels).toContain("Source");
        const source =
            fieldByLabel("Source").querySelector<HTMLSelectElement>("select");
        expect(
            Array.from(source?.options ?? []).map((option) => option.value),
        ).toEqual(["Upload", "Incoming"]);
        expect(source?.options[1].disabled).toBe(false);
        expect(row?.textContent).toContain("Only this media's audio");
        expect(row?.textContent).toContain("frames are ignored");
    });

    it("lets Custom choose Audio and uses the same generic audio contract", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        stage: 1,
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const select =
            fieldByLabel("Drive data").querySelector<HTMLSelectElement>(
                "select",
            );
        if (!select) {
            throw new Error("Drive data select missing");
        }
        select.value = "audio";
        select.dispatchEvent(new Event("change", { bubbles: true }));

        const entry = lastSavedClips<Clip[]>(h.saveSpy)[0].icLoras[0];
        expect(entry.driveData).toBe("audio");
        expect(entry.driveMediaKinds).toEqual(["audio", "video"]);
        expect(entry.controlType).toBe("none");
        expect(controlNetLabels()).not.toEqual(
            expect.arrayContaining(["Attention", "Control"]),
        );
        expect(
            fieldByLabel("Drive Media").querySelector<HTMLInputElement>(
                'input[type="file"]',
            )?.accept,
        ).toBe("audio/*,video/*");
    });

    it("lets Custom choose a model-only patch and clears hidden drive media", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        driveData: "visual",
                        driveMedia: {
                            data: "data:image/png;base64,AA==",
                            fileName: "guide.png",
                        },
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const select =
            fieldByLabel("Drive data").querySelector<HTMLSelectElement>(
                "select",
            );
        if (!select) {
            throw new Error("Drive data select missing");
        }
        select.value = "none";
        select.dispatchEvent(new Event("change", { bubbles: true }));

        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].icLoras[0]).toMatchObject({
            driveData: "none",
            driveSource: "Upload",
            driveMedia: null,
        });
        expect(controlNetLabels()).not.toEqual(
            expect.arrayContaining(["Source", "Drive Media"]),
        );
    });

    it("uses persisted image-only Drive Media kinds for Upload and Incoming gating", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        driveData: "visual",
                        driveMediaKinds: ["image"],
                        stage: 1,
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });

        expect(
            fieldByLabel("Drive Media").querySelector<HTMLInputElement>(
                'input[type="file"]',
            )?.accept,
        ).toBe("image/*");
        expect(
            fieldByLabel("Source").querySelector<HTMLSelectElement>("select")
                ?.options[1].disabled,
        ).toBe(true);
    });

    it("resets per-stage IC-LoRA strengths from the selected model default", () => {
        modelGlobals.sdLoraBrowser = {
            models: {
                "weighted-ic.safetensors": {
                    data: { lora_default_weight: "0.4" },
                },
            },
        };
        h.setup(
            [
                {
                    duration: 4,
                    stages: [{}],
                    icLoras: [
                        {
                            lora: "lora-x.safetensors",
                            driveData: "visual",
                        },
                    ],
                },
            ],
            ["lora-x.safetensors", "weighted-ic.safetensors"],
        );
        setSelection({ kind: "ic-lora", clipIdx: 0, entryIdx: 0 });
        const select =
            fieldByLabel("LoRA").querySelector<HTMLSelectElement>("select");
        if (!select) throw new Error("IC-LoRA model select missing");
        select.value = "weighted-ic.safetensors";
        select.dispatchEvent(new Event("change", { bubbles: true }));

        expect(
            lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].icLoraStrengths,
        ).toEqual([0.4]);
    });

    it("Apply on lists every stage plus All stages", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const options = Array.from(icLoraSelect("Apply on").options).map(
            (o) => [o.value, o.textContent],
        );
        expect(options).toEqual([
            ["-1", "All stages"],
            ["0", "Stage 0"],
            ["1", "Stage 1"],
        ]);
        // The whole field set a Custom entry renders, in order.
        expect(
            Array.from(
                document.querySelectorAll(
                    ".vst-detail-iclora .vst-detail-field-label",
                ),
            ).map((el) => el.textContent),
        ).toEqual([
            "Preset",
            "LoRA",
            "Strength",
            "Attention",
            "Control",
            "Apply on",
            "Drive data",
            "Source",
            "Drive Media",
        ]);
        // Incoming is disabled because the all-stages target includes a
        // generated stage 0.
        expect(icLoraSelect("Source").options[1].disabled).toBe(true);
    });

    it("refine-stage placement offers Incoming and swaps the upload row for a hint", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        changeIcLoraSelect("Apply on", "1");
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].icLoras[0].stage).toBe(1);

        changeIcLoraSelect("Source", "Incoming");
        const entry = lastSavedClips<Clip[]>(h.saveSpy)[0].icLoras[0];
        expect(entry.driveSource).toBe("Incoming");
        expect(controlNetLabels()).not.toContain("Drive Media");
        expect(detail()?.textContent).toContain(
            "Uses visual from stage 1's incoming media.",
        );
    });

    it("moving an Incoming entry to an unavailable scope resets it to Upload", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        stage: 1,
                        driveSource: "Incoming",
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        changeIcLoraSelect("Apply on", "-1");
        const entry = lastSavedClips<Clip[]>(h.saveSpy)[0].icLoras[0];
        expect(entry.stage).toBe(-1);
        expect(entry.driveSource).toBe("Upload");
        expect(controlNetLabels()).toContain("Drive Media");
    });

    it("init-video clip renders the IC-LoRA Source select and footage-drive hint on an all-stages entry", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}, {}],
                initVideo: initVideoFixture({
                    fileName: "clip.mp4",
                    durationSeconds: 4,
                    lengthSeconds: 4,
                }),
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        // Incoming is available because source footage enters stage 0.
        expect(icLoraSelect("Source").value).toBe("Upload");
        expect(controlNetLabels()).toContain("Drive Media");
        expect(icLoraSelect("Source").options[1].disabled).toBe(false);
    });

    it("init-video clip Incoming entry shows its data source at stage 0", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}, {}],
                initVideo: initVideoFixture({
                    fileName: "clip.mp4",
                    durationSeconds: 4,
                    lengthSeconds: 4,
                }),
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        stage: 0,
                        driveSource: "Incoming",
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(icLoraSelect("Source").value).toBe("Incoming");
        expect(controlNetLabels()).not.toContain("Drive Media");
        expect(detail()?.textContent).toContain(
            "Uses visual from stage 0's incoming media.",
        );
    });

    it("non-init-video clip disables Incoming on a stage-0/all entry", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(icLoraSelect("Source").options[1].disabled).toBe(true);
        expect(controlNetLabels()).toContain("Drive Media");
    });

    it("does not mistake a skipped authored stage for prior-stage Incoming media", () => {
        h.setup([
            {
                duration: 4,
                stages: [{ skipped: true }, {}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        stage: 1,
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
        expect(icLoraSelect("Source").options[1].disabled).toBe(true);
    });

    it("repairs Incoming to Upload when skipping its targeted later stage", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        stage: 1,
                        driveSource: "Incoming",
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
        document
            .querySelector<HTMLButtonElement>(
                '.vst-stage-tab[aria-pressed="true"] .vst-detail-skip-stage',
            )
            ?.click();

        expect(committedClips()[0].icLoras[0].driveSource).toBe("Upload");
        expect(
            fieldByLabel("Drive Media").querySelector<HTMLInputElement>(
                'input[type="file"]',
            )?.accept,
        ).toBe("image/*,video/*");
    });

    it("repairs a later clip's Incoming source when its prior clip is skipped", () => {
        h.setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
            {
                duration: 4,
                stages: [{}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        stage: 0,
                        driveSource: "Incoming",
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
        document
            .querySelector<HTMLButtonElement>(".vst-detail-skip-clip")
            ?.click();

        expect(committedClips()[2].icLoras[0].driveSource).toBe("Upload");
    });

    it("disables Incoming after the first skipped clip truncates the sequence", () => {
        h.setup([
            { duration: 4, stages: [{}] },
            { duration: 4, skipped: true, stages: [{}] },
            {
                duration: 4,
                stages: [{}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        stage: 0,
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 2, stageIdx: 0 });
        expect(icLoraSelect("Source").options[1].disabled).toBe(true);
    });

    it("does not treat a skipped earlier clip as Incoming output", () => {
        h.setup([
            { duration: 4, skipped: true, stages: [{}] },
            {
                duration: 4,
                stages: [{}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        stage: 0,
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
        expect(icLoraSelect("Source").options[1].disabled).toBe(true);
    });

    it("shows only actual models in the Custom IC-LoRA dropdown", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const options = Array.from(icLoraSelect("LoRA").options).map(
            (o) => o.value,
        );
        expect(options).toEqual(["lora-x.safetensors"]);
        expect(options).not.toContain(IC_LORA_AUTO);
    });

    it("shows the model dropdown only after choosing Custom", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [{ lora: IC_LORA_AUTO, preset: "deblur" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(controlNetLabels()).not.toContain("LoRA");

        const presetSelect = icLoraSelect("Preset");
        presetSelect.value = "custom";
        presetSelect.dispatchEvent(new Event("change", { bubbles: true }));

        expect(controlNetLabels()).toContain("LoRA");
        expect(icLoraSelect("LoRA").value).toBe("lora-x.safetensors");
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].icLoras[0]).toMatchObject({
            preset: "custom",
            lora: "lora-x.safetensors",
        });
    });

    it("selecting a preset hides the model dropdown and starts its download", () => {
        swarmGlobals.makeWSRequest = jest.fn();
        h.setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const presetSelect = icLoraSelect("Preset");
        presetSelect.value = "deblur";
        presetSelect.dispatchEvent(new Event("change", { bubbles: true }));

        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].icLoras[0].lora).toBe(
            IC_LORA_AUTO,
        );
        expect(controlNetLabels()).not.toContain("LoRA");
        expect(swarmGlobals.makeWSRequest).toHaveBeenCalledTimes(1);
        expect(swarmGlobals.makeWSRequest).toHaveBeenCalledWith(
            "VideoStagesDownloadIcLoraWS",
            { presetId: "deblur" },
            expect.any(Function),
            0,
            expect.any(Function),
        );
        expect(
            document.querySelector('[data-vst-iclora-auto="deblur"]')
                ?.textContent,
        ).toContain("Downloading Deblur weights");
    });

    it("repairs legacy Custom + [AUTO] and downloads the default preset", () => {
        swarmGlobals.makeWSRequest = jest.fn();
        swarmGlobals.refreshParameterValues = jest.fn();
        h.setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [{ lora: IC_LORA_AUTO }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(icLoraSelect("Preset").value).toBe("union-control");
        expect(controlNetLabels()).not.toContain("LoRA");
        expect(swarmGlobals.makeWSRequest).toHaveBeenCalledTimes(1);
        expect(swarmGlobals.makeWSRequest).toHaveBeenCalledWith(
            "VideoStagesDownloadIcLoraWS",
            { presetId: "union-control" },
            expect.any(Function),
            0,
            expect.any(Function),
        );

        // Completion refreshes the host's model lists and settles the hint.
        const onData = swarmGlobals.makeWSRequest.mock.calls[0][2] as (
            data: Record<string, unknown>,
        ) => void;
        onData({ success: true });
        expect(swarmGlobals.refreshParameterValues).toHaveBeenCalledWith(true);
        expect(detail()?.textContent).toContain(
            "Downloaded to LTX-2/IC-LoRA/ltx-2_3-22b-ic-lora-union-control-ref0_5",
        );
    });

    it("shows the transfer progress from current_percent, not the 0.2 step marker", () => {
        swarmGlobals.makeWSRequest = jest.fn();
        h.setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [{ lora: IC_LORA_AUTO, preset: "deblur" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const onData = swarmGlobals.makeWSRequest.mock.calls[0][2] as (
            data: Record<string, unknown>,
        ) => void;
        // The core downloader pins overall_percent to 0.2 for the whole
        // transfer; the live percentage is current_percent.
        onData({ current_percent: 0.57, overall_percent: 0.2, per_second: 1 });
        expect(
            document.querySelector('[data-vst-iclora-auto="deblur"]')
                ?.textContent,
        ).toContain("Downloading Deblur weights… 57%");
    });

    it.each([
        "Model not found.",
        "Download was cancelled.",
    ])("shows terminal downloader failure %s and retries only after preset reselection", (message) => {
        swarmGlobals.makeWSRequest = jest.fn();
        h.setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        changeIcLoraSelect("Preset", "deblur");

        const onError = swarmGlobals.makeWSRequest.mock.calls[0][4] as (
            error: unknown,
        ) => void;
        onError(message);

        expect(
            document.querySelector('[data-vst-iclora-auto="deblur"]')
                ?.textContent,
        ).toContain(`Download failed: ${message}`);
        expect(swarmGlobals.makeWSRequest).toHaveBeenCalledTimes(1);

        h.strip.render();
        expect(swarmGlobals.makeWSRequest).toHaveBeenCalledTimes(1);

        changeIcLoraSelect("Preset", "custom");
        expect(swarmGlobals.makeWSRequest).toHaveBeenCalledTimes(1);

        changeIcLoraSelect("Preset", "deblur");
        expect(swarmGlobals.makeWSRequest).toHaveBeenCalledTimes(2);
    });

    it("skips the [AUTO] download when the preset weights are already installed", () => {
        swarmGlobals.makeWSRequest = jest.fn();
        h.setup(
            [
                {
                    duration: 4,
                    stages: [{}],
                    icLoras: [{ lora: IC_LORA_AUTO, preset: "deblur" }],
                },
            ],
            [
                "lora-x.safetensors",
                "LTX-2/IC-LoRA/ltx-2_3-22b-ic-lora-deblur-0_9",
            ],
        );
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(swarmGlobals.makeWSRequest).not.toHaveBeenCalled();
        expect(detail()?.textContent).toContain(
            "Using LTX-2/IC-LoRA/ltx-2_3-22b-ic-lora-deblur-0_9",
        );
    });

    it("accepts weights installed under the legacy dotted download name", () => {
        swarmGlobals.makeWSRequest = jest.fn();
        h.setup(
            [
                {
                    duration: 4,
                    stages: [{}],
                    icLoras: [{ lora: IC_LORA_AUTO, preset: "deblur" }],
                },
            ],
            ["LTX-2/IC-LoRA/ltx-2.3-22b-ic-lora-deblur-0.9"],
        );
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(swarmGlobals.makeWSRequest).not.toHaveBeenCalled();
        expect(detail()?.textContent).toContain(
            "Using LTX-2/IC-LoRA/ltx-2.3-22b-ic-lora-deblur-0.9",
        );
    });

    it("offers IC-LoRAs with [AUTO] even when no LoRAs are installed", () => {
        h.setup([{ duration: 4, stages: [{}] }], []);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const addBtn = document.querySelector<HTMLButtonElement>(
            ".vst-detail-add-iclora",
        );
        if (!addBtn) {
            throw new Error("add IC-LoRA button missing");
        }
        addBtn.click();
        const clips = lastSavedClips<Clip[]>(h.saveSpy);
        expect(clips[0].icLoras).toHaveLength(1);
        expect(clips[0].icLoras[0].lora).toBe(IC_LORA_AUTO);
    });

    it("removes an IC-LoRA entry via the rail Delete button", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const removeBtn = document.querySelector<HTMLButtonElement>(
            ".vst-detail-delete-iclora",
        );
        if (!removeBtn) {
            throw new Error("remove IC-LoRA button missing");
        }
        removeBtn.click();
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].icLoras).toHaveLength(0);
    });
});
