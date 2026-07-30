import { afterEach, describe, expect, it, jest } from "@jest/globals";
import { resetArchitectureCatalogForTests } from "../__test_helpers__/architectureCatalog";
import {
    testArchitectureCapabilities,
    testArchitectureCatalog,
    testRootDefaults,
    testSourceOnlyArchitecture,
} from "../__test_helpers__/architectureFixtures";
import {
    hdrIcLoraFixture,
    minimalClip,
    minimalRef,
    minimalStage,
    sourceVideoFixture,
} from "../__test_helpers__/clipFixtures";
import { createCapabilityViewResolver } from "../architectures/policy";
import type { ArchitectureModelCatalog } from "../architectures/types";
import { __resetPersistenceForTests } from "../persistence";
import type { Clip } from "../types";
import { buildAudioBody } from "./audioPanel";
import { buildClipBody } from "./clipPanel";
import type { DetailStripContext } from "./context";
import { buildStageParamsColumn } from "./stagePanel";

/** LTX-2 catalog with frame references and upscaling taken away. */
const restrictedCatalog = (): ArchitectureModelCatalog => {
    const models = testArchitectureCatalog();
    models.architectures[0].capabilities = testArchitectureCapabilities({
        clip: [
            "source-video",
            "prompts",
            "prompt-relay",
            "retake",
            "audio-sources",
            "audio-segments",
        ],
        stage: ["image-input", "video-input", "lora", "ic-lora"],
        upscaleModes: [],
    });
    return models;
};

const context = (
    models: ArchitectureModelCatalog,
    clips: Clip[],
): DetailStripContext =>
    ({
        commit: (mutate: (clips: Clip[]) => void) => {
            mutate(clips);
        },
        commitState: jest.fn(),
        debouncedCommit: jest.fn(),
        debouncedCommitState: jest.fn(),
        buildClampedNumber: () => document.createElement("input"),
        structuralCommit: jest.fn(),
        render: jest.fn(),
        addRefEntry: jest.fn(),
        deleteRefEntry: jest.fn(),
        addPromptWindow: jest.fn(),
        deleteWindowEntry: jest.fn(),
        createRetake: jest.fn(),
        removeRetake: jest.fn(),
        addStage: jest.fn(),
        deleteStage: jest.fn(),
        selectStage: jest.fn(),
        toggleClipSkip: jest.fn(),
        toggleStageSkip: jest.fn(),
        getBoundBody: () => null,
        getDockEl: () => null,
        getSettingsMode: () => null,
        setSettingsMode: jest.fn(),
        capabilities: () => createCapabilityViewResolver(models),
        rootDefaults: () => testRootDefaults(models),
        generatedEntryMode: () => "text-to-video",
    }) as unknown as DetailStripContext;

/** A control a user could actually reach and operate with the keyboard. */
const keyboardOperable = (element: HTMLButtonElement | null): boolean =>
    element !== null && !element.disabled && element.tabIndex >= 0;

afterEach(() => {
    jest.restoreAllMocks();
    resetArchitectureCatalogForTests();
    __resetPersistenceForTests();
    document.body.innerHTML = "";
});

