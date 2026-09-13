import { afterEach, describe, expect, it, jest } from "@jest/globals";
import {
    testArchitectureCapabilities,
    testArchitectureCatalog,
    testAuthoringTransactionSnapshot,
} from "../__test_helpers__/architectureFixtures";
import {
    icLoraFixture,
    minimalClip,
    minimalClipReference,
    minimalRef,
} from "../__test_helpers__/clipFixtures";
import { mountVideoStagesData } from "../__test_helpers__/dom";
import type { ArchitectureModelCatalog } from "../architectures/types";
import type { DetailStripContext } from "../detailStrip/context";
import { resetRememberedAccordionSections } from "../detailWidgets";
import { setVideoStagesHostBridgeForTests } from "../host";
import { __resetPersistenceForTests } from "../persistence/repository";
import type { AuthoringDocument, Clip } from "../types";
import { sectionPanel } from "./components";
import { clipSections } from "./sections";

const setup = (clips: Clip[], catalog = testArchitectureCatalog()) => {
    mountVideoStagesData({ clips: JSON.parse(JSON.stringify(clips)) });
    const snapshot = testAuthoringTransactionSnapshot(catalog);
    const state: AuthoringDocument = {
        width: 1024,
        height: 576,
        fps: 24,
        dimsExplicit: false,
        clips,
        audioTracks: [],
    };
    const context: DetailStripContext = {
        authoring: () => snapshot,
        commit: (edit) => edit(clips),
        commitState: (edit) => edit(state),
        debouncedCommit: (_key, edit) => edit(clips),
        debouncedCommitState: (_key, edit) => edit(state),
        structuralCommit: (edit) => {
            edit(clips);
        },
        buildClampedNumber: (options) => {
            const input = document.createElement("input");
            input.type = "number";
            input.value = String(options.value);
            return input;
        },
        render: jest.fn(),
        addRefEntry: jest.fn(),
        deleteRefEntry: jest.fn(),
        addClipReference: jest.fn(),
        deleteClipReference: jest.fn(),
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
        getSettingsMode: () => null,
        setSettingsMode: jest.fn(),
    };
    const mount = (key: string, clipIdx = 0) => {
        const section = clipSections(state, clipIdx, context).find(
            (entry) => entry.key === key,
        );
        if (!section) throw new Error(`Missing ${key}`);
        const panel = sectionPanel(section, clips[clipIdx].id ?? "clip");
        document.querySelector("section[data-section]")?.remove();
        document.body.append(panel);
        return panel;
    };
    return { state, snapshot, context, mount };
};
const required = <T extends HTMLElement>(
    root: HTMLElement,
    selector: string,
): T => {
    const node = root.querySelector<T>(selector);
    if (!node) throw new Error(`Missing ${selector}`);
    return node;
};
const restrict = (): ArchitectureModelCatalog => {
    const catalog = testArchitectureCatalog();
    catalog.architectures[0].capabilities = testArchitectureCapabilities({
        features: [],
        audioSourceKinds: [],
    });
    return catalog;
};
afterEach(() => {
    __resetPersistenceForTests();
    document.body.replaceChildren();
    resetRememberedAccordionSections();
    setVideoStagesHostBridgeForTests(null);
});

