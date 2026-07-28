import { afterEach, describe, expect, it, jest } from "@jest/globals";
import { testArchitectureCatalog } from "../__test_helpers__/architectureFixtures";
import { minimalClip } from "../__test_helpers__/clipFixtures";
import {
    setVideoStagesHostBridgeForTests,
    type VideoStagesHostBridge,
} from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import { isRootTextToVideoModel } from "../swarmInputs";
import {
    ARCHITECTURE_CATALOG_API,
    buildArchitectureModelCatalog,
    invalidateArchitectureCatalog,
    loadAuthoritativeArchitectureCatalog,
    parseVideoArchitectureCatalog,
} from "./catalog";
import { createCapabilityViewResolver } from "./policy";

const dto = {
    architectures: [
        {
            ...testArchitectureCatalog().architectures[0],
            profiles: [
                ...testArchitectureCatalog().architectures[0].profiles,
                {
                    id: "synthetic-profile",
                    label: "Synthetic Profile",
                    capabilities: ["normal-lora"],
                    rules: [],
                },
            ],
        },
    ],
    models: [
        {
            modelName: "ltx-two.safetensors",
            architectureId: "ltx2",
            modelProfileId: "ltx-2.3",
            compatId: "ltxv2",
        },
        {
            modelName: "ltx-two-three.safetensors",
            architectureId: "ltx2",
            modelProfileId: "synthetic-profile",
            compatId: "ltxv2",
        },
    ],
};

afterEach(() => {
    invalidateArchitectureCatalog();
    setVideoStagesHostBridgeForTests(null);
    jest.restoreAllMocks();
    document.body.innerHTML = "";
});