describe("persisted-but-unsupported repair contract", () => {
    it("keeps the delete of an unsupported persisted reference operable", () => {
        const models = restrictedCatalog();
        const clip = minimalClip({ refs: [minimalRef()] });
        const ctx = context(models, [clip]);

        const body = buildClipBody(
            ctx,
            { kind: "clip", clipIdx: 0, stageIdx: 0 },
            [clip],
        );
        const section = body.querySelector<HTMLElement>(
            ".vst-detail-ref-section",
        );
        expect(section).not.toBeNull();
        // Visible and read-only, with the reason.
        expect(
            section?.querySelectorAll("[data-vst-capability-unsupported]"),
        ).toHaveLength(1);
        expect(
            section?.querySelector<HTMLButtonElement>(".vst-detail-add-ref")
                ?.disabled,
        ).toBe(true);

        const remove =
            section?.querySelector<HTMLButtonElement>(".vst-detail-delete");
        expect(keyboardOperable(remove ?? null)).toBe(true);
        remove?.click();
        expect(ctx.deleteRefEntry).toHaveBeenCalledWith(0, 0);
    });

    it("offers a reset for an unsupported persisted upscale", () => {
        const models = restrictedCatalog();
        const stage = minimalStage({ upscale: 2 });
        const clip = minimalClip({ stages: [minimalStage(), stage] });
        const clips = [clip];
        const ctx = context(models, clips);

        const column = buildStageParamsColumn(
            ctx,
            clip,
            0,
            1,
            stage,
            testRootDefaults(models),
        );

        const reset = column.querySelector<HTMLButtonElement>(
            ".vst-reset-unsupported-upscale",
        );
        expect(keyboardOperable(reset)).toBe(true);
        reset?.click();
        expect(clips[0].stages[1].upscale).toBe(1);
    });

    it("keeps unsupported green framing visible with an operable reset", () => {
        const models = restrictedCatalog();
        const clip = minimalClip({ refFraming: "fit-green" });
        const clips = [clip];
        const ctx = context(models, clips);

        const body = buildClipBody(
            ctx,
            { kind: "clip", clipIdx: 0, stageIdx: 0 },
            clips,
        );

        const select = body.querySelector<HTMLSelectElement>(
            "[data-vst-reference-framing]",
        );
        expect(select?.value).toBe("fit-green");
        expect(select?.disabled).toBe(true);
        expect(body.querySelector(".sui-popover")?.textContent).toContain(
            "#66FF00",
        );
        const reset = body.querySelector<HTMLButtonElement>(
            ".vst-reset-unsupported-reference-framing",
        );
        expect(keyboardOperable(reset)).toBe(true);
        reset?.click();
        expect(clips[0].refFraming).toBe("crop");
    });

    it("commits a supported clip-level reference framing selection", () => {
        const models = testArchitectureCatalog();
        const clip = minimalClip();
        const clips = [clip];
        const ctx = context(models, clips);
        const body = buildClipBody(
            ctx,
            { kind: "clip", clipIdx: 0, stageIdx: 0 },
            clips,
        );
        const select = body.querySelector<HTMLSelectElement>(
            "[data-vst-reference-framing]",
        );

        expect(select?.disabled).toBe(false);
        if (!select) throw new Error("reference framing select missing");
        select.value = "fit";
        select.dispatchEvent(new Event("change", { bubbles: true }));

        expect(clips[0].refFraming).toBe("fit");
    });

    it("keeps supported audio reuse operable while repairing unsupported clip audio", () => {
        const models = testArchitectureCatalog();
        models.architectures[0].capabilities = testArchitectureCapabilities({
            clip: ["audio-reuse"],
            audioSourceKinds: ["Disabled"],
        });
        const clip = minimalClip({
            audioSource: "Upload",
            uploadedAudio: {
                data: "data:audio/wav;base64,AA==",
                fileName: "voice.wav",
            },
            reuseAudio: true,
            stages: [minimalStage(), minimalStage(), minimalStage()],
        });
        const clips = [clip];
        const ctx = context(models, clips);
        ctx.structuralCommit = (apply) => {
            apply(clips);
        };

        const body = buildAudioBody(ctx, { kind: "audio", clipIdx: 0 }, clips);
        const source = body.querySelector<HTMLSelectElement>("select");
        const reuse = body.querySelector<HTMLInputElement>(
            ".vst-detail-audio-reuse input",
        );
        const repair = body.querySelector<HTMLButtonElement>(
            ".vst-remove-unsupported-audio",
        );

        expect(source?.disabled).toBe(true);
        expect(reuse?.disabled).toBe(false);
        expect(repair).not.toBeNull();

        repair?.click();

        expect(clip.audioSource).toBe("Native");
        expect(clip.uploadedAudio).toBeNull();
        expect(clip.reuseAudio).toBe(true);
    });

    it("repairs unsupported None duration without deleting its uploaded audio", () => {
        const models = testArchitectureCatalog();
        models.architectures.push(testSourceOnlyArchitecture());
        const clip = minimalClip({
            architectureHint: "none",
            modelProfileId: "none",
            sourceVideo: sourceVideoFixture(),
            stages: [],
            audioSource: "Upload",
            uploadedAudio: {
                data: "data:audio/wav;base64,AA==",
                fileName: "voice.wav",
            },
            clipLengthFromAudio: true,
        });
        const clips = [clip];
        const body = buildAudioBody(
            context(models, clips),
            { kind: "audio", clipIdx: 0 },
            clips,
        );
        const source = body.querySelector<HTMLSelectElement>("select");
        const duration = body.querySelector<HTMLInputElement>(
            ".vst-detail-audio-derived-duration input",
        );
        const repair = body.querySelector<HTMLButtonElement>(
            ".vst-detail-audio-derived-duration .vst-detail-delete",
        );

        expect(source?.disabled).toBe(false);
        expect(duration).toMatchObject({ checked: true, disabled: true });
        expect(keyboardOperable(repair)).toBe(true);

        repair?.click();

        expect(clip.clipLengthFromAudio).toBe(false);
        expect(clip.audioSource).toBe("Upload");
        expect(clip.uploadedAudio?.fileName).toBe("voice.wav");
    });

    it("enables LTX audio-derived duration only for an eligible source", () => {
        const models = testArchitectureCatalog();
        const upload = minimalClip({
            audioSource: "Upload",
            uploadedAudio: {
                data: "data:audio/wav;base64,AA==",
                fileName: "voice.wav",
            },
        });
        const uploadBody = buildAudioBody(
            context(models, [upload]),
            { kind: "audio", clipIdx: 0 },
            [upload],
        );
        expect(
            uploadBody.querySelector<HTMLInputElement>(
                ".vst-detail-audio-derived-duration input",
            )?.disabled,
        ).toBe(false);

        const native = minimalClip({
            clipLengthFromAudio: true,
        });
        const nativeBody = buildAudioBody(
            context(models, [native]),
            { kind: "audio", clipIdx: 0 },
            [native],
        );
        const duration = nativeBody.querySelector<HTMLInputElement>(
            ".vst-detail-audio-derived-duration input",
        );
        const repair = nativeBody.querySelector<HTMLButtonElement>(
            ".vst-detail-audio-derived-duration .vst-detail-delete",
        );

        expect(duration).toMatchObject({ checked: true, disabled: true });
        expect(nativeBody.textContent).toContain(
            "cannot determine video duration",
        );
        expect(keyboardOperable(repair)).toBe(true);
    });

    it("normalizes a disallowed source without deleting supported duration state", () => {
        const models = testArchitectureCatalog();
        models.architectures[0].capabilities = testArchitectureCapabilities({
            clip: ["audio-sources", "audio-derived-duration"],
            audioSourceKinds: ["Upload"],
        });
        const clip = minimalClip({
            audioSource: "ControlNet",
            clipLengthFromAudio: true,
        });
        const clips = [clip];
        const ctx = context(models, clips);
        ctx.structuralCommit = (apply) => {
            apply(clips);
        };
        const body = buildAudioBody(ctx, { kind: "audio", clipIdx: 0 }, clips);
        const duration = body.querySelector<HTMLInputElement>(
            ".vst-detail-audio-derived-duration input",
        );
        const repair = body.querySelector<HTMLButtonElement>(
            ".vst-remove-unsupported-audio",
        );

        expect(duration?.disabled).toBe(false);
        repair?.click();

        expect(clip.audioSource).toBe("Upload");
        expect(clip.uploadedAudio).toBeNull();
        expect(clip.clipLengthFromAudio).toBe(true);
        const repairedBody = buildAudioBody(
            ctx,
            { kind: "audio", clipIdx: 0 },
            clips,
        );
        expect(
            repairedBody.querySelector<HTMLInputElement>(
                ".vst-detail-audio-derived-duration input",
            ),
        ).toMatchObject({ checked: true, disabled: false });
    });

    it("repairs an unknown source before offering duration repair", () => {
        const models = testArchitectureCatalog();
        const clip = minimalClip({
            audioSource: "future-audio-source",
            clipLengthFromAudio: true,
        });
        const clips = [clip];
        const ctx = context(models, clips);
        ctx.structuralCommit = (apply) => {
            apply(clips);
        };
        const body = buildAudioBody(ctx, { kind: "audio", clipIdx: 0 }, clips);
        const duration = body.querySelector<HTMLInputElement>(
            ".vst-detail-audio-derived-duration input",
        );
        const sourceRepair = body.querySelector<HTMLButtonElement>(
            ".vst-remove-unsupported-audio",
        );
        const durationRepair = body.querySelector<HTMLButtonElement>(
            ".vst-detail-audio-derived-duration .vst-detail-delete",
        );

        expect(duration?.disabled).toBe(true);
        expect(sourceRepair).not.toBeNull();
        expect(durationRepair).toBeNull();

        sourceRepair?.click();

        expect(clip.audioSource).toBe("Native");
        expect(clip.clipLengthFromAudio).toBe(true);
        const repairedBody = buildAudioBody(
            ctx,
            { kind: "audio", clipIdx: 0 },
            clips,
        );
        expect(
            repairedBody.querySelector(
                ".vst-detail-audio-derived-duration .vst-detail-delete",
            ),
        ).not.toBeNull();
    });

    it("repairs Wan-like duration while preserving its valid disabled-audio alias", () => {
        const models = testArchitectureCatalog();
        models.architectures[0].capabilities = testArchitectureCapabilities({
            clip: [],
            audioSourceKinds: ["Disabled"],
        });
        const clip = minimalClip({
            audioSource: "Native",
            clipLengthFromAudio: true,
        });
        const clips = [clip];
        const body = buildAudioBody(
            context(models, clips),
            { kind: "audio", clipIdx: 0 },
            clips,
        );
        const durationRepair = body.querySelector<HTMLButtonElement>(
            ".vst-detail-audio-derived-duration .vst-detail-delete",
        );

        expect(durationRepair).not.toBeNull();
        durationRepair?.click();

        expect(clip.clipLengthFromAudio).toBe(false);
        expect(clip.audioSource).toBe("Native");
    });

    it("shows persisted control-signal duration as repair-only state", () => {
        const models = testArchitectureCatalog();
        const clip = minimalClip({
            clipLengthFromControlNet: true,
            icLoras: [
                hdrIcLoraFixture({
                    hdr: false,
                    driveSource: "ControlNet 2",
                }),
            ],
        });
        const clips = [clip];
        const body = buildAudioBody(
            context(models, clips),
            { kind: "audio", clipIdx: 0 },
            clips,
        );
        const status = body.querySelector<HTMLElement>(
            ".vst-detail-control-signal-derived-duration",
        );
        const repair = status?.querySelector<HTMLButtonElement>(
            ".vst-remove-control-signal-derived-duration",
        );

        expect(status?.textContent).toContain(
            "Control-signal-derived clip duration is active",
        );
        expect(status?.querySelector("input")).toBeNull();
        expect(
            status?.querySelector("[data-vst-capability-unsupported]"),
        ).toBeNull();
        expect(keyboardOperable(repair ?? null)).toBe(true);

        repair?.click();

        expect(clip.clipLengthFromControlNet).toBe(false);
    });

    it("explains and repairs a missing LTX control-signal source", () => {
        const models = testArchitectureCatalog();
        const clip = minimalClip({ clipLengthFromControlNet: true });
        const clips = [clip];
        const body = buildAudioBody(
            context(models, clips),
            { kind: "audio", clipIdx: 0 },
            clips,
        );
        const status = body.querySelector<HTMLElement>(
            ".vst-detail-control-signal-derived-duration",
        );
        const repair = status?.querySelector<HTMLButtonElement>(
            ".vst-remove-control-signal-derived-duration",
        );

        expect(status?.textContent).toContain(
            "No IC-LoRA supplies a ControlNet 1-3 drive source",
        );
        expect(keyboardOperable(repair ?? null)).toBe(true);
    });

    it("explains unsupported persisted control-signal duration through the capability", () => {
        const models = testArchitectureCatalog();
        models.architectures.push(testSourceOnlyArchitecture());
        const clip = minimalClip({
            architectureHint: "none",
            modelProfileId: "none",
            sourceVideo: sourceVideoFixture(),
            stages: [],
            clipLengthFromControlNet: true,
        });
        const clips = [clip];
        const body = buildAudioBody(
            context(models, clips),
            { kind: "audio", clipIdx: 0 },
            clips,
        );
        const status = body.querySelector<HTMLElement>(
            ".vst-detail-control-signal-derived-duration",
        );
        const repair = status?.querySelector<HTMLButtonElement>(
            ".vst-remove-control-signal-derived-duration",
        );

        expect(status?.textContent).toContain(
            "Control-signal-derived clip duration is not supported by Decoded source only",
        );
        expect(keyboardOperable(repair ?? null)).toBe(true);
    });

    it("keeps control-signal duration repair operable when clip audio is read-only", () => {
        const models = testArchitectureCatalog();
        models.architectures[0].capabilities = testArchitectureCapabilities({
            clip: ["control-signal-derived-duration"],
            audioSourceKinds: ["Disabled"],
        });
        const clip = minimalClip({
            clipLengthFromControlNet: true,
            icLoras: [
                hdrIcLoraFixture({
                    hdr: false,
                    driveSource: "ControlNet 1",
                }),
            ],
        });
        const clips = [clip];
        const ctx = context(models, clips);
        ctx.structuralCommit = (apply) => {
            apply(clips);
        };
        const body = buildAudioBody(ctx, { kind: "audio", clipIdx: 0 }, clips);
        const repair = body.querySelector<HTMLButtonElement>(
            ".vst-remove-control-signal-derived-duration",
        );

        expect(body.querySelector<HTMLSelectElement>("select")?.disabled).toBe(
            true,
        );
        expect(keyboardOperable(repair)).toBe(true);
    });
});
