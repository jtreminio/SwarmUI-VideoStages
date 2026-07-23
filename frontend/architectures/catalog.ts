/**
 * Stable catalog facade. Implementation is split by responsibility while
 * existing callers keep one public import surface.
 */

export {
    architectureCatalogView,
    architectureForModel,
    buildArchitectureRetargetPlan,
    modelProfileForModel,
    supportedArchitectureCatalog,
} from "./catalogQueries";
export {
    __resetArchitectureCatalogForTests,
    ARCHITECTURE_CATALOG_API,
    buildArchitectureModelCatalog,
    loadAuthoritativeArchitectureCatalog,
} from "./catalogRepository";
export { parseVideoArchitectureCatalog } from "./catalogWire";
