import { afterEach, describe, expect, it } from "@jest/globals";
import {
    buildImageSourceOptions,
    resolveImageSourceValue,
} from "./imageSource";

const stubBase2Edit = (refs: string[]): void => {
    window.base2editStageRegistry = {
        getSnapshot: () => ({
            enabled: true,
            stageCount: refs.length,
            refs,
        }),
    };
};

describe("imageSource", () => {
    afterEach(() => {
        window.base2editStageRegistry = undefined;
    });

    describe("buildImageSourceOptions", () => {
        it("always offers Base, Refiner and Upload", () => {
            const values = buildImageSourceOptions().map((o) => o.value);
            expect(values).toEqual(["Base", "Refiner", "Upload"]);
        });

        it("appends one option per published Base2Edit stage", () => {
            stubBase2Edit(["edit0", "edit1"]);
            const options = buildImageSourceOptions();
            expect(options.map((o) => o.value)).toEqual([
                "Base",
                "Refiner",
                "Upload",
                "edit0",
                "edit1",
            ]);
            expect(options.find((o) => o.value === "edit1")?.label).toBe(
                "Base2Edit Edit 1 Output",
            );
        });

        it("surfaces an unknown non-Base2Edit selection as a selectable passthrough option", () => {
            const options = buildImageSourceOptions("SomethingCustom");
            expect(options[0]).toEqual({
                value: "SomethingCustom",
                label: "SomethingCustom",
                disabled: false,
            });
        });

        it("surfaces a missing Base2Edit selection as a disabled leading option", () => {
            const options = buildImageSourceOptions("edit9");
            expect(options[0]).toEqual({
                value: "edit9",
                label: "Missing Base2Edit edit9",
                disabled: true,
            });
        });

        it("does not duplicate a selection already present in the option set", () => {
            const values = buildImageSourceOptions("Refiner").map(
                (o) => o.value,
            );
            expect(values).toEqual(["Base", "Refiner", "Upload"]);
        });
    });

    describe("resolveImageSourceValue", () => {
        it("preserves a value that exists in the option set", () => {
            const options = buildImageSourceOptions();
            expect(resolveImageSourceValue("Upload", options)).toBe("Upload");
        });

        it("falls back to Refiner for an unknown or empty value", () => {
            const options = buildImageSourceOptions();
            expect(resolveImageSourceValue("Nope", options)).toBe("Refiner");
            expect(resolveImageSourceValue("", options)).toBe("Refiner");
        });
    });
});
