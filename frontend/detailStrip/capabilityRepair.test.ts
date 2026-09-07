import { beforeEach, describe, expect, it, jest } from "@jest/globals";
import { resetArchitectureCatalogForTests } from "../__test_helpers__/architectureCatalog";
import {
    testArchitectureCapabilities,
    testArchitectureCatalog,
    testArchitectureCatalogDto,
    testSourceOnlyArchitecture,
} from "../__test_helpers__/architectureFixtures";
import {
    icLoraFixture,
    initVideoFixture,
    minimalClip,
    minimalRef,
    minimalStage,
} from "../__test_helpers__/clipFixtures";
import { editorHarness } from "../__test_helpers__/editor";
import { loadAuthoritativeArchitectureCatalog } from "../architectures/catalog";
import type { ArchitectureModelCatalog } from "../architectures/types";
import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import { getClips, saveClips } from "../persistence/repository";
import type { Clip } from "../types";

/** LTX-2 catalog with keyframes and upscaling taken away. */
const restrictedCatalog = (): ArchitectureModelCatalog => {
    const models = testArchitectureCatalog();
    models.architectures[0].capabilities = testArchitectureCapabilities({
        features: ["promptRelay", "retake", "icLora"],
    });
    return models;
};

const keyboardOperable = (element: HTMLButtonElement | null): boolean =>
    element !== null && !element.disabled && element.tabIndex >= 0;