describe("production editor feature parity", () => {
    it("edits a Continue reference and shows the prompt tags for subsequent references", () => {
        const catalog = testArchitectureCatalog();
        catalog.architectures[0].capabilities.features.push("clipReferences");
        const constraints =
            catalog.architectures[0].boundaryRules.continue.constraints;
        if (!constraints) throw new Error("Missing Continue constraints");
        constraints.continueMode = "reference";
        constraints.continuityExtraFrames = 0;
        const clips = [
            minimalClip({ id: "left", boundaryOut: "continue", duration: 5 }),
            minimalClip({
                id: "right",
                duration: 5,
                references: [
                    minimalClipReference({
                        kind: "video",
                        uploadedMedia: null,
                    }),
                    minimalClipReference({
                        kind: "audio",
                        uploadedMedia: null,
                    }),
                ],
            }),
        ];
        const { mount } = setup(clips, catalog);
        const panel = mount("references", 1);
        expect(
            Array.from(
                panel.querySelectorAll("h4"),
                (node) => node.textContent,
            ),
        ).toEqual(["<Video 1> · From Clip 0", "<Video 2>", "<Audio 2>"]);
        const incoming = required<HTMLElement>(
            panel,
            '[data-selection*="boundary-ref"]',
        );
        const scale = required<HTMLSelectElement>(incoming, "select");
        scale.value = "0.5";
        scale.dispatchEvent(new Event("change", { bubbles: true }));
        const soundtrack = required<HTMLInputElement>(
            incoming,
            'input[type="checkbox"]',
        );
        soundtrack.checked = false;
        soundtrack.dispatchEvent(new Event("change", { bubbles: true }));
        expect(clips[0].boundaryOutReferenceScale).toBe(0.5);
        expect(clips[0].boundaryOutReferenceIncludeSoundtrack).toBe(false);
        expect(
            Array.from(
                mount("references", 1).querySelectorAll("h4"),
                (node) => node.textContent,
            ),
        ).toEqual(["<Video 1> · From Clip 0", "<Video 2>", "<Audio 1>"]);
    });

    it("keeps persisted references repairable when Continue exists without reference authoring support", () => {
        const catalog = restrict();
        const constraints =
            catalog.architectures[0].boundaryRules.continue.constraints;
        if (!constraints) throw new Error("Missing Continue constraints");
        constraints.continueMode = "reference";
        constraints.continuityExtraFrames = 0;
        const { mount } = setup(
            [
                minimalClip({
                    id: "left",
                    boundaryOut: "continue",
                    duration: 5,
                }),
                minimalClip({
                    id: "right",
                    duration: 5,
                    references: [minimalClipReference({ uploadedMedia: null })],
                }),
            ],
            catalog,
        );
        const panel = mount("references", 1);
        expect(required<HTMLElement>(panel, "h4").textContent).toBe(
            "<Picture 1>",
        );
        expect(required<HTMLSelectElement>(panel, "select").disabled).toBe(
            true,
        );
        expect(
            required<HTMLButtonElement>(panel, '[data-action="delete"]')
                .disabled,
        ).toBe(false);
        expect(
            required<HTMLElement>(panel, ".vst-capability-notice").textContent
                ?.length,
        ).toBeGreaterThan(0);
    });

    it("keeps the retake repair action reachable beside its read-only persisted fields", () => {
        const { mount, context } = setup(
            [
                minimalClip({
                    retake: {
                        startSeconds: 0,
                        lengthSeconds: 1,
                        strength: 0.5,
                    },
                }),
            ],
            restrict(),
        );
        const panel = mount("retake");
        expect(required<HTMLInputElement>(panel, "input").disabled).toBe(true);
        expect(
            required<HTMLElement>(panel, ".vst-capability-notice").textContent,
        ).toContain("Retake");
        const remove = required<HTMLButtonElement>(
            panel,
            '[data-action="delete"]',
        );
        expect(remove.disabled).toBe(false);
        remove.click();
        expect(context.removeRetake).toHaveBeenCalledWith(0);
    });

    it.each([
        ["keyframes", "deleteRefEntry"],
        ["references", "deleteClipReference"],
    ] as const)("keeps %s visible with a usable repair action when unsupported", (key, method) => {
        const { mount, context } = setup(
            [
                minimalClip({
                    frameRefs: [minimalRef()],
                    references: [minimalClipReference({ uploadedMedia: null })],
                }),
            ],
            restrict(),
        );
        const panel = mount(key);
        expect(
            required<HTMLButtonElement>(panel, '[data-action="add"]').disabled,
        ).toBe(true);
        expect(required<HTMLSelectElement>(panel, "select").disabled).toBe(
            true,
        );
        expect(
            required<HTMLElement>(panel, ".vst-capability-notice").textContent
                ?.length,
        ).toBeGreaterThan(0);
        const repair = required<HTMLButtonElement>(
            panel,
            '[data-action="delete"]',
        );
        expect(repair.disabled).toBe(false);
        repair.click();
        expect(context[method]).toHaveBeenCalledWith(0, 0);
    });

    it("offers persisted IC-LoRAs and relay windows for repair with explanatory controls", () => {
        const clip = minimalClip({
            icLoras: [icLoraFixture()],
            promptWindows: [
                { start: 0, duration: 1, prompt: "retained relay" },
            ],
        });
        const { mount, context } = setup([clip], restrict());
        const ic = mount("ic-loras");
        expect(
            required<HTMLButtonElement>(ic, '[data-action="add"]').disabled,
        ).toBe(true);
        expect(
            required<HTMLElement>(ic, ".vst-capability-notice").textContent
                ?.length,
        ).toBeGreaterThan(0);
        expect(
            required<HTMLButtonElement>(ic, '[data-action="delete"]').disabled,
        ).toBe(false);
        const relay = mount("relay-prompts");
        expect(required<HTMLTextAreaElement>(relay, "textarea").value).toBe(
            "retained relay",
        );
        expect(required<HTMLTextAreaElement>(relay, "textarea").disabled).toBe(
            true,
        );
        expect(
            required<HTMLButtonElement>(relay, '[data-action="add"]').disabled,
        ).toBe(true);
        required<HTMLButtonElement>(relay, '[data-action="delete"]').click();
        expect(context.deleteWindowEntry).toHaveBeenCalledWith(0, 0);
    });

    it("lets unsupported persisted audio return to the architecture default", () => {
        const clip = minimalClip({
            audioSource: "Upload",
            uploadedAudio: { data: "stored.wav", fileName: "stored.wav" },
        });
        const { mount } = setup([clip], restrict());
        const panel = mount("audio");
        const repair = required<HTMLButtonElement>(
            panel,
            ".vst-remove-unsupported-audio",
        );
        expect(repair.disabled).toBe(false);
        repair.click();
        expect(clip.audioSource).toBe("Native");
        expect(clip.saveAudioTrack).toBe(false);
    });

    it("explains unavailable Add actions for audio-only clips, missing endpoints, and exhausted LoRAs", () => {
        const { mount, snapshot } = setup([minimalClip({ audioOnly: true })]);
        expect(
            required<HTMLButtonElement>(mount("stages"), '[data-action="add"]')
                .title,
        ).toContain("Audio-only");
        expect(
            required<HTMLButtonElement>(mount("stages"), '[data-action="add"]')
                .disabled,
        ).toBe(true);
        expect(
            required<HTMLButtonElement>(
                mount("keyframes"),
                '[data-action="add"]',
            ).disabled,
        ).toBe(true);
        snapshot.defaults.modelCatalog.entries[0].enhancements = {
            referencePositions: [],
        };
        expect(
            required<HTMLButtonElement>(
                mount("keyframes"),
                '[data-action="add"]',
            ).title,
        ).toContain("keyframe endpoint");
        const lora = required<HTMLButtonElement>(
            mount("stages"),
            ".vst-stage-lora-add",
        );
        expect(lora.title).toContain("All available LoRAs");
        expect(lora.disabled).toBe(true);
    });
});
