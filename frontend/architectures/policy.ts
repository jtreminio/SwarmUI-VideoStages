import { executableClipIndexes } from "../clipSemantics";
import { createBoundaryCapabilityViews } from "./policy/boundaryPolicy";
import { createClipStageCapabilityViews } from "./policy/clipStageViews";
import type {
    CapabilityRuleScopeContext,
    CapabilityViewResolver,
} from "./policy/types";
import type { ArchitectureModelCatalog } from "./types";

export type { FeatureSupportScope } from "./policy/clipStageViews";
export { architectureFeatureSupport } from "./policy/clipStageViews";
export {
    isAudioSourceSupported,
    upscaleModeForMethod,
} from "./policy/featureValues";
export { reconcileSourcedClipIdentity } from "./policy/identity";
export type {
    AuthoringFeature,
    AuthoringState,
    BoundaryCapabilityView,
    CapabilityDecision,
    CapabilityRuleScopeContext,
    CapabilityViewResolver,
    ClipCapabilityView,
    StageCapabilityView,
} from "./policy/types";

/** Composes catalog-backed clip, stage, and boundary policy views. */
export const createCapabilityViewResolver = (
    catalog: ArchitectureModelCatalog,
    scope: CapabilityRuleScopeContext = {},
): CapabilityViewResolver => {
    const architectureById = new Map(
        catalog.architectures.map((entry) => [entry.id, entry]),
    );
    const clipStage = createClipStageCapabilityViews(architectureById, scope);
    const boundaries = createBoundaryCapabilityViews(
        architectureById,
        clipStage.forClip,
    );
    return {
        catalog,
        ...clipStage,
        ...boundaries,
        executableClipIndexes,
    };
};