describe("persisted-but-unsupported repair contract", () => {
    const h = editorHarness();
    beforeEach(() => {
        jest.spyOn(HTMLMediaElement.prototype, "pause").mockImplementation(
            () => {},
        );
        jest.spyOn(HTMLMediaElement.prototype, "load").mockImplementation(
            () => {},
        );
    });
    const mount = async (
        models: ArchitectureModelCatalog,
        clips: Clip[],
        audio = false,
    ): Promise<HTMLElement> => {
        h.disposeEditor();
        resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => testArchitectureCatalogDto(models),
        });
        await loadAuthoritativeArchitectureCatalog();
        h.setup([{ duration: 2, stages: [{}] }]);
        saveClips(clips);
        return audio ? h.section("audio") : h.root;
    };
    it("disables new references when no model publishes an endpoint", async () => {
        const models = testArchitectureCatalog();
        models.entries[0].enhancements = { referencePositions: [] };
        const clip = minimalClip();
        const body = await mount(models, [clip]);

        const add = body.querySelector<HTMLButtonElement>(
            '[data-testid="add-keyframes"]',
        );
        expect(add?.disabled).toBe(true);
        expect(add?.title).toContain(
            "do not publish a supported keyframe endpoint",
        );
    });

    it("keeps the delete of an unsupported persisted reference operable", async () => {
        const models = restrictedCatalog();
        const clip = minimalClip({ frameRefs: [minimalRef()] });

        const body = await mount(models, [clip]);
        const section = body.querySelector<HTMLElement>(
            '[data-section="keyframes"]',
        );
        expect(section).not.toBeNull();
        expect(
            section?.querySelectorAll("[data-vst-capability-unsupported]"),
        ).toHaveLength(1);
        expect(
            section?.querySelector<HTMLButtonElement>(
                '[data-testid="add-keyframes"]',
            )?.disabled,
        ).toBe(true);

        const remove = section?.querySelector<HTMLButtonElement>(
            '[data-action="delete"]',
        );
        expect(keyboardOperable(remove ?? null)).toBe(true);
        remove?.click();
        expect(getClips()[0].frameRefs).toEqual([]);
    });

    it("keeps unsupported green framing visible with an operable reset", async () => {
        const models = restrictedCatalog();
        const clip = minimalClip({ refFraming: "fit-green" });
        const clips = [clip];

        const body = await mount(models, clips);

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
        expect(getClips()[0].refFraming).toBe("crop");
    });

    it("commits a supported clip-level reference framing selection", async () => {
        const models = testArchitectureCatalog();
        const clip = minimalClip();
        const clips = [clip];
        const body = await mount(models, clips);
        const select = body.querySelector<HTMLSelectElement>(
            "[data-vst-reference-framing]",
        );

        expect(select?.disabled).toBe(false);
        if (!select) throw new Error("reference framing select missing");
        select.value = "fit";
        select.dispatchEvent(new Event("change", { bubbles: true }));

        expect(getClips()[0].refFraming).toBe("fit");
    });

    it("keeps supported audio reuse operable while repairing unsupported clip audio", async () => {
        const models = testArchitectureCatalog();
        models.architectures[0].capabilities = testArchitectureCapabilities({
            features: ["audioReuse"],
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

        const body = await mount(models, clips, true);
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

        expect(getClips()[0].audioSource).toBe("Native");
        expect(getClips()[0].uploadedAudio).toBeNull();
        expect(getClips()[0].reuseAudio).toBe(true);
    });

    it("repairs unsupported None duration without deleting its uploaded audio", async () => {
        const models = testArchitectureCatalog();
        models.architectures.push(testSourceOnlyArchitecture());
        const clip = minimalClip({
            architectureHint: "none",
            modelProfileId: "none",
            initVideo: initVideoFixture(),
            stages: [],
            audioSource: "Upload",
            uploadedAudio: {
                data: "data:audio/wav;base64,AA==",
                fileName: "voice.wav",
            },
            clipLengthFromAudio: true,
        });
        const clips = [clip];
        const body = await mount(models, clips, true);
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

        expect(getClips()[0].clipLengthFromAudio).toBe(false);
        expect(getClips()[0].audioSource).toBe("Upload");
        expect(getClips()[0].uploadedAudio?.fileName).toBe("voice.wav");
    });

    it("enables LTX audio-derived duration only for an eligible source", async () => {
        const models = testArchitectureCatalog();
        const upload = minimalClip({
            audioSource: "Upload",
            uploadedAudio: {
                data: "data:audio/wav;base64,AA==",
                fileName: "voice.wav",
            },
        });
        const uploadBody = await mount(models, [upload], true);
        expect(
            uploadBody.querySelector<HTMLInputElement>(
                ".vst-detail-audio-derived-duration input",
            )?.disabled,
        ).toBe(false);

        const native = minimalClip({
            clipLengthFromAudio: true,
        });
        const nativeBody = await mount(models, [native], true);
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

    it("normalizes a disallowed source without deleting supported duration state", async () => {
        const models = testArchitectureCatalog();
        models.architectures[0].capabilities = testArchitectureCapabilities({
            features: ["audioDerivedDuration"],
            audioSourceKinds: ["Upload"],
        });
        const clip = minimalClip({
            audioSource: "ControlNet",
            clipLengthFromAudio: true,
        });
        const clips = [clip];
        const body = await mount(models, clips, true);
        const duration = body.querySelector<HTMLInputElement>(
            ".vst-detail-audio-derived-duration input",
        );
        const repair = body.querySelector<HTMLButtonElement>(
            ".vst-remove-unsupported-audio",
        );

        expect(duration?.disabled).toBe(true);
        repair?.click();

        expect(getClips()[0].audioSource).toBe("Upload");
        expect(getClips()[0].uploadedAudio).toBeNull();
        expect(getClips()[0].clipLengthFromAudio).toBe(true);
        const repairedBody = h.section("audio");
        expect(
            repairedBody.querySelector<HTMLInputElement>(
                ".vst-detail-audio-derived-duration input",
            ),
        ).toMatchObject({ checked: true, disabled: false });
    });

    it("offers ControlNet audio without requiring an IC-LoRA", async () => {
        const models = testArchitectureCatalog();
        models.architectures[0].capabilities = testArchitectureCapabilities({
            features: ["audioDerivedDuration"],
            audioSourceKinds: ["Native", "ControlNet"],
        });
        const clip = minimalClip();

        const body = await mount(models, [clip], true);
        const options = Array.from(
            body.querySelectorAll<HTMLSelectElement>("select")[0]?.options ??
                [],
        ).map((option) => option.value);

        expect(options).toEqual(["Native", "ControlNet"]);
    });

    it("repairs an unknown source before offering duration repair", async () => {
        const models = testArchitectureCatalog();
        const clip = minimalClip({
            audioSource: "future-audio-source",
            clipLengthFromAudio: true,
        });
        const clips = [clip];
        const body = await mount(models, clips, true);
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

        expect(getClips()[0].audioSource).toBe("Native");
        expect(getClips()[0].clipLengthFromAudio).toBe(true);
        const repairedBody = h.section("audio");
        expect(
            repairedBody.querySelector(
                ".vst-detail-audio-derived-duration .vst-detail-delete",
            ),
        ).not.toBeNull();
    });

    it("repairs Wan-like duration while preserving its valid disabled-audio alias", async () => {
        const models = testArchitectureCatalog();
        models.architectures[0].capabilities = testArchitectureCapabilities({
            features: [],
            audioSourceKinds: ["Disabled"],
        });
        const clip = minimalClip({
            audioSource: "Native",
            clipLengthFromAudio: true,
        });
        const clips = [clip];
        const body = await mount(models, clips, true);
        const durationRepair = body.querySelector<HTMLButtonElement>(
            ".vst-detail-audio-derived-duration .vst-detail-delete",
        );

        expect(durationRepair).not.toBeNull();
        durationRepair?.click();

        expect(getClips()[0].clipLengthFromAudio).toBe(false);
        expect(getClips()[0].audioSource).toBe("Native");
    });

    it("shows persisted control-signal duration as repair-only state", async () => {
        const models = testArchitectureCatalog();
        const clip = minimalClip({
            clipLengthFromControlNet: true,
            icLoras: [
                icLoraFixture({
                    driveSource: "ControlNet 2",
                }),
            ],
        });
        const clips = [clip];
        const body = await mount(models, clips, true);
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

        expect(getClips()[0].clipLengthFromControlNet).toBe(false);
    });

    it("explains and repairs a missing LTX control-signal source", async () => {
        const models = testArchitectureCatalog();
        const clip = minimalClip({ clipLengthFromControlNet: true });
        const clips = [clip];
        const body = await mount(models, clips, true);
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

    it("explains unsupported persisted control-signal duration through the capability", async () => {
        const models = testArchitectureCatalog();
        models.architectures.push(testSourceOnlyArchitecture());
        const clip = minimalClip({
            architectureHint: "none",
            modelProfileId: "none",
            initVideo: initVideoFixture(),
            stages: [],
            clipLengthFromControlNet: true,
        });
        const clips = [clip];
        const body = await mount(models, clips, true);
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

    it("keeps control-signal duration repair operable when clip audio is read-only", async () => {
        const models = testArchitectureCatalog();
        models.architectures[0].capabilities = testArchitectureCapabilities({
            features: ["icLora"],
            audioSourceKinds: ["Disabled"],
        });
        const clip = minimalClip({
            clipLengthFromControlNet: true,
            icLoras: [
                icLoraFixture({
                    driveSource: "ControlNet 1",
                }),
            ],
        });
        const clips = [clip];
        const body = await mount(models, clips, true);
        const repair = body.querySelector<HTMLButtonElement>(
            ".vst-remove-control-signal-derived-duration",
        );

        expect(body.querySelector<HTMLSelectElement>("select")?.disabled).toBe(
            true,
        );
        expect(keyboardOperable(repair)).toBe(true);
    });
});
