import { getVideoStagesHostBridge } from "../host";
import { parseVideoArchitectureCatalog } from "./catalogWire";
import { videoArchitectureRegistry } from "./registry";
import type {
    ArchitectureCatalogEntryDto,
    ArchitectureModelCatalog,
    ArchitectureRegistry,
    VideoArchitectureCatalogDto,
} from "./types";

export const ARCHITECTURE_CATALOG_API = "VideoStagesGetArchitectureCatalog";

let authoritativeCatalog: VideoArchitectureCatalogDto | null = null;
let catalogRequest: Promise<VideoArchitectureCatalogDto | null> | null = null;

export const loadAuthoritativeArchitectureCatalog =
    (): Promise<VideoArchitectureCatalogDto | null> => {
        if (authoritativeCatalog) {
            return Promise.resolve(structuredClone(authoritativeCatalog));
        }
        if (catalogRequest) {
            return catalogRequest;
        }
        catalogRequest = getVideoStagesHostBridge()
            .requestJson(ARCHITECTURE_CATALOG_API)
            .then((response) => {
                const parsed = parseVideoArchitectureCatalog(response);
                if (parsed) {
                    authoritativeCatalog = parsed;
                }
                return parsed ? structuredClone(parsed) : null;
            })
            .catch((error) => {
                console.warn(
                    "VideoStages: architecture catalog unavailable; using registered frontend bootstrap",
                    error,
                );
                return null;
            })
            .finally(() => {
                catalogRequest = null;
            });
        return catalogRequest;
    };

/**
 * Drops the process-lifetime catalog cache. The host installs models while the
 * page lives, so a model refresh must be able to force a re-request instead of
 * resolving new models to a null architecture until reload.
 */
export const invalidateArchitectureCatalog = (): void => {
    authoritativeCatalog = null;
    catalogRequest = null;
};

const bootstrapArchitectures = (
    registry: ArchitectureRegistry,
): ArchitectureCatalogEntryDto[] =>
    registry
        .definitions()
        .map(
            ({
                id,
                label,
                defaultProfileId,
                capabilities,
                profiles,
                boundaryRules,
                rules,
            }) => ({
                id,
                label,
                defaultProfileId,
                capabilities: structuredClone(capabilities),
                profiles: structuredClone(profiles),
                boundaryRules: structuredClone(boundaryRules),
                rules: structuredClone(rules),
            }),
        );

export const buildArchitectureModelCatalog = (
    values: readonly string[],
    labels: readonly string[],
    registry: ArchitectureRegistry = videoArchitectureRegistry,
): ArchitectureModelCatalog => {
    const backend = authoritativeCatalog;
    // The host dropdown is a useful ordered view, but it is not guaranteed to
    // exist yet when the timeline first renders. The backend catalog already
    // contains every installed model that resolved through a production
    // architecture module, so retain those backend-only models as valid
    // authoring choices instead of dropping them during startup.
    const modelNames = [...values];
    if (backend) {
        const seen = new Set(modelNames);
        for (const model of backend.models) {
            if (!seen.has(model.modelName)) {
                seen.add(model.modelName);
                modelNames.push(model.modelName);
            }
        }
    }
    return {
        architectures:
            backend?.architectures ?? bootstrapArchitectures(registry),
        source: backend ? "backend" : "bootstrap",
        entries: modelNames.map((value, index) => {
            const backendModel = backend?.models.find(
                (model) => model.modelName === value,
            );
            const descriptor = {
                value,
                label: labels[index] ?? value,
                compatId:
                    backendModel?.compatId ??
                    getVideoStagesHostBridge().getModelCompatId(value),
                modelClassId: getVideoStagesHostBridge().getModelClassId(value),
            };
            const bootstrap = backend
                ? null
                : registry.resolveModel(descriptor);
            return {
                ...descriptor,
                architectureId:
                    backendModel?.architectureId ??
                    bootstrap?.definition.id ??
                    null,
                modelProfileId:
                    backendModel?.modelProfileId ??
                    bootstrap?.profileId ??
                    null,
            };
        }),
    };
};
