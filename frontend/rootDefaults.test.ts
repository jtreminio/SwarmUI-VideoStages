import { beforeEach, describe, expect, it } from "@jest/globals";

import { mountCheckbox, mountSelect } from "./__test_helpers__/dom";
import { getRootDefaults, readInheritedDimsSignature } from "./rootDefaults";

const mountNumber = (id: string, value: number): HTMLInputElement => {
    const input = document.createElement("input");
    input.type = "number";
    input.id = id;
    input.value = `${value}`;
    document.body.appendChild(input);
    return input;
};

describe("root dimension defaults", () => {
    beforeEach(() => {
        document.body.innerHTML = "";
        mountSelect("input_aspectratio", {
            value: "16:9",
            options: ["1:1", "16:9", "Custom"],
        });
        mountNumber("input_sidelength", 1024);
        mountCheckbox("input_sidelength_toggle", { checked: true });
        mountNumber("input_width", 640);
        mountNumber("input_height", 640);
    });

    it("probes enabled host aspect ratio and side length using SwarmUI math", () => {
        expect(getRootDefaults()).toMatchObject({
            aspectRatio: "16:9",
            sideLength: 1024,
            width: 1344,
            height: 768,
        });
    });

    it("falls back to host model dimensions when Side Length is unchecked", () => {
        const toggle = document.getElementById(
            "input_sidelength_toggle",
        ) as HTMLInputElement;
        toggle.checked = false;
        expect(getRootDefaults()).toMatchObject({
            aspectRatio: "16:9",
            sideLength: null,
            width: 640,
            height: 640,
        });
    });

    it("includes ratio, side length, and toggle state in the watcher signature", () => {
        const before = readInheritedDimsSignature();
        const side = document.getElementById(
            "input_sidelength",
        ) as HTMLInputElement;
        side.value = "768";
        expect(readInheritedDimsSignature()).not.toBe(before);
        const withSide = readInheritedDimsSignature();
        const toggle = document.getElementById(
            "input_sidelength_toggle",
        ) as HTMLInputElement;
        toggle.checked = false;
        expect(readInheritedDimsSignature()).not.toBe(withSide);
    });

    it("uses host step and CFG limits without applying extension caps", () => {
        const steps = mountNumber("input_videosteps", 32);
        steps.min = "2";
        steps.max = "240";
        steps.step = "2";
        const cfg = mountNumber("input_videocfg", 12);
        cfg.min = "-5";
        cfg.max = "30";
        cfg.step = "0.25";

        expect(getRootDefaults()).toMatchObject({
            stepsMin: 2,
            stepsMax: 240,
            stepsStep: 2,
            cfgScaleMin: -5,
            cfgScaleMax: 30,
            cfgScaleStep: 0.25,
        });
    });
});
