import { describe, expect, it } from "@jest/globals";
import { stubAceStepFunRegistry } from "./__test_helpers__/registries";
import {
    AUDIO_SOURCE_CONTROLNET,
    buildAudioSourceOptions,
    buildSegmentAudioSourceOptions,
    canUseClipLengthFromAudio,
    isControlNetAudioSource,
    resolveAudioSourceValue,
} from "./audioSource";

describe("audioSource", () => {
    describe("buildAudioSourceOptions", () => {
        it("returns Native + Upload by default", () => {
            const values = buildAudioSourceOptions().map((o) => o.value);
            expect(values).toEqual(["Native", "Upload"]);
        });

        it("appends AceStepFun refs when the registry is enabled", () => {
            stubAceStepFunRegistry(["track-a", "track-b"]);

            const values = buildAudioSourceOptions().map((o) => o.value);
            expect(values).toEqual(["Native", "Upload", "track-a", "track-b"]);
        });

        it("labels AceStepFun audio refs with a friendly display name", () => {
            stubAceStepFunRegistry(["audio0"]);

            expect(buildAudioSourceOptions()).toContainEqual({
                value: "audio0",
                label: "AceStepFun Audio 0",
            });
        });

        it("keeps a selected AceStepFun ref labeled when the registry omits it", () => {
            expect(buildAudioSourceOptions("audio0")).toContainEqual({
                value: "audio0",
                label: "AceStepFun Audio 0",
            });
        });

        it("ignores AceStepFun refs when the registry is disabled", () => {
            stubAceStepFunRegistry(["track-a"], false);

            const values = buildAudioSourceOptions().map((o) => o.value);
            expect(values).toEqual(["Native", "Upload"]);
        });

        it("trims, dedupes, and skips empty AceStepFun refs", () => {
            stubAceStepFunRegistry([
                "track-a",
                "  track-a  ",
                "",
                "  ",
                "track-b",
            ]);

            const values = buildAudioSourceOptions().map((o) => o.value);
            expect(values).toEqual(["Native", "Upload", "track-a", "track-b"]);
        });

        it("survives a registry that returns a non-array refs payload", () => {
            window.acestepfunTrackRegistry = {
                getSnapshot: () => ({
                    enabled: true,
                    trackCount: 0,
                    // @ts-expect-error intentional malformed snapshot
                    refs: "not an array",
                }),
            };

            const values = buildAudioSourceOptions().map((o) => o.value);
            expect(values).toEqual(["Native", "Upload"]);
        });
    });

    describe("buildSegmentAudioSourceOptions", () => {
        it("offers Upload plus AceStepFun refs, without Native/ControlNet", () => {
            stubAceStepFunRegistry(["audio0"]);

            const values = buildSegmentAudioSourceOptions().map((o) => o.value);
            expect(values).toEqual(["Upload", "audio0"]);
        });

        it("keeps a selected AceStepFun ref that left the registry", () => {
            expect(buildSegmentAudioSourceOptions("audio2")).toContainEqual({
                value: "audio2",
                label: "AceStepFun Audio 2",
            });
        });
    });

    describe("resolveAudioSourceValue", () => {
        it("preserves the current value when it exists in the option set", () => {
            const options = buildAudioSourceOptions();

            expect(resolveAudioSourceValue("Upload", options)).toBe("Upload");
        });

        it("falls back to Native when the current value is unknown", () => {
            const options = buildAudioSourceOptions();

            expect(resolveAudioSourceValue("ghost", options)).toBe("Native");
        });

        it("falls back to Native for null/undefined input", () => {
            const options = buildAudioSourceOptions();

            expect(
                resolveAudioSourceValue(null as unknown as string, options),
            ).toBe("Native");
            expect(
                resolveAudioSourceValue(
                    undefined as unknown as string,
                    options,
                ),
            ).toBe("Native");
        });
    });

    describe("ControlNet audio source", () => {
        it("isControlNetAudioSource matches only the literal 'ControlNet' value", () => {
            expect(isControlNetAudioSource("ControlNet")).toBe(true);
            expect(isControlNetAudioSource("  ControlNet  ")).toBe(true);
            expect(isControlNetAudioSource("ControlNet 1")).toBe(false);
            expect(isControlNetAudioSource("controlnet")).toBe(false);
            expect(isControlNetAudioSource("Native")).toBe(false);
            expect(isControlNetAudioSource("")).toBe(false);
        });

        it("canUseClipLengthFromAudio includes ControlNet audio sources", () => {
            expect(canUseClipLengthFromAudio(AUDIO_SOURCE_CONTROLNET)).toBe(
                true,
            );
            expect(canUseClipLengthFromAudio("Upload")).toBe(true);
            expect(canUseClipLengthFromAudio("audio0")).toBe(true);
            expect(canUseClipLengthFromAudio("Native")).toBe(false);
        });

        it("includes ControlNet only when context.controlNetEnabled is true", () => {
            const without = buildAudioSourceOptions("", {
                controlNetEnabled: false,
            }).map((o) => o.value);
            expect(without).not.toContain(AUDIO_SOURCE_CONTROLNET);

            const withControlNet = buildAudioSourceOptions("", {
                controlNetEnabled: true,
            }).map((o) => o.value);
            expect(withControlNet).toContain(AUDIO_SOURCE_CONTROLNET);
        });

        it("ControlNet option is appended after Upload and AceStepFun refs", () => {
            stubAceStepFunRegistry(["audio0"]);

            const values = buildAudioSourceOptions("", {
                controlNetEnabled: true,
            }).map((o) => o.value);
            expect(values).toEqual([
                "Native",
                "Upload",
                "audio0",
                AUDIO_SOURCE_CONTROLNET,
            ]);
        });

        it("maps architecture-neutral Disabled to the ordinary Native authoring choice", () => {
            const values = buildAudioSourceOptions("", {
                allowedKinds: ["Disabled", "Upload"],
            }).map((option) => option.value);

            expect(values).toEqual(["Native", "Upload"]);
        });
    });
});
