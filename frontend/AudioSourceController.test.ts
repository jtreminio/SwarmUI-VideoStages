import { afterEach, describe, expect, it, jest } from "@jest/globals";
import { AudioSourceController } from "./AudioSourceController";
import {
    mountCheckbox,
    mountSelect,
    mountUploadRow,
} from "./__test_helpers__/dom";
import {
    stubAceStepFunRegistry,
    stubAceStepFunRegistryThrowing,
} from "./__test_helpers__/registries";

type Controller = ReturnType<typeof AudioSourceController>;

const SOURCE_ID = "input_vsaudiosource";
const UPLOAD_ID = "input_vsaudioupload";
const T2A_ID = "input_group_content_texttoaudio_toggle";
const ACESTEPFUN_EVENT = "acestepfun:tracks-changed";

interface DomFixtureOptions {
    initialValue?: string;
    initialOptions?: string[];
    t2aChecked?: boolean;
    omitSource?: boolean;
    omitUpload?: boolean;
    omitToggle?: boolean;
}

interface DomFixture {
    select: HTMLSelectElement | null;
    wrapper: HTMLElement | null;
    fileInput: HTMLInputElement | null;
    toggle: HTMLInputElement | null;
}

const setupDom = (options: DomFixtureOptions = {}): DomFixture => {
    const fixture: DomFixture = {
        select: null,
        wrapper: null,
        fileInput: null,
        toggle: null,
    };

    if (!options.omitSource) {
        fixture.select = mountSelect(SOURCE_ID, {
            value: options.initialValue,
            options: options.initialOptions ?? ["Native", "Upload"],
        });
    }

    if (!options.omitUpload) {
        const row = mountUploadRow(UPLOAD_ID);
        fixture.wrapper = row.wrapper;
        fixture.fileInput = row.fileInput;
    }

    if (!options.omitToggle) {
        fixture.toggle = mountCheckbox(T2A_ID, {
            checked: options.t2aChecked,
        });
    }

    return fixture;
};

