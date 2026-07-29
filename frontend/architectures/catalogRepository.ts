import { getVideoStagesHostBridge } from "../host";
import { parseVideoArchitectureCatalog } from "./catalogWire";
import type {
    ArchitectureCatalogSnapshot,
    ArchitectureModelCatalog,
    VideoArchitectureCatalogDto,
} from "./types";

export const ARCHITECTURE_CATALOG_API = "VideoStagesGetArchitectureCatalog";

interface CatalogRequest {
    generation: number;
    promise: Promise<VideoArchitectureCatalogDto | null>;
}

interface TrailingCatalogRequest {
    after: CatalogRequest;
    promise: Promise<VideoArchitectureCatalogDto | null>;
}

let authoritativeCatalog: VideoArchitectureCatalogDto | null = null;
let snapshotStatus: ArchitectureCatalogSnapshot["status"] = "loading";
let snapshotError: string | null = null;
let requestGeneration = 0;
let activeRequest: CatalogRequest | null = null;
let trailingRequest: TrailingCatalogRequest | null = null;

const cloneCatalog = (
    catalog: VideoArchitectureCatalogDto,
): VideoArchitectureCatalogDto => structuredClone(catalog);

const errorMessage = (error: unknown): string =>
    error instanceof Error ? error.message : `${error}`;

export const getArchitectureCatalogSnapshot =
    (): ArchitectureCatalogSnapshot => ({
        status: snapshotStatus,
        catalog: authoritativeCatalog
            ? cloneCatalog(authoritativeCatalog)
            : null,
        error: snapshotError,
    });

const requestAuthoritativeCatalog =
    (): Promise<VideoArchitectureCatalogDto | null> => {
        if (activeRequest) {
            return activeRequest.promise;
        }

        const generation = ++requestGeneration;
        snapshotStatus = authoritativeCatalog ? "refreshing" : "loading";
        snapshotError = null;

        let request!: CatalogRequest;
        const ownsRequest = (): boolean =>
            activeRequest === request && requestGeneration === generation;
        const promise = Promise.resolve()
            .then(() =>
                getVideoStagesHostBridge().requestJson(
                    ARCHITECTURE_CATALOG_API,
                ),
            )
            .then((response) => {
                const parsed = parseVideoArchitectureCatalog(response);
                if (!parsed) {
                    throw new Error(
                        "The architecture catalog response was malformed.",
                    );
                }
                if (!ownsRequest()) {
                    return null;
                }
                authoritativeCatalog = parsed;
                snapshotStatus = "ready";
                snapshotError = null;
                return cloneCatalog(parsed);
            })
            .catch((error: unknown) => {
                if (!ownsRequest()) {
                    return null;
                }
                snapshotStatus = authoritativeCatalog ? "stale" : "unavailable";
                snapshotError = errorMessage(error);
                console.warn(
                    "VideoStages: authoritative architecture catalog unavailable",
                    error,
                );
                return null;
            })
            .finally(() => {
                if (ownsRequest()) {
                    activeRequest = null;
                }
            });
        request = { generation, promise };
        activeRequest = request;
        return promise;
    };

/**
 * Loads the initial authoritative catalog. A retained ready/stale catalog is
 * returned without a request; unavailable initial state can always retry.
 */
export const loadAuthoritativeArchitectureCatalog =
    (): Promise<VideoArchitectureCatalogDto | null> => {
        if (activeRequest) {
            return activeRequest.promise;
        }
        if (authoritativeCatalog) {
            return Promise.resolve(cloneCatalog(authoritativeCatalog));
        }
        return requestAuthoritativeCatalog();
    };

/**
 * Re-requests the backend catalog without dropping the last-known-good DTO.
 * Forced calls during one active generation share a fresh trailing request.
 */
export const refreshAuthoritativeArchitectureCatalog =
    (): Promise<VideoArchitectureCatalogDto | null> => {
        if (!activeRequest) {
            return requestAuthoritativeCatalog();
        }
        if (trailingRequest?.after === activeRequest) {
            return trailingRequest.promise;
        }

        const precedingRequest = activeRequest;
        let request!: TrailingCatalogRequest;
        const promise = precedingRequest.promise
            .then(() => {
                if (
                    trailingRequest !== request ||
                    requestGeneration !== precedingRequest.generation
                ) {
                    return null;
                }
                // The trailing request is now the active generation. Clear its
                // slot so a later forced signal can schedule after this one.
                trailingRequest = null;
                return requestAuthoritativeCatalog();
            })
            .finally(() => {
                if (trailingRequest === request) {
                    trailingRequest = null;
                }
            });
        request = { after: precedingRequest, promise };
        trailingRequest = request;
        return promise;
    };

/**
 * Test/process reset and explicit supersession boundary. Older requests may
 * still settle, but generation ownership prevents them from publishing state
 * or clearing a newer request handle.
 */
export const invalidateArchitectureCatalog = (): void => {
    requestGeneration++;
    activeRequest = null;
    trailingRequest = null;
    authoritativeCatalog = null;
    snapshotStatus = "loading";
    snapshotError = null;
};

export const buildArchitectureModelCatalog = (
    values: readonly string[],
    labels: readonly string[],
): ArchitectureModelCatalog => {
    const backend = authoritativeCatalog;
    const hostLabels = new Map<string, string>();
    const modelNames: string[] = [];
    const seen = new Set<string>();
    values.forEach((value, index) => {
        if (!seen.has(value)) {
            seen.add(value);
            modelNames.push(value);
        }
        hostLabels.set(value, labels[index] ?? value);
    });
    for (const model of backend?.models ?? []) {
        if (!seen.has(model.modelName)) {
            seen.add(model.modelName);
            modelNames.push(model.modelName);
        }
    }

    const backendModels = new Map(
        backend?.models.map((model) => [model.modelName, model]) ?? [],
    );
    return {
        architectures: backend ? structuredClone(backend.architectures) : [],
        source: backend ? "backend" : "unavailable",
        entries: modelNames.map((value) => {
            const backendModel = backendModels.get(value);
            return {
                value,
                label: hostLabels.get(value) ?? value,
                architectureId: backendModel?.architectureId ?? null,
                modelProfileId: backendModel?.modelProfileId ?? null,
            };
        }),
    };
};
