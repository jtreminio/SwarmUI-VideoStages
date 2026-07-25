import type {
    ArchitectureCapabilities,
    ArchitectureCatalogEntryDto,
    CapabilityRuleDecision,
    CapabilityRuleScope,
    VideoArchitectureCatalogDto,
} from "./types";

const BOUNDARY_MODES = ["cut", "continue", "crossfade"] as const;

const isRecord = (value: unknown): value is Record<string, unknown> =>
    typeof value === "object" && value !== null && !Array.isArray(value);

const isTrimmedNonEmpty = (value: unknown): value is string =>
    typeof value === "string" && value.length > 0 && value === value.trim();

const isUniqueStringArray = (value: unknown): value is string[] =>
    Array.isArray(value) &&
    value.every((entry) => isTrimmedNonEmpty(entry)) &&
    new Set(value).size === value.length;

const isRuleDecision = (
    value: unknown,
    allowedScopes?: readonly CapabilityRuleScope[],
): value is CapabilityRuleDecision => {
    if (
        !isRecord(value) ||
        !["supported", "unsupported", "conditional"].includes(
            `${value.support}`,
        ) ||
        !isTrimmedNonEmpty(value.code) ||
        !isTrimmedNonEmpty(value.reason) ||
        ![
            "architecture",
            "model-profile",
            "clip",
            "stage",
            "boundary",
            "output",
        ].includes(`${value.scope}`) ||
        (value.entityId !== null && !isTrimmedNonEmpty(value.entityId)) ||
        (value.constraints !== null && !isRecord(value.constraints))
    ) {
        return false;
    }
    const scope = value.scope as CapabilityRuleScope;
    if (allowedScopes && !allowedScopes.includes(scope)) {
        return false;
    }
    if (value.support === "conditional" && !isRecord(value.constraints)) {
        return false;
    }
    if (value.support === "unsupported" && value.constraints !== null) {
        return false;
    }
    return true;
};

const isBoundaryRule = (value: unknown): value is CapabilityRuleDecision => {
    if (!isRuleDecision(value, ["boundary"]) || value.entityId !== null) {
        return false;
    }
    if (value.support !== "conditional") {
        return value.constraints === null;
    }
    if (!isRecord(value.constraints)) {
        return false;
    }
    const constraints = value.constraints;
    const integers = [
        constraints.frameStep,
        constraints.minFrames,
        constraints.maxFrames,
        constraints.defaultFrames,
        constraints.continuityExtraFrames,
    ];
    if (
        constraints.sameArchitecture !== true ||
        typeof constraints.targetRequiresGeneratedEntry !== "boolean" ||
        typeof constraints.targetRequiresStage !== "boolean" ||
        typeof constraints.targetDisallowsInitialReference !== "boolean" ||
        !integers.every(Number.isInteger)
    ) {
        return false;
    }
    const frameStep = constraints.frameStep as number;
    const minFrames = constraints.minFrames as number;
    const maxFrames = constraints.maxFrames as number;
    const defaultFrames = constraints.defaultFrames as number;
    const continuityExtraFrames = constraints.continuityExtraFrames as number;
    return (
        frameStep > 0 &&
        minFrames >= 0 &&
        maxFrames >= minFrames &&
        defaultFrames >= minFrames &&
        defaultFrames <= maxFrames &&
        continuityExtraFrames >= 0 &&
        (defaultFrames - minFrames) % frameStep === 0
    );
};

const isRuleArray = (
    value: unknown,
    allowedScopes: readonly CapabilityRuleScope[],
): value is CapabilityRuleDecision[] =>
    Array.isArray(value) &&
    value.every((rule) => isRuleDecision(rule, allowedScopes)) &&
    new Set(value.map((rule) => rule.code)).size === value.length;

const isProfile = (
    value: unknown,
): value is ArchitectureCatalogEntryDto["profiles"][number] =>
    isRecord(value) &&
    isTrimmedNonEmpty(value.id) &&
    isTrimmedNonEmpty(value.label) &&
    isUniqueStringArray(value.capabilities) &&
    isRuleArray(value.rules, ["model-profile"]);

