import {
    CONDITIONAL_RULE_CODES,
    isKnownConditionalRuleCode,
} from "./conditionalRules";
import { MAX_FRAME_GRID } from "./temporalGrid";
import type {
    ArchitectureCapabilities,
    ArchitectureCatalogEntryDto,
    CapabilityRuleDecision,
    CapabilityRuleScope,
    MinimumStageControlRuleConstraints,
    VideoArchitectureCatalogDto,
} from "./types";

const BOUNDARY_MODES = ["cut", "continue", "crossfade"] as const;
const ENTRY_MODES = ["text-to-video", "image-to-video", "init-video"] as const;
const ENTRY_ABILITIES = ["text", "image"] as const;
const REFERENCE_POSITIONS = ["first", "last", "any"] as const;

const isRecord = (value: unknown): value is Record<string, unknown> =>
    typeof value === "object" && value !== null && !Array.isArray(value);

const isTrimmedNonEmpty = (value: unknown): value is string =>
    typeof value === "string" && value.length > 0 && value === value.trim();

const isUniqueStringArray = (value: unknown): value is string[] =>
    Array.isArray(value) &&
    value.every((entry) => isTrimmedNonEmpty(entry)) &&
    new Set(value).size === value.length;

const isEntryModeArray = (value: unknown): value is string[] =>
    isUniqueStringArray(value) &&
    value.every((entry) => (ENTRY_MODES as readonly string[]).includes(entry));

const isEntryAbilityArray = (value: unknown): value is string[] =>
    isUniqueStringArray(value) &&
    value.every((entry) =>
        (ENTRY_ABILITIES as readonly string[]).includes(entry),
    );

const isReferencePositionArray = (value: unknown): value is string[] =>
    isUniqueStringArray(value) &&
    value.every((entry) =>
        (REFERENCE_POSITIONS as readonly string[]).includes(entry),
    );

const hasExactKeys = (
    value: Record<string, unknown>,
    expected: readonly string[],
): boolean =>
    Object.keys(value).length === expected.length &&
    expected.every((key) => Object.hasOwn(value, key));

const isRuleDecision = (
    value: unknown,
    allowedScopes?: readonly CapabilityRuleScope[],
): value is CapabilityRuleDecision => {
    if (
        !isRecord(value) ||
        !hasExactKeys(value, [
            "support",
            "code",
            "reason",
            "scope",
            "constraints",
        ]) ||
        typeof value.support !== "string" ||
        !["supported", "unsupported", "conditional"].includes(value.support) ||
        !isTrimmedNonEmpty(value.code) ||
        !isTrimmedNonEmpty(value.reason) ||
        typeof value.scope !== "string" ||
        !["clip", "stage", "boundary"].includes(value.scope) ||
        (value.constraints !== null && !isRecord(value.constraints))
    ) {
        return false;
    }
    const scope = value.scope as CapabilityRuleScope;
    if (allowedScopes && !allowedScopes.includes(scope)) {
        return false;
    }
    if (value.support === "unsupported" && value.constraints !== null) {
        return false;
    }
    return true;
};

const isKnownExecutableRule = (value: CapabilityRuleDecision): boolean => {
    if (value.support !== "conditional") {
        return false;
    }
    // A conditional rule whose whole meaning is its code publishes no thresholds.
    if (value.code === CONDITIONAL_RULE_CODES.promptRelayRequiresFixedLength) {
        return value.scope === "clip" && value.constraints === null;
    }
    if (!isRecord(value.constraints)) {
        return false;
    }
    const constraints = value.constraints;
    switch (value.code) {
        case CONDITIONAL_RULE_CODES.audioReuseRequiresStages:
            return (
                value.scope === "clip" &&
                hasExactKeys(constraints, ["minimumActiveStages"]) &&
                Number.isInteger(constraints.minimumActiveStages) &&
                Number(constraints.minimumActiveStages) > 0
            );
        case CONDITIONAL_RULE_CODES.normalLoraRequiresSamplingStage: {
            const typed =
                constraints as Partial<MinimumStageControlRuleConstraints>;
            return (
                value.scope === "stage" &&
                hasExactKeys(constraints, ["exclusiveMinimumControl"]) &&
                typeof typed.exclusiveMinimumControl === "number" &&
                Number.isFinite(typed.exclusiveMinimumControl)
            );
        }
        case CONDITIONAL_RULE_CODES.retakeExcludesReferences:
            return (
                value.scope === "stage" &&
                hasExactKeys(constraints, ["mutuallyExclusive"]) &&
                isUniqueStringArray(constraints.mutuallyExclusive) &&
                constraints.mutuallyExclusive.length === 2 &&
                constraints.mutuallyExclusive.includes("retake") &&
                constraints.mutuallyExclusive.includes("frameReferences")
            );
        case CONDITIONAL_RULE_CODES.retakeRequiresSource:
            return (
                value.scope === "clip" &&
                hasExactKeys(constraints, ["requiresAnyEntryMode"]) &&
                isEntryModeArray(constraints.requiresAnyEntryMode) &&
                constraints.requiresAnyEntryMode.length === 1 &&
                constraints.requiresAnyEntryMode.includes("init-video")
            );
    }
    return false;
};

