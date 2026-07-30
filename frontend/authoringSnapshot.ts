import { getArchitectureCatalogSnapshot } from "./architectures/catalog";
import {
    type CapabilityViewResolver,
    createCapabilityViewResolver,
} from "./architectures/policy";
import type { ArchitectureCatalogSnapshot } from "./architectures/types";
import { getRootDefaults } from "./rootDefaults";
import { getRootGeneratedEntryMode } from "./swarmInputs";
import type { RootDefaults } from "./types";

/**
 * The stable backend catalog and live host defaults used by one synchronous
 * authoring render/command. Capture again for the next user event; host inputs
 * intentionally remain live rather than becoming a global cache.
 */
export interface AuthoringTransactionSnapshot {
    catalogStatus: ArchitectureCatalogSnapshot;
    defaults: RootDefaults;
    capabilities: CapabilityViewResolver;
    generatedEntryMode: "text-to-video" | "image-to-video";
}

export const captureAuthoringTransactionSnapshot =
    (): AuthoringTransactionSnapshot => {
        const catalogStatus = getArchitectureCatalogSnapshot();
        const defaults = getRootDefaults(catalogStatus.catalog);
        return {
            catalogStatus,
            defaults,
            capabilities: createCapabilityViewResolver(defaults.modelCatalog),
            generatedEntryMode: getRootGeneratedEntryMode(
                defaults.modelCatalog,
            ),
        };
    };
