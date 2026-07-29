/**
 * Stable catalog facade. Implementation is split by responsibility while
 * existing callers keep one public import surface.
 */

export {
    architectureCatalogView,
    architectureDescriptor,
    architectureForModel,
    buildArchitectureRetargetPlan,
    modelCatalogEntry,
    modelProfileForModel,
    supportedArchitectureCatalog,
} from "./catalogQueries";
export {
    ARCHITECTURE_CATALOG_API,
    buildArchitectureModelCatalog,
    getArchitectureCatalogSnapshot,
    invalidateArchitectureCatalog,
    loadAuthoritativeArchitectureCatalog,
    refreshAuthoritativeArchitectureCatalog,
} from "./catalogRepository";
export { parseVideoArchitectureCatalog } from "./catalogWire";
