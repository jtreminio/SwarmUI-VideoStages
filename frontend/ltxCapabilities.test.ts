import { afterEach, describe, expect, it } from "@jest/globals";
import {
    filterLtxModelOptions,
    isCurrentRootLtxVideoModel,
    isLtxV2CompatId,
    isLtxVideoModelValue,
} from "./ltxCapabilities";

type ModelHelpersStub = {
    getDataFor?: (
        category: string,
        modelName: string,
    ) => {
        modelClass?: {
            compatClass?: { id?: string; isText2Video?: boolean };
        };
    } | null;
    compatClasses?: Record<string, { id?: string; isText2Video?: boolean }>;
};

const globals = globalThis as unknown as {
    modelsHelpers?: ModelHelpersStub;
    currentModelHelper?: { curCompatClass?: string };
};

afterEach(() => {
    delete globals.modelsHelpers;
    delete globals.currentModelHelper;
});

describe("LTX frontend capabilities", () => {
    it.each([
        "ltxv2",
        "LTX-V2",
        "ltx_v2",
    ])("recognizes the canonical LTX v2 compat id %s", (id) => {
        expect(isLtxV2CompatId(id)).toBe(true);
    });

    it("uses model metadata as the authoritative family gate", () => {
        globals.modelsHelpers = {
            getDataFor: (_category, modelName) => ({
                modelClass: {
                    compatClass: {
                        id: modelName === "good.ckpt" ? "ltxv2" : "wan-22-5b",
                        isText2Video: true,
                    },
                },
            }),
        };

        expect(isLtxVideoModelValue("good.ckpt")).toBe(true);
        expect(isLtxVideoModelValue("ltx-looking-but-wan.ckpt")).toBe(false);
    });

    it("falls back to recognizable LTX names before model metadata exists", () => {
        expect(isLtxVideoModelValue("LTX-2.3/model.safetensors")).toBe(true);
        expect(isLtxVideoModelValue("models/ltxv2.safetensors")).toBe(true);
        expect(isLtxVideoModelValue("wan-2.2.safetensors")).toBe(false);
        expect(isLtxVideoModelValue("generic-video.safetensors")).toBe(false);
    });

    it("filters model options while preserving their matching labels", () => {
        globals.modelsHelpers = {
            getDataFor: (_category, modelName) => ({
                modelClass: {
                    compatClass: {
                        id: modelName.startsWith("ltx") ? "ltxv2" : "wan-22-5b",
                    },
                },
            }),
        };

        expect(
            filterLtxModelOptions(
                ["ltx-a", "wan-a", "ltx-b"],
                ["LTX A", "WAN A", "LTX B"],
            ),
        ).toEqual({
            values: ["ltx-a", "ltx-b"],
            labels: ["LTX A", "LTX B"],
        });
    });

    it("requires LTX compatibility for the current root model", () => {
        globals.modelsHelpers = {
            getDataFor: () => null,
            compatClasses: {
                current: { id: "ltxv2", isText2Video: true },
            },
        };
        globals.currentModelHelper = { curCompatClass: "current" };
        expect(isCurrentRootLtxVideoModel("generic-root.ckpt")).toBe(true);

        globals.modelsHelpers.compatClasses = {
            current: { id: "wan-22-5b", isText2Video: true },
        };
        expect(isCurrentRootLtxVideoModel("generic-root.ckpt")).toBe(false);
    });
});