const isBoundaryRule = (value: unknown): value is CapabilityRuleDecision => {
    if (!isRuleDecision(value, ["boundary"])) {
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
        !hasExactKeys(constraints, [
            "sameArchitecture",
            "frameStep",
            "minFrames",
            "maxFrames",
            "defaultFrames",
            "continuityExtraFrames",
            "targetRequiresGeneratedEntry",
            "targetRequiresStage",
            "targetDisallowsInitialReference",
        ]) ||
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
    value.every(
        (rule) =>
            isRuleDecision(rule, allowedScopes) &&
            isKnownConditionalRuleCode(rule.code) &&
            isKnownExecutableRule(rule),
    ) &&
    new Set(value.map((rule) => rule.code)).size === value.length;

const isCapabilities = (value: unknown): value is ArchitectureCapabilities => {
    if (
        !isRecord(value) ||
        !hasExactKeys(value, [
            "architecture",
            "clip",
            "stage",
            "upscaleModes",
            "entryModes",
            "audioSourceKinds",
        ])
    ) {
        return false;
    }
    return (
        [
            value.architecture,
            value.clip,
            value.stage,
            value.upscaleModes,
            value.audioSourceKinds,
        ].every(isUniqueStringArray) && isEntryModeArray(value.entryModes)
    );
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
        !hasExactKeys(value, ["schemaVersion", "architectures", "models"]) ||
        value.schemaVersion !== 2 ||
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
            !hasExactKeys(raw, [
                "id",
                "label",
                "capabilities",
                "boundaryRules",
                "rules",
            ]) ||
            !isTrimmedNonEmpty(raw.id) ||
            !isTrimmedNonEmpty(raw.label) ||
            !isCapabilities(raw.capabilities) ||
            !hasCompleteBoundaryRules(raw.boundaryRules) ||
            !isRuleArray(raw.rules, ["clip", "stage"])
        ) {
            return null;
        }
        const executableRuleCodes = [
            ...Object.values(raw.boundaryRules).map((rule) => rule.code),
            ...raw.rules.map((rule) => rule.code),
        ];
        if (
            architectureIds.has(raw.id) ||
            new Set(executableRuleCodes).size !== executableRuleCodes.length
        ) {
            return null;
        }
        architectureIds.add(raw.id);
        architectures.push({
            id: raw.id,
            label: raw.label,
            capabilities: structuredClone(raw.capabilities),
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
            !hasExactKeys(raw, [
                "modelName",
                "architectureId",
                "modelProfileId",
                "modelClassId",
                "compatibilityClassId",
                "frameGrid",
                "entryAbilities",
                "capabilities",
                "enhancements",
            ]) ||
            !isTrimmedNonEmpty(raw.modelName) ||
            !isTrimmedNonEmpty(raw.architectureId) ||
            !architectureIds.has(raw.architectureId) ||
            !isTrimmedNonEmpty(raw.modelProfileId) ||
            !isTrimmedNonEmpty(raw.modelClassId) ||
            !isTrimmedNonEmpty(raw.compatibilityClassId) ||
            !Number.isSafeInteger(raw.frameGrid) ||
            Number(raw.frameGrid) < 1 ||
            Number(raw.frameGrid) > MAX_FRAME_GRID ||
            !isCapabilities(raw.capabilities) ||
            !isEntryAbilityArray(raw.entryAbilities) ||
            raw.entryAbilities.length === 0 ||
            !isRecord(raw.enhancements) ||
            !hasExactKeys(raw.enhancements, ["referencePositions"]) ||
            !isReferencePositionArray(raw.enhancements.referencePositions)
        ) {
            return null;
        }
        if (modelNames.has(raw.modelName)) {
            return null;
        }
        modelNames.add(raw.modelName);
        models.push({
            modelName: raw.modelName,
            architectureId: raw.architectureId,
            modelProfileId: raw.modelProfileId,
            modelClassId: raw.modelClassId,
            compatibilityClassId: raw.compatibilityClassId,
            frameGrid: Number(raw.frameGrid),
            capabilities: structuredClone(raw.capabilities),
            entryAbilities: [...raw.entryAbilities],
            enhancements: {
                referencePositions: [
                    ...(raw.enhancements.referencePositions as string[]),
                ],
            },
        });
    }
    return { schemaVersion: 2, architectures, models };
};
