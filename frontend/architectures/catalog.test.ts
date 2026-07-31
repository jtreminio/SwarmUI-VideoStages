import { afterEach, describe, expect, it, jest } from "@jest/globals";
import { resetArchitectureCatalogForTests } from "../__test_helpers__/architectureCatalog";
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
    getArchitectureCatalogSnapshot,
    loadAuthoritativeArchitectureCatalog,
    parseVideoArchitectureCatalog,
    refreshAuthoritativeArchitectureCatalog,
    subscribeArchitectureCatalog,
} from "./catalog";
import { evaluateConditionalRule } from "./conditionalRules";
import { createCapabilityViewResolver } from "./policy";
import type { VideoArchitectureCatalogDto } from "./types";

const dto: VideoArchitectureCatalogDto = {
    schemaVersion: 2,
    architectures: structuredClone(testArchitectureCatalog().architectures),
    models: [
        {
            modelName: "ltx-two.safetensors",
            architectureId: "ltx2",
            modelProfileId: "ltx-2.3",
            modelClassId: "ltx-video",
            compatibilityClassId: "ltx-video",
            frameGrid: 8,
            capabilities:
                testArchitectureCatalog().architectures[0].capabilities,
            entryAbilities: ["text", "image"],
            enhancements: { referencePositions: ["any"] },
        },
        {
            modelName: "ltx-two-three.safetensors",
            architectureId: "ltx2",
            modelProfileId: "synthetic-profile",
            modelClassId: "ltx-image-video",
            compatibilityClassId: "ltx-video",
            frameGrid: 8,
            capabilities: {
                ...testArchitectureCatalog().architectures[0].capabilities,
                entryModes: ["image-to-video"],
            },
            entryAbilities: ["image"],
            enhancements: { referencePositions: ["first"] },
        },
    ],
};

const deferred = <T>() => {
    let resolve!: (value: T | PromiseLike<T>) => void;
    let reject!: (reason?: unknown) => void;
    const promise = new Promise<T>((resolvePromise, rejectPromise) => {
        resolve = resolvePromise;
        reject = rejectPromise;
    });
    return { promise, resolve, reject };
};

afterEach(() => {
    resetArchitectureCatalogForTests();
    setVideoStagesHostBridgeForTests(null);
    jest.restoreAllMocks();
    document.body.innerHTML = "";
});

