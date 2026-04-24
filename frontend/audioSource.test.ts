import { afterEach, describe, expect, it, jest } from "@jest/globals";
import { mountSelect } from "./__test_helpers__/dom";
import {
    stubAceStepFunRegistry,
    stubAceStepFunRegistryThrowing,
} from "./__test_helpers__/registries";
import { audioSource } from "./audioSource";

type Controller = ReturnType<typeof audioSource>;

const SOURCE_ID = "vsclip0_audioSource";
const ACESTEPFUN_EVENT = "acestepfun:tracks-changed";

interface DomFixtureOptions {
    initialValue?: string;
    initialOptions?: string[];
    omitSource?: boolean;
}

interface DomFixture {
    select: HTMLSelectElement | null;
}

const setupDom = (options: DomFixtureOptions = {}): DomFixture => {
    const fixture: DomFixture = {
        select: null,
    };

    if (!options.omitSource) {
        fixture.select = mountSelect(SOURCE_ID, {
            value: options.initialValue,
            options: options.initialOptions ?? ["Native", "Upload"],
        });
        if (fixture.select) {
            fixture.select.dataset.clipField = "audioSource";
            fixture.select.dataset.clipIdx = "0";
        }
    }

    return fixture;
};

describe("audioSource", () => {
    let controller: Controller | null = null;

    afterEach(() => {
        controller?.dispose();
        controller = null;
    });

    describe("buildOptions", () => {
        it("returns Native + Upload by default", () => {
            setupDom();
            controller = audioSource();

            const values = controller.buildOptions().map((o) => o.value);
            expect(values).toEqual(["Native", "Upload"]);
        });

        it("appends AceStepFun refs when the registry is enabled", () => {
            setupDom();
            stubAceStepFunRegistry(["track-a", "track-b"]);
            controller = audioSource();

            const values = controller.buildOptions().map((o) => o.value);
            expect(values).toEqual(["Native", "Upload", "track-a", "track-b"]);
        });

        it("labels AceStepFun audio refs with a friendly display name", () => {
            setupDom();
            stubAceStepFunRegistry(["audio0"]);
            controller = audioSource();

            const options = controller.buildOptions();
            expect(options).toContainEqual({
                value: "audio0",
                label: "AceStepFun Audio 0",
            });
        });

        it("ignores AceStepFun refs when the registry is disabled", () => {
            setupDom();
            stubAceStepFunRegistry(["track-a"], false);
            controller = audioSource();

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
            controller = audioSource();

            const values = controller.buildOptions().map((o) => o.value);
            expect(values).toEqual(["Native", "Upload", "track-a", "track-b"]);
        });

        it("survives a registry that returns a non-array refs payload", () => {
            setupDom();
            window.acestepfunTrackRegistry = {
                getSnapshot: () => ({
                    enabled: true,
                    trackCount: 0,
                    // @ts-expect-error intentional malformed snapshot
                    refs: "not an array",
                }),
            };
            controller = audioSource();

            const values = controller.buildOptions().map((o) => o.value);
            expect(values).toEqual(["Native", "Upload"]);
        });
    });

    describe("resolveSelectedValue", () => {
        it("preserves the current value when it exists in the option set", () => {
            setupDom();
            controller = audioSource();
            const options = controller.buildOptions();

            expect(controller.resolveSelectedValue("Upload", options)).toBe(
                "Upload",
            );
        });

        it("falls back to Native when the current value is unknown", () => {
            setupDom();
            controller = audioSource();
            const options = controller.buildOptions();

            expect(controller.resolveSelectedValue("ghost", options)).toBe(
                "Native",
            );
        });

        it("falls back to Native for null/undefined input", () => {
            setupDom();
            controller = audioSource();
            const options = controller.buildOptions();

            expect(controller.resolveSelectedValue(null, options)).toBe(
                "Native",
            );
            expect(controller.resolveSelectedValue(undefined, options)).toBe(
                "Native",
            );
        });
    });

    describe("refreshOptions", () => {
        it("rebuilds <option> elements and selects the resolved value", () => {
            const { select } = setupDom({
                initialValue: "Upload",
                initialOptions: ["Native", "Upload", "stale-extra"],
            });
            controller = audioSource();

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
            controller = audioSource();

            controller.refreshOptions();

            expect(select?.value).toBe("Native");
        });

        it("emits a change event after rebuilding options", () => {
            const { select } = setupDom({
                initialValue: "Upload",
                initialOptions: ["Native", "Upload", "stale"],
            });
            controller = audioSource();
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
            controller = audioSource();
            const originalOptions = select ? Array.from(select.options) : [];

            controller.refreshOptions();

            const afterOptions = select ? Array.from(select.options) : [];
            expect(afterOptions).toHaveLength(originalOptions.length);
            for (let i = 0; i < originalOptions.length; i += 1) {
                expect(afterOptions[i]).toBe(originalOptions[i]);
            }
        });

        it("rebuilds options when labels change but values are unchanged", () => {
            const { select } = setupDom({
                initialValue: "audio0",
                initialOptions: ["Native", "Upload", "audio0"],
            });
            stubAceStepFunRegistry(["audio0"]);
            controller = audioSource();

            controller.refreshOptions();

            const aceStepFunOption = Array.from(select?.options ?? []).find(
                (o) => o.value === "audio0",
            );
            expect(aceStepFunOption?.textContent).toBe("AceStepFun Audio 0");
            expect(select?.value).toBe("audio0");
        });

        it("is a no-op when the source <select> is missing", () => {
            setupDom({ omitSource: true });
            const c = audioSource();
            controller = c;
            expect(() => c.refreshOptions()).not.toThrow();
        });
    });

    describe("event wiring", () => {
        it("refreshes options when the acestepfun:tracks-changed event fires", () => {
            const { select } = setupDom();
            controller = audioSource();

            stubAceStepFunRegistry(["new-track"]);
            document.dispatchEvent(new Event(ACESTEPFUN_EVENT));

            const values = Array.from(select?.options ?? []).map(
                (o) => o.value,
            );
            expect(values).toContain("new-track");
        });

        it("refreshes options when the source select receives a focusin", () => {
            const { select } = setupDom({
                initialValue: "Upload",
                initialOptions: ["Native", "Upload", "stale"],
            });
            controller = audioSource();

            select?.dispatchEvent(new Event("focusin", { bubbles: true }));

            const values = Array.from(select?.options ?? []).map(
                (o) => o.value,
            );
            expect(values).toEqual(["Native", "Upload"]);
        });

        it("stops responding to acestepfun events after dispose", () => {
            const { select } = setupDom();
            controller = audioSource();
            const originalCount = select?.options.length ?? 0;
            controller.dispose();
            controller = null;

            stubAceStepFunRegistry(["new-track"]);
            document.dispatchEvent(new Event(ACESTEPFUN_EVENT));

            expect(select?.options.length).toBe(originalCount);
        });
    });

    describe("postParamBuildSteps integration", () => {
        it("registers runOnEachBuild on construction", () => {
            setupDom();
            expect(postParamBuildSteps).toEqual([]);

            controller = audioSource();

            const steps = postParamBuildSteps;
            expect(steps).toHaveLength(1);
            expect(typeof steps[0]).toBe("function");
        });

        it("runs the registered build step without throwing and refreshes the DOM", () => {
            const { select } = setupDom({
                initialValue: "Upload",
                initialOptions: ["Native", "Upload", "stale"],
            });
            controller = audioSource();
            for (const step of postParamBuildSteps) {
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
            });
            const c = audioSource();
            controller = c;
            expect(() => c.runOnEachBuild()).not.toThrow();
        });

        it("swallows errors thrown during a refresh and logs them", () => {
            setupDom();
            stubAceStepFunRegistryThrowing(new Error("boom"));
            const consoleSpy = jest
                .spyOn(console, "warn")
                .mockImplementation(() => {});
            const c = audioSource();
            controller = c;

            expect(() => c.runOnEachBuild()).not.toThrow();
            expect(consoleSpy).toHaveBeenCalledWith(
                "audioSource: param build sync failed",
                expect.any(Error),
            );

            consoleSpy.mockRestore();
        });
    });
});