const isCapabilities = (value: unknown): value is ArchitectureCapabilities => {
    if (!isRecord(value)) {
        return false;
    }
    return [
        value.architecture,
        value.clip,
        value.stage,
        value.output,
        value.upscaleModes,
        value.entryModes,
        value.audioSourceKinds,
    ].every(isUniqueStringArray);
};

const hasCompleteBoundaryRules = (
    value: unknown,
): value is Record<string, CapabilityRuleDecision> => {
    if (!isRecord(value)) {
        return false;
    }
    const keys = Object.keys(value);
    return (
        keys.length === BOUNDARY_MODES.length &&
        BOUNDARY_MODES.every((mode) => isBoundaryRule(value[mode]))
    );
};

/**
 * All-or-nothing decoder for the backend-owned architecture catalog. Invalid
 * wire data never becomes a partially-authoritative frontend catalog.
 */
export const parseVideoArchitectureCatalog = (
    value: unknown,
): VideoArchitectureCatalogDto | null => {
    if (
        !isRecord(value) ||
        !Array.isArray(value.architectures) ||
        !Array.isArray(value.models)
    ) {
        return null;
    }
    const architectures: ArchitectureCatalogEntryDto[] = [];
    const architectureIds = new Set<string>();
    for (const raw of value.architectures) {
        if (
            !isRecord(raw) ||
            !isTrimmedNonEmpty(raw.id) ||
            !isTrimmedNonEmpty(raw.label) ||
            !isTrimmedNonEmpty(raw.defaultProfileId) ||
            !isCapabilities(raw.capabilities) ||
            !Array.isArray(raw.profiles) ||
            !raw.profiles.every(isProfile) ||
            !hasCompleteBoundaryRules(raw.boundaryRules) ||
            !isRuleArray(raw.rules, ["architecture", "clip", "stage", "output"])
        ) {
            return null;
        }
        const profileIds = raw.profiles.map((profile) => profile.id);
        const allCodes = [
            ...Object.values(raw.boundaryRules).map((rule) => rule.code),
            ...raw.rules.map((rule) => rule.code),
            ...raw.profiles.flatMap((profile) =>
                profile.rules.map((rule) => rule.code),
            ),
        ];
        if (
            architectureIds.has(raw.id) ||
            new Set(profileIds).size !== profileIds.length ||
            !profileIds.includes(raw.defaultProfileId) ||
            new Set(allCodes).size !== allCodes.length
        ) {
            return null;
        }
        architectureIds.add(raw.id);
        architectures.push({
            id: raw.id,
            label: raw.label,
            defaultProfileId: raw.defaultProfileId,
            capabilities: structuredClone(raw.capabilities),
            profiles: structuredClone(raw.profiles),
            boundaryRules: structuredClone(raw.boundaryRules),
            rules: structuredClone(raw.rules),
        });
    }
    if (architectures.length === 0) {
        return null;
    }
    const modelNames = new Set<string>();
    const models: VideoArchitectureCatalogDto["models"] = [];
    for (const raw of value.models) {
        if (
            !isRecord(raw) ||
            !isTrimmedNonEmpty(raw.modelName) ||
            !isTrimmedNonEmpty(raw.architectureId) ||
            !architectureIds.has(raw.architectureId) ||
            !isTrimmedNonEmpty(raw.modelProfileId) ||
            (raw.compatId !== undefined &&
                raw.compatId !== null &&
                typeof raw.compatId !== "string")
        ) {
            return null;
        }
        const descriptor = architectures.find(
            (architecture) => architecture.id === raw.architectureId,
        );
        if (
            modelNames.has(raw.modelName) ||
            !descriptor?.profiles.some(
                (profile) => profile.id === raw.modelProfileId,
            )
        ) {
            return null;
        }
        modelNames.add(raw.modelName);
        models.push({
            modelName: raw.modelName,
            architectureId: raw.architectureId,
            modelProfileId: raw.modelProfileId,
            compatId: raw.compatId ?? null,
        });
    }
    return { architectures, models };
};