describe("architecture catalog", () => {
    it("strictly parses the backend DTO", () => {
        expect(parseVideoArchitectureCatalog(dto)).toEqual(dto);
        expect(
            parseVideoArchitectureCatalog({
                architectures: [
                    {
                        id: "bad",
                        label: "Bad",
                        capabilities: { boundaryModes: ["cut"] },
                    },
                ],
                models: [],
            }),
        ).toBeNull();
    });

    it("rejects the complete catalog on duplicate or dangling identities", () => {
        expect(
            parseVideoArchitectureCatalog({
                ...dto,
                architectures: [dto.architectures[0], dto.architectures[0]],
            }),
        ).toBeNull();
        expect(
            parseVideoArchitectureCatalog({
                ...dto,
                architectures: [
                    {
                        ...dto.architectures[0],
                        defaultProfileId: "missing-profile",
                    },
                ],
            }),
        ).toBeNull();
        expect(
            parseVideoArchitectureCatalog({
                ...dto,
                models: [
                    {
                        ...dto.models[0],
                        modelProfileId: "missing-profile",
                    },
                ],
            }),
        ).toBeNull();
        expect(
            parseVideoArchitectureCatalog({
                ...dto,
                models: [dto.models[0], dto.models[0]],
            }),
        ).toBeNull();
        expect(
            parseVideoArchitectureCatalog({
                ...dto,
                architectures: [
                    {
                        ...dto.architectures[0],
                        profiles: [
                            {
                                ...dto.architectures[0].profiles[0],
                                id: " ltx-2 ",
                            },
                        ],
                    },
                ],
            }),
        ).toBeNull();
    });

    it("requires the complete cut/continue/crossfade boundary contract", () => {
        const missing = structuredClone(dto);
        delete missing.architectures[0].boundaryRules.cut;
        expect(parseVideoArchitectureCatalog(missing)).toBeNull();

        const extra = structuredClone(dto) as typeof dto & {
            architectures: Array<
                (typeof dto.architectures)[number] & {
                    boundaryRules: Record<string, unknown>;
                }
            >;
        };
        extra.architectures[0].boundaryRules.dissolve =
            extra.architectures[0].boundaryRules.cut;
        expect(parseVideoArchitectureCatalog(extra)).toBeNull();
    });

    it("rejects invalid rule scope, support semantics, codes, and boundary grids", () => {
        const wrongScope = structuredClone(dto);
        wrongScope.architectures[0].boundaryRules.continue.scope = "clip";
        expect(parseVideoArchitectureCatalog(wrongScope)).toBeNull();

        const missingConditionalConstraints = structuredClone(dto);
        missingConditionalConstraints.architectures[0].boundaryRules.continue.constraints =
            null;
        expect(
            parseVideoArchitectureCatalog(missingConditionalConstraints),
        ).toBeNull();

        const unsupportedWithConstraints = structuredClone(dto);
        unsupportedWithConstraints.architectures[0].boundaryRules.continue = {
            ...unsupportedWithConstraints.architectures[0].boundaryRules
                .continue,
            support: "unsupported",
        };
        expect(
            parseVideoArchitectureCatalog(unsupportedWithConstraints),
        ).toBeNull();

        const invalidGrid = structuredClone(dto);
        const constraints =
            invalidGrid.architectures[0].boundaryRules.continue.constraints;
        if (!constraints) throw new Error("continue constraints missing");
        constraints.frameStep = 0;
        expect(parseVideoArchitectureCatalog(invalidGrid)).toBeNull();

        const duplicateCode = structuredClone(dto);
        duplicateCode.architectures[0].rules[0].code =
            duplicateCode.architectures[0].boundaryRules.continue.code;
        expect(parseVideoArchitectureCatalog(duplicateCode)).toBeNull();
    });

    it("rejects lossy model metadata instead of coercing it", () => {
        const malformed = structuredClone(dto) as unknown as {
            architectures: typeof dto.architectures;
            models: Array<Record<string, unknown>>;
        };
        malformed.models[0].compatId = 23;
        expect(parseVideoArchitectureCatalog(malformed)).toBeNull();
    });

    it("uses exact authoritative boundary constraint keys for grid and target gates", () => {
        const parsed = parseVideoArchitectureCatalog(dto);
        if (!parsed) throw new Error("catalog did not parse");
        const catalog = {
            source: "backend" as const,
            architectures: parsed.architectures,
            entries: parsed.models.map((model) => ({
                value: model.modelName,
                label: model.modelName,
                compatId: model.compatId ?? null,
                modelClassId: null,
                architectureId: model.architectureId,
                modelProfileId: model.modelProfileId,
            })),
        };
        const left = minimalClip({ boundaryOut: "continue" });
        const right = minimalClip({
            sourceVideo: {
                data: "data:video/mp4;base64,AA==",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
        });
        const boundary = createCapabilityViewResolver(catalog).forBoundary(
            left,
            right,
        );

        expect(boundary.overlapConstraints("continue")).toMatchObject({
            frameStep: 8,
            minFrames: 8,
            maxFrames: 48,
            defaultFrames: 8,
            continuityExtraFrames: 1,
        });
        expect(boundary.effective("continue")).toBe("cut");
    });

    it("coalesces and caches the exact host route request", async () => {
        const requestJson = jest.fn<VideoStagesHostBridge["requestJson"]>(
            async () => dto,
        );
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson,
        });

        const [first, second] = await Promise.all([
            loadAuthoritativeArchitectureCatalog(),
            loadAuthoritativeArchitectureCatalog(),
        ]);
        const third = await loadAuthoritativeArchitectureCatalog();

        expect(first).toEqual(dto);
        expect(second).toEqual(dto);
        expect(third).toEqual(dto);
        expect(requestJson).toHaveBeenCalledTimes(1);
        expect(requestJson).toHaveBeenCalledWith(ARCHITECTURE_CATALOG_API);
        expect(ARCHITECTURE_CATALOG_API).toBe(
            "VideoStagesGetArchitectureCatalog",
        );
    });

    it("re-requests the catalog after an invalidation so new models resolve", async () => {
        const newModel = {
            modelName: "ltx-brand-new.safetensors",
            architectureId: "ltx2",
            modelProfileId: "ltx-2.3",
            compatId: "ltxv2",
        };
        const requestJson = jest
            .fn<VideoStagesHostBridge["requestJson"]>()
            .mockResolvedValueOnce(dto)
            .mockResolvedValue({ ...dto, models: [...dto.models, newModel] });
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson,
        });

        await loadAuthoritativeArchitectureCatalog();
        expect(
            buildArchitectureModelCatalog([newModel.modelName], ["Brand New"])
                .entries[0].architectureId,
        ).toBeNull();

        invalidateArchitectureCatalog();
        await loadAuthoritativeArchitectureCatalog();

        expect(requestJson).toHaveBeenCalledTimes(2);
        expect(
            buildArchitectureModelCatalog([newModel.modelName], ["Brand New"])
                .entries[0],
        ).toMatchObject({
            architectureId: "ltx2",
            modelProfileId: "ltx-2.3",
        });
    });

    it("uses backend model profiles even when compat ids are identical", async () => {
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => dto,
            getModelCompatId: () => "ltxv2",
        });
        await loadAuthoritativeArchitectureCatalog();

        const catalog = buildArchitectureModelCatalog(
            dto.models.map((model) => model.modelName),
            ["LTX 2.3", "Synthetic"],
        );

        expect(catalog.source).toBe("backend");
        expect(catalog.entries.map((entry) => entry.modelProfileId)).toEqual([
            "ltx-2.3",
            "synthetic-profile",
        ]);
    });

    it("retains authoritative backend models that are absent from the host dropdown", async () => {
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => dto,
        });
        await loadAuthoritativeArchitectureCatalog();

        const catalog = buildArchitectureModelCatalog([], []);

        expect(catalog.source).toBe("backend");
        expect(catalog.entries).toEqual([
            expect.objectContaining({
                value: "ltx-two.safetensors",
                label: "ltx-two.safetensors",
                compatId: "ltxv2",
                architectureId: "ltx2",
                modelProfileId: "ltx-2.3",
            }),
            expect.objectContaining({
                value: "ltx-two-three.safetensors",
                label: "ltx-two-three.safetensors",
                compatId: "ltxv2",
                architectureId: "ltx2",
                modelProfileId: "synthetic-profile",
            }),
        ]);
    });

    it("distinguishes the LTX 2.3 bootstrap profile by model class", () => {
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            getModelCompatId: () => "ltxv2",
            getModelClassId: () => "lightricks-ltx-video-2-3",
        });

        expect(
            buildArchitectureModelCatalog(
                ["opaque-model.safetensors"],
                ["Opaque Model"],
            ).entries[0],
        ).toMatchObject({
            architectureId: "ltx2",
            modelProfileId: "ltx-2.3",
        });
    });

    it("does not bootstrap older LTX models from the broad compat id alone", () => {
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            getModelCompatId: () => "ltxv2",
            getModelClassId: () => null,
        });

        expect(
            buildArchitectureModelCatalog(
                ["ltx-two.safetensors"],
                ["Older LTX"],
            ).entries[0],
        ).toMatchObject({
            architectureId: null,
            modelProfileId: null,
        });
    });

    it("bootstraps a clearly named LTX 2.3 model without host metadata", () => {
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            getModelCompatId: () => null,
            getModelClassId: () => null,
        });

        expect(
            buildArchitectureModelCatalog(
                ["LTX-2.3-22b.safetensors"],
                ["LTX 2.3"],
            ).entries[0],
        ).toMatchObject({
            architectureId: "ltx2",
            modelProfileId: "ltx-2.3",
        });
    });

    it("retries after an invalid response while retaining bootstrap support", async () => {
        const requestJson = jest
            .fn<VideoStagesHostBridge["requestJson"]>()
            .mockResolvedValueOnce({ architectures: [], models: [] })
            .mockResolvedValueOnce(dto);
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson,
            getModelCompatId: () => "ltxv2",
        });

        expect(await loadAuthoritativeArchitectureCatalog()).toBeNull();
        expect(
            buildArchitectureModelCatalog(["ltx-two.safetensors"], ["LTX 2"])
                .source,
        ).toBe("bootstrap");
        expect(await loadAuthoritativeArchitectureCatalog()).toEqual(dto);
        expect(requestJson).toHaveBeenCalledTimes(2);
    });

    it("clears a failed request so a later retry can become authoritative", async () => {
        const failure = new Error("catalog route unavailable");
        const requestJson = jest
            .fn<VideoStagesHostBridge["requestJson"]>()
            .mockRejectedValueOnce(failure)
            .mockResolvedValueOnce(dto);
        const warning = jest
            .spyOn(console, "warn")
            .mockImplementation(() => undefined);
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson,
        });

        expect(await loadAuthoritativeArchitectureCatalog()).toBeNull();

        expect(await loadAuthoritativeArchitectureCatalog()).toEqual(dto);
        expect(
            buildArchitectureModelCatalog(
                dto.models.map((model) => model.modelName),
                dto.models.map((model) => model.modelName),
            ).source,
        ).toBe("backend");
        expect(requestJson).toHaveBeenCalledTimes(2);
        expect(warning).toHaveBeenCalledWith(
            expect.stringContaining("architecture catalog unavailable"),
            failure,
        );
    });

    it("recognizes any cataloged text-to-video root architecture", async () => {
        const future = structuredClone(dto);
        future.architectures[0].id = "future-video";
        future.models[0].architectureId = "future-video";
        future.models[1].architectureId = "future-video";
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => future,
        });
        await loadAuthoritativeArchitectureCatalog();
        const input = document.createElement("input");
        input.id = "input_model";
        input.value = future.models[0].modelName;
        document.body.appendChild(input);

        expect(isRootTextToVideoModel()).toBe(true);
    });
});