describe("AudioSourceController", () => {
    let controller: Controller | null = null;

    afterEach(() => {
        controller?.dispose();
        controller = null;
    });

    describe("buildOptions", () => {
        it("returns Native + Upload by default", () => {
            setupDom();
            controller = AudioSourceController();

            const values = controller.buildOptions().map((o) => o.value);
            expect(values).toEqual(["Native", "Upload"]);
        });

        it("appends Swarm Audio when the text-to-audio toggle is checked", () => {
            setupDom({ t2aChecked: true });
            controller = AudioSourceController();

            const values = controller.buildOptions().map((o) => o.value);
            expect(values).toEqual(["Native", "Upload", "Swarm Audio"]);
        });

        it("omits Swarm Audio when the text-to-audio toggle is missing", () => {
            setupDom({ omitToggle: true });
            controller = AudioSourceController();

            const values = controller.buildOptions().map((o) => o.value);
            expect(values).not.toContain("Swarm Audio");
        });

        it("appends AceStepFun refs when the registry is enabled", () => {
            setupDom();
            stubAceStepFunRegistry(["track-a", "track-b"]);
            controller = AudioSourceController();

            const values = controller.buildOptions().map((o) => o.value);
            expect(values).toEqual(["Native", "Upload", "track-a", "track-b"]);
        });

        it("ignores AceStepFun refs when the registry is disabled", () => {
            setupDom();
            stubAceStepFunRegistry(["track-a"], false);
            controller = AudioSourceController();

            const values = controller.buildOptions().map((o) => o.value);
            expect(values).toEqual(["Native", "Upload"]);
        });

        it("trims, dedupes, and skips empty AceStepFun refs", () => {
            setupDom();
            stubAceStepFunRegistry([
                "track-a",
                "  track-a  ",
                "",
                "  ",
                "track-b",
            ]);
            controller = AudioSourceController();

            const values = controller.buildOptions().map((o) => o.value);
            expect(values).toEqual(["Native", "Upload", "track-a", "track-b"]);
        });

        it("survives a registry that returns a non-array refs payload", () => {
            setupDom();
            window.acestepfunTrackRegistry = {
                getSnapshot: () =>
                    ({
                        enabled: true,
                        trackCount: 0,
                        refs: "not an array",
                    }) as unknown as ReturnType<
                        NonNullable<
                            Window["acestepfunTrackRegistry"]
                        >["getSnapshot"]
                    >,
            };
            controller = AudioSourceController();

            const values = controller.buildOptions().map((o) => o.value);
            expect(values).toEqual(["Native", "Upload"]);
        });
    });

    describe("resolveSelectedValue", () => {
        it("preserves the current value when it exists in the option set", () => {
            setupDom();
            controller = AudioSourceController();
            const options = controller.buildOptions();

            expect(controller.resolveSelectedValue("Upload", options)).toBe(
                "Upload",
            );
        });

        it("falls back to Native when the current value is unknown", () => {
            setupDom();
            controller = AudioSourceController();
            const options = controller.buildOptions();

            expect(controller.resolveSelectedValue("ghost", options)).toBe(
                "Native",
            );
        });

        it("falls back to Native for null/undefined input", () => {
            setupDom();
            controller = AudioSourceController();
            const options = controller.buildOptions();

            expect(
                controller.resolveSelectedValue(
                    null as unknown as string,
                    options,
                ),
            ).toBe("Native");
            expect(
                controller.resolveSelectedValue(
                    undefined as unknown as string,
                    options,
                ),
            ).toBe("Native");
        });
    });

    describe("applyUploadVisibility", () => {
        it("hides the upload row when the source is not Upload", () => {
            const { wrapper } = setupDom({ initialValue: "Native" });
            controller = AudioSourceController();

            controller.applyUploadVisibility();

            expect(wrapper?.style.display).toBe("none");
            expect(wrapper?.dataset.visible_controlled).toBe("true");
        });

        it("reveals the upload row and clears the controlled flag for Upload", () => {
            const { select, wrapper } = setupDom({ initialValue: "Native" });
            controller = AudioSourceController();
            controller.applyUploadVisibility();

            if (!select || !wrapper) {
                throw new Error("fixture missing");
            }
            select.value = "Upload";
            controller.applyUploadVisibility();

            expect(wrapper.style.display).toBe("");
            expect(wrapper.dataset.visible_controlled).toBeUndefined();
        });

        it("is a no-op when the upload container is missing", () => {
            setupDom({ omitUpload: true });
            controller = AudioSourceController();

            expect(() => controller?.applyUploadVisibility()).not.toThrow();
        });
    });

    describe("refreshOptions", () => {
        it("rebuilds <option> elements and selects the resolved value", () => {
            const { select } = setupDom({
                initialValue: "Upload",
                initialOptions: ["Native", "Upload", "stale-extra"],
            });
            controller = AudioSourceController();

            controller.refreshOptions();

            const values = Array.from(select?.options ?? []).map(
                (o) => o.value,
            );
            expect(values).toEqual(["Native", "Upload"]);
            expect(select?.value).toBe("Upload");
        });

        it("falls back to Native when the previously selected value disappears", () => {
            const { select } = setupDom({
                initialValue: "stale",
                initialOptions: ["Native", "Upload", "stale"],
            });
            controller = AudioSourceController();

            controller.refreshOptions();

            expect(select?.value).toBe("Native");
        });

        it("emits a change event after rebuilding options", () => {
            const { select } = setupDom({
                initialValue: "Upload",
                initialOptions: ["Native", "Upload", "stale"],
            });
            controller = AudioSourceController();
            const changeSpy = jest.fn();
            select?.addEventListener("change", changeSpy);

            controller.refreshOptions();

            expect(changeSpy).toHaveBeenCalled();
        });

        it("skips DOM mutation when the option set and selection are unchanged", () => {
            const { select } = setupDom({
                initialValue: "Native",
                initialOptions: ["Native", "Upload"],
            });
            controller = AudioSourceController();
            const originalOptions = select ? Array.from(select.options) : [];

            controller.refreshOptions();

            const afterOptions = select ? Array.from(select.options) : [];
            expect(afterOptions).toHaveLength(originalOptions.length);
            for (let i = 0; i < originalOptions.length; i += 1) {
                expect(afterOptions[i]).toBe(originalOptions[i]);
            }
        });

        it("is a no-op when the source <select> is missing", () => {
            setupDom({ omitSource: true });
            controller = AudioSourceController();

            expect(() => controller?.refreshOptions()).not.toThrow();
        });
    });

    describe("event wiring", () => {
        it("refreshes options when the acestepfun:tracks-changed event fires", () => {
            const { select } = setupDom();
            controller = AudioSourceController();

            stubAceStepFunRegistry(["new-track"]);
            document.dispatchEvent(new Event(ACESTEPFUN_EVENT));

            const values = Array.from(select?.options ?? []).map(
                (o) => o.value,
            );
            expect(values).toContain("new-track");
        });

        it("re-applies upload visibility when the source select fires a change event", () => {
            const { select, wrapper } = setupDom({ initialValue: "Native" });
            controller = AudioSourceController();
            controller.applyUploadVisibility();
            if (!select || !wrapper) {
                throw new Error("fixture missing");
            }
            expect(wrapper.style.display).toBe("none");

            select.value = "Upload";
            select.dispatchEvent(new Event("change", { bubbles: true }));

            expect(wrapper.style.display).toBe("");
        });

        it("ignores change events from unrelated elements", () => {
            const { wrapper } = setupDom({ initialValue: "Native" });
            controller = AudioSourceController();
            controller.applyUploadVisibility();
            if (!wrapper) {
                throw new Error("fixture missing");
            }
            const originalDisplay = wrapper.style.display;

            const unrelated = document.createElement("input");
            unrelated.id = "some-other-input";
            document.body.appendChild(unrelated);
            unrelated.dispatchEvent(new Event("change", { bubbles: true }));

            expect(wrapper.style.display).toBe(originalDisplay);
        });

        it("refreshes options when the source select receives a focusin", () => {
            const { select } = setupDom({
                initialValue: "Upload",
                initialOptions: ["Native", "Upload", "stale"],
            });
            controller = AudioSourceController();

            select?.dispatchEvent(new Event("focusin", { bubbles: true }));

            const values = Array.from(select?.options ?? []).map(
                (o) => o.value,
            );
            expect(values).toEqual(["Native", "Upload"]);
        });

        it("refreshes options when the text-to-audio toggle changes", () => {
            const { select, toggle } = setupDom();
            controller = AudioSourceController();
            // The toggle listener is wired by bindText2AudioToggle, which only
            // runs as part of runOnEachBuild (the postParamBuildSteps hook).
            controller.runOnEachBuild();
            if (!toggle) {
                throw new Error("fixture missing");
            }

            toggle.checked = true;
            toggle.dispatchEvent(new Event("change"));

            const values = Array.from(select?.options ?? []).map(
                (o) => o.value,
            );
            expect(values).toContain("Swarm Audio");
        });

        it("stops responding to events after dispose", () => {
            const { select, wrapper } = setupDom({ initialValue: "Native" });
            controller = AudioSourceController();
            controller.applyUploadVisibility();
            if (!select || !wrapper) {
                throw new Error("fixture missing");
            }
            controller.dispose();
            controller = null;

            select.value = "Upload";
            select.dispatchEvent(new Event("change", { bubbles: true }));

            expect(wrapper.style.display).toBe("none");
        });
    });

    describe("postParamBuildSteps integration", () => {
        it("registers runOnEachBuild on construction", () => {
            setupDom();
            const before = (
                globalThis as { postParamBuildSteps?: Array<unknown> }
            ).postParamBuildSteps;
            expect(before).toEqual([]);

            controller = AudioSourceController();

            const after = (
                globalThis as { postParamBuildSteps?: Array<() => void> }
            ).postParamBuildSteps;
            expect(after).toHaveLength(1);
            expect(typeof after?.[0]).toBe("function");
        });

        it("runs the registered build step without throwing and refreshes the DOM", () => {
            const { select } = setupDom({
                initialValue: "Upload",
                initialOptions: ["Native", "Upload", "stale"],
            });
            controller = AudioSourceController();
            const steps = (
                globalThis as { postParamBuildSteps?: Array<() => void> }
            ).postParamBuildSteps;
            if (!steps) {
                throw new Error("postParamBuildSteps was not seeded");
            }

            for (const step of steps) {
                step();
            }

            const values = Array.from(select?.options ?? []).map(
                (o) => o.value,
            );
            expect(values).toEqual(["Native", "Upload"]);
        });

        it("is resilient when the DOM is empty", () => {
            setupDom({
                omitSource: true,
                omitUpload: true,
                omitToggle: true,
            });
            controller = AudioSourceController();

            expect(() => controller?.runOnEachBuild()).not.toThrow();
        });

        it("swallows errors thrown during a refresh and logs them", () => {
            setupDom();
            stubAceStepFunRegistryThrowing(new Error("boom"));
            const consoleSpy = jest
                .spyOn(console, "log")
                .mockImplementation(() => {});
            controller = AudioSourceController();

            expect(() => controller?.runOnEachBuild()).not.toThrow();
            expect(consoleSpy).toHaveBeenCalledWith(
                "AudioSourceController: param build sync failed",
                expect.any(Error),
            );

            consoleSpy.mockRestore();
        });
    });
});
