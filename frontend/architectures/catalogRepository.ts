import { getVideoStagesHostBridge } from "../host";
import { parseVideoArchitectureCatalog } from "./catalogWire";
import type {
    ArchitectureCatalogSnapshot,
    ArchitectureModelCatalog,
    VideoArchitectureCatalogDto,
} from "./types";

export const ARCHITECTURE_CATALOG_API = "VideoStagesGetArchitectureCatalog";

type CatalogRequest = Promise<VideoArchitectureCatalogDto | null>;

let authoritativeCatalog: VideoArchitectureCatalogDto | null = null;
let snapshotStatus: ArchitectureCatalogSnapshot["status"] = "loading";
let snapshotError: string | null = null;
/** Bumped by the test reset so an in-flight request cannot write state afterwards. */
let requestGeneration = 0;
let activeRequest: CatalogRequest | null = null;
/** The single request coalescing every forced refresh raised during `activeRequest`. */
let pendingRefresh: CatalogRequest | null = null;
let onRequestStarted: (() => void) | null = null;

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

/**
 * Notified the moment a request starts and moves the snapshot to
 * `loading`/`refreshing`, including a queued refresh starting later. Views paint
 * the transition they are showing instead of guessing when one will happen.
 */
export const setArchitectureCatalogRequestListener = (
    listener: (() => void) | null,
): void => {
    onRequestStarted = listener;
};

const requestAuthoritativeCatalog = (): CatalogRequest => {
    if (activeRequest) {
        return activeRequest;
    }

    const generation = ++requestGeneration;
    const owned = (): boolean => requestGeneration === generation;
    snapshotStatus = authoritativeCatalog ? "refreshing" : "loading";
    snapshotError = null;

    const request = Promise.resolve()
        .then(() =>
            getVideoStagesHostBridge().requestJson(ARCHITECTURE_CATALOG_API),
        )
        .then((response) => {
            const parsed = parseVideoArchitectureCatalog(response);
            if (!parsed) {
                throw new Error(
                    "The architecture catalog response was malformed.",
                );
            }
            if (!owned()) {
                return null;
            }
            authoritativeCatalog = parsed;
            snapshotStatus = "ready";
            snapshotError = null;
            return cloneCatalog(parsed);
        })
        .catch((error: unknown) => {
            if (!owned()) {
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
            if (owned()) {
                activeRequest = null;
            }
        });
    activeRequest = request;
    onRequestStarted?.();
    return request;
};

/**
 * Loads the initial authoritative catalog. A retained ready/stale catalog is
 * returned without a request; unavailable initial state can always retry.
 */
export const loadAuthoritativeArchitectureCatalog = (): CatalogRequest => {
    if (activeRequest) {
        return activeRequest;
    }
    if (authoritativeCatalog) {
        return Promise.resolve(cloneCatalog(authoritativeCatalog));
    }
    return requestAuthoritativeCatalog();
};

/**
 * Re-requests the backend catalog without dropping the last-known-good DTO.
 * Forced calls raised while a request is in flight share one request that starts
 * after it, so the signal is never consumed by the older response.
 */
export const refreshAuthoritativeArchitectureCatalog = (): CatalogRequest => {
    if (!activeRequest) {
        return requestAuthoritativeCatalog();
    }
    if (pendingRefresh) {
        return pendingRefresh;
    }
    const generation = requestGeneration;
    const refresh: CatalogRequest = activeRequest.then(() => {
        // Always vacate the slot, including when abandoning: a settled promise
        // left there would answer later refresh signals without requesting.
        if (pendingRefresh === refresh) {
            pendingRefresh = null;
        }
        // A reset, or a request started between the settle and this callback,
        // already supersedes this refresh.
        return requestGeneration === generation
            ? requestAuthoritativeCatalog()
            : null;
    });
    pendingRefresh = refresh;
    return refresh;
};

/** Test-only reset for this module's process-wide singleton state. */
export const resetArchitectureCatalogForTests = (): void => {
    requestGeneration++;
    activeRequest = null;
    pendingRefresh = null;
    authoritativeCatalog = null;
    snapshotStatus = "loading";
    snapshotError = null;
    onRequestStarted = null;
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