describe("architecture catalog wire contract", () => {
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

    it("decodes complete model facts even when the stored profile hint is stale", () => {
        const current: VideoArchitectureCatalogDto = structuredClone(dto);
        current.models[0].modelProfileId = "removed-profile-alias";

        expect(parseVideoArchitectureCatalog(current)).toEqual(current);
    });

    it("requires the versioned exact shape and rejects retired aliases", () => {
        const oldVersion = structuredClone(dto) as unknown as Record<
            string,
            unknown
        >;
        oldVersion.schemaVersion = 1;
        expect(parseVideoArchitectureCatalog(oldVersion)).toBeNull();

        const retiredArchitectureAlias = structuredClone(dto) as unknown as {
            architectures: Array<Record<string, unknown>>;
        };
        retiredArchitectureAlias.architectures[0].profiles = [];
        expect(
            parseVideoArchitectureCatalog(retiredArchitectureAlias),
        ).toBeNull();

        const retiredModelAlias = structuredClone(dto) as unknown as {
            models: Array<Record<string, unknown>>;
        };
        retiredModelAlias.models[0].entryModes = ["text-to-video"];
        expect(parseVideoArchitectureCatalog(retiredModelAlias)).toBeNull();

        const retiredCapabilityAlias = structuredClone(dto) as unknown as {
            architectures: Array<{
                capabilities: Record<string, unknown>;
            }>;
        };
        retiredCapabilityAlias.architectures[0].capabilities.output = [
            "decoded-video",
        ];
        expect(
            parseVideoArchitectureCatalog(retiredCapabilityAlias),
        ).toBeNull();

        const retiredRuleAlias = structuredClone(dto) as unknown as {
            architectures: Array<{
                rules: Array<Record<string, unknown>>;
            }>;
        };
        retiredRuleAlias.architectures[0].rules[0].extras = {};
        expect(parseVideoArchitectureCatalog(retiredRuleAlias)).toBeNull();
    });

    it("requires host model identity, capability, and entry facts", () => {
        for (const key of [
            "modelClassId",
            "compatibilityClassId",
            "frameGrid",
            "capabilities",
            "entryAbilities",
            "enhancements",
        ] as const) {
            const missing = structuredClone(dto) as unknown as Record<
                string,
                unknown
            >;
            const models = missing.models as Record<string, unknown>[];
            delete models[0][key];
            expect(parseVideoArchitectureCatalog(missing)).toBeNull();
        }
        for (const invalid of [
            0,
            -1,
            1.5,
            "8",
            2_147_483_648,
            Number.MAX_SAFE_INTEGER + 1,
        ]) {
            const malformed = structuredClone(dto) as unknown as Record<
                string,
                unknown
            >;
            const models = malformed.models as Record<string, unknown>[];
            models[0].frameGrid = invalid;
            expect(parseVideoArchitectureCatalog(malformed)).toBeNull();
        }
        const unknownArchitecture = structuredClone(dto);
        unknownArchitecture.models[0].architectureId = "removed-architecture";
        expect(parseVideoArchitectureCatalog(unknownArchitecture)).toBeNull();
    });

    it("rejects duplicate architecture and model identities", () => {
        expect(
            parseVideoArchitectureCatalog({
                ...dto,
                architectures: [dto.architectures[0], dto.architectures[0]],
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
        ).not.toBeNull();
        expect(
            parseVideoArchitectureCatalog({
                ...dto,
                models: [dto.models[0], dto.models[0]],
            }),
        ).toBeNull();
    });

    it("rejects unknown effective entry modes", () => {
        const unknownOverview = structuredClone(dto);
        unknownOverview.architectures[0].capabilities.entryModes[0] =
            "future-video-mode";
        expect(parseVideoArchitectureCatalog(unknownOverview)).toBeNull();
    });

    it("accepts reordered or narrowed typed entry-mode capability facts", () => {
        const reordered = structuredClone(dto);
        reordered.architectures[0].capabilities.entryModes.reverse();
        expect(parseVideoArchitectureCatalog(reordered)).not.toBeNull();

        const missingOverviewMode = structuredClone(dto);
        missingOverviewMode.architectures[0].capabilities.entryModes =
            missingOverviewMode.architectures[0].capabilities.entryModes.filter(
                (mode) => mode !== "source-video",
            );
        expect(
            parseVideoArchitectureCatalog(missingOverviewMode),
        ).not.toBeNull();

        const narrowedModelMode = structuredClone(dto);
        narrowedModelMode.models[0].capabilities.entryModes = [
            "image-to-video",
        ];
        expect(parseVideoArchitectureCatalog(narrowedModelMode)).not.toBeNull();
    });

    it("requires the complete boundary/rule contract and lossless metadata", () => {
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

        const extraBoundaryConstraint = structuredClone(dto);
        const extraConstraints = extraBoundaryConstraint.architectures[0]
            .boundaryRules.continue.constraints as Record<string, unknown>;
        extraConstraints.unpublishedBehavior = true;
        expect(
            parseVideoArchitectureCatalog(extraBoundaryConstraint),
        ).toBeNull();

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

        const unknownExecutableRule = structuredClone(dto);
        unknownExecutableRule.architectures[0].rules[0].code =
            "future-rule-with-unknown-semantics";
        expect(parseVideoArchitectureCatalog(unknownExecutableRule)).toBeNull();

        const arbitraryBoundaryCode = structuredClone(dto);
        arbitraryBoundaryCode.architectures[0].boundaryRules.continue.code =
            "future-architecture.boundary.continue";
        expect(parseVideoArchitectureCatalog(arbitraryBoundaryCode)).toEqual(
            arbitraryBoundaryCode,
        );
    });

    it("rejects known executable rules with mismatched semantics", () => {
        const wrongScope = structuredClone(dto);
        wrongScope.architectures[0].rules[0].scope = "stage";
        expect(parseVideoArchitectureCatalog(wrongScope)).toBeNull();

        const extraConstraint = structuredClone(dto);
        const constraints = extraConstraint.architectures[0].rules[0]
            .constraints as Record<string, unknown>;
        constraints.unpublishedBehavior = true;
        expect(parseVideoArchitectureCatalog(extraConstraint)).toBeNull();
    });

    it("defensively treats unchecked unknown rules as violated", () => {
        expect(
            evaluateConditionalRule(
                {
                    support: "conditional",
                    code: "future-rule",
                    reason: "Unknown behavior",
                    scope: "clip",
                    constraints: {},
                },
                {},
            ),
        ).toBe(true);
    });

    it("uses exact authoritative boundary constraints", () => {
        const parsed = parseVideoArchitectureCatalog(dto);
        if (!parsed) throw new Error("catalog did not parse");
        const catalog = {
            source: "backend" as const,
            architectures: parsed.architectures,
            entries: parsed.models.map((model) => ({
                value: model.modelName,
                label: model.modelName,
                architectureId: model.architectureId,
                modelProfileId: model.modelProfileId,
                modelClassId: model.modelClassId,
                compatibilityClassId: model.compatibilityClassId,
                frameGrid: model.frameGrid,
                capabilities: structuredClone(model.capabilities),
                entryModes: [...model.capabilities.entryModes],
            })),
        };
        const stageModel = dto.models[0].modelName;
        const left = minimalClip({
            boundaryOut: "continue",
            stages: [
                {
                    ...minimalClip().stages[0],
                    model: stageModel,
                },
            ],
        });
        const right = minimalClip({
            sourceVideo: {
                data: "data:video/mp4;base64,AA==",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
            stages: [
                {
                    ...minimalClip().stages[0],
                    model: stageModel,
                },
            ],
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
});

describe("authoritative catalog repository", () => {
    it("notifies subscribers for request and settled state transitions", async () => {
        const response = deferred<unknown>();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: () => response.promise,
        });
        const statuses: string[] = [];
        const unsubscribe = subscribeArchitectureCatalog((snapshot) => {
            statuses.push(snapshot.status);
        });

        const request = loadAuthoritativeArchitectureCatalog();
        expect(statuses).toEqual(["loading"]);
        response.resolve(dto);
        await request;
        expect(statuses).toEqual(["loading", "ready"]);

        unsubscribe();
        await refreshAuthoritativeArchitectureCatalog();
        expect(statuses).toEqual(["loading", "ready"]);
    });

    it("moves loading to unavailable, then retry to ready without inferred identity", async () => {
        const first = deferred<unknown>();
        const second = deferred<unknown>();
        const failure = new Error("route unavailable");
        const requestJson = jest
            .fn<VideoStagesHostBridge["requestJson"]>()
            .mockImplementationOnce(() => first.promise)
            .mockImplementationOnce(() => second.promise);
        jest.spyOn(console, "warn").mockImplementation(() => undefined);
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson,
        });

        const initial = loadAuthoritativeArchitectureCatalog();
        expect(getArchitectureCatalogSnapshot()).toMatchObject({
            status: "loading",
            catalog: null,
        });
        first.reject(failure);
        await expect(initial).resolves.toBeNull();
        expect(getArchitectureCatalogSnapshot()).toMatchObject({
            status: "unavailable",
            catalog: null,
            error: "route unavailable",
        });
        expect(
            buildArchitectureModelCatalog(
                ["LTX-2.3-22b.safetensors"],
                ["LTX 2.3"],
            ),
        ).toMatchObject({
            source: "unavailable",
            architectures: [],
            entries: [
                {
                    architectureId: null,
                    modelProfileId: null,
                },
            ],
        });

        const retry = loadAuthoritativeArchitectureCatalog();
        expect(getArchitectureCatalogSnapshot().status).toBe("loading");
        second.resolve(dto);
        await expect(retry).resolves.toEqual(dto);
        expect(getArchitectureCatalogSnapshot()).toMatchObject({
            status: "ready",
            catalog: dto,
            error: null,
        });
    });

    it("retains the exact ready DTO through a failed refresh and later replaces it", async () => {
        const refreshFailure = deferred<unknown>();
        const replacement = structuredClone(dto);
        replacement.architectures[0].capabilities.stage =
            replacement.architectures[0].capabilities.stage.filter(
                (capability) => capability !== "ic-lora",
            );
        replacement.models = replacement.models.slice(0, 1);
        const requestJson = jest
            .fn<VideoStagesHostBridge["requestJson"]>()
            .mockResolvedValueOnce(dto)
            .mockImplementationOnce(() => refreshFailure.promise)
            .mockResolvedValueOnce(replacement);
        jest.spyOn(console, "warn").mockImplementation(() => undefined);
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson,
        });
        await loadAuthoritativeArchitectureCatalog();
        const oldCatalog = getArchitectureCatalogSnapshot().catalog;

        const refresh = refreshAuthoritativeArchitectureCatalog();
        expect(getArchitectureCatalogSnapshot()).toEqual({
            status: "refreshing",
            catalog: oldCatalog,
            error: null,
        });
        refreshFailure.reject(new Error("refresh failed"));
        await expect(refresh).resolves.toBeNull();
        expect(getArchitectureCatalogSnapshot()).toEqual({
            status: "stale",
            catalog: oldCatalog,
            error: "refresh failed",
        });

        await expect(
            refreshAuthoritativeArchitectureCatalog(),
        ).resolves.toEqual(replacement);
        expect(getArchitectureCatalogSnapshot()).toEqual({
            status: "ready",
            catalog: replacement,
            error: null,
        });
    });

    it("prevents a request from before a test reset from publishing late", async () => {
        const requestA = deferred<unknown>();
        const requestB = deferred<unknown>();
        const newer = structuredClone(dto);
        newer.models = newer.models.slice(0, 1);
        const requestJson = jest
            .fn<VideoStagesHostBridge["requestJson"]>()
            .mockImplementationOnce(() => requestA.promise)
            .mockImplementationOnce(() => requestB.promise);
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson,
        });

        const loadA = loadAuthoritativeArchitectureCatalog();
        resetArchitectureCatalogForTests();
        const loadB = loadAuthoritativeArchitectureCatalog();
        requestA.resolve(dto);
        await expect(loadA).resolves.toBeNull();
        expect(loadAuthoritativeArchitectureCatalog()).toBe(loadB);

        requestB.resolve(newer);
        await expect(loadB).resolves.toEqual(newer);
        expect(getArchitectureCatalogSnapshot()).toMatchObject({
            status: "ready",
            catalog: newer,
        });
        expect(requestJson).toHaveBeenCalledTimes(2);
    });

    it("coalesces loads while forced callers share one trailing refresh", async () => {
        const initial = deferred<unknown>();
        const trailing = deferred<unknown>();
        const replacement = structuredClone(dto);
        replacement.models = replacement.models.slice(0, 1);
        const requestJson = jest
            .fn<VideoStagesHostBridge["requestJson"]>()
            .mockImplementationOnce(() => initial.promise)
            .mockImplementationOnce(() => trailing.promise);
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson,
        });

        const firstLoad = loadAuthoritativeArchitectureCatalog();
        expect(loadAuthoritativeArchitectureCatalog()).toBe(firstLoad);
        const forcedRefresh = refreshAuthoritativeArchitectureCatalog();
        expect(forcedRefresh).not.toBe(firstLoad);
        expect(refreshAuthoritativeArchitectureCatalog()).toBe(forcedRefresh);

        initial.resolve(dto);
        await firstLoad;
        await Promise.resolve();
        expect(requestJson).toHaveBeenCalledTimes(2);
        expect(getArchitectureCatalogSnapshot()).toMatchObject({
            status: "refreshing",
            catalog: dto,
        });

        trailing.resolve(replacement);
        await expect(forcedRefresh).resolves.toEqual(replacement);

        expect(requestJson).toHaveBeenCalledTimes(2);
        expect(requestJson).toHaveBeenNthCalledWith(
            1,
            ARCHITECTURE_CATALOG_API,
        );
    });

    it("decorates backend-known model labels and retains backend-only entries without inferring host-only identity", async () => {
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => dto,
        });
        await loadAuthoritativeArchitectureCatalog();

        const catalog = buildArchitectureModelCatalog(
            [dto.models[0].modelName, "LTX-2.3-host-only.safetensors"],
            ["Current Host Label", "Looks Like LTX"],
        );

        expect(catalog.source).toBe("backend");
        expect(catalog.entries).toEqual([
            expect.objectContaining({
                value: dto.models[0].modelName,
                label: "Current Host Label",
                architectureId: "ltx2",
                modelProfileId: "ltx-2.3",
            }),
            expect.objectContaining({
                value: "LTX-2.3-host-only.safetensors",
                label: "Looks Like LTX",
                architectureId: null,
                modelProfileId: null,
            }),
            expect.objectContaining({
                value: dto.models[1].modelName,
                label: dto.models[1].modelName,
                architectureId: "ltx2",
                modelProfileId: "synthetic-profile",
            }),
        ]);
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

    it("uses authoritative entry abilities over the architecture overview", async () => {
        const current: VideoArchitectureCatalogDto = structuredClone(dto);
        current.models[0].entryAbilities = ["image"];
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => current,
        });
        await loadAuthoritativeArchitectureCatalog();
        const input = document.createElement("input");
        input.id = "input_model";
        input.value = current.models[0].modelName;
        document.body.appendChild(input);

        expect(isRootTextToVideoModel()).toBe(false);
    });

    it("classifies a root model by its authoritative entry abilities instead of the architecture union", async () => {
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => dto,
        });
        await loadAuthoritativeArchitectureCatalog();
        const input = document.createElement("input");
        input.id = "input_model";
        input.value = dto.models[1].modelName;
        document.body.appendChild(input);

        expect(dto.architectures[0].capabilities.entryModes).toContain(
            "text-to-video",
        );
        expect(dto.models[1].entryAbilities).not.toContain("text");
        expect(isRootTextToVideoModel()).toBe(false);
    });
});
