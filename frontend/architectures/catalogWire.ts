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
const ENTRY_MODES = [
    "text-to-video",
    "image-to-video",
    "source-video",
    "refine-video",
] as const;
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

const hasExactKeys = (
    value: Record<string, unknown>,
    expected: readonly string[],
): boolean =>
    Object.keys(value).length === expected.length &&
    expected.every((key) => Object.hasOwn(value, key));

const isKnownExecutableRule = (value: CapabilityRuleDecision): boolean => {
    if (
        value.support !== "conditional" ||
        value.entityId !== null ||
        !isRecord(value.constraints)
    ) {
        return false;
    }
    const constraints = value.constraints;
    switch (value.code) {
        case CONDITIONAL_RULE_CODES.audioReuseRequiresStages:
            return (
                value.scope === "clip" &&
                hasExactKeys(constraints, [
                    "minimumActiveStages",
                    "failureSeverity",
                    "failureEffect",
                ]) &&
                Number.isInteger(constraints.minimumActiveStages) &&
                Number(constraints.minimumActiveStages) > 0 &&
                constraints.failureSeverity === "warning" &&
                constraints.failureEffect === "disable-feature"
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
        case CONDITIONAL_RULE_CODES.promptRelayRequiresFixedLength:
            return (
                value.scope === "clip" &&
                hasExactKeys(constraints, ["requiresFixedFrameCount"]) &&
                constraints.requiresFixedFrameCount === true
            );
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
                constraints.requiresAnyEntryMode.length === 2 &&
                constraints.requiresAnyEntryMode.includes("source-video") &&
                constraints.requiresAnyEntryMode.includes("refine-video")
            );
        case CONDITIONAL_RULE_CODES.uniformTimelineHdr:
            return (
                value.scope === "architecture" &&
                hasExactKeys(constraints, [
                    "uniformTimelineFeature",
                    "minimumTimelineClips",
                ]) &&
                constraints.uniformTimelineFeature === "hdr" &&
                Number.isInteger(constraints.minimumTimelineClips) &&
                Number(constraints.minimumTimelineClips) >= 2
            );
    }
    return false;
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
    value.every(
        (rule) =>
            isRuleDecision(rule, allowedScopes) &&
            isKnownConditionalRuleCode(rule.code) &&
            isKnownExecutableRule(rule),
    ) &&
    new Set(value.map((rule) => rule.code)).size === value.length;

const isProfile = (
    value: unknown,
): value is ArchitectureCatalogEntryDto["profiles"][number] =>
    isRecord(value) &&
    isTrimmedNonEmpty(value.id) &&
    isTrimmedNonEmpty(value.label) &&
    isEntryModeArray(value.entryModes) &&
    value.entryModes.length > 0 &&
    isUniqueStringArray(value.capabilities) &&
    isRuleArray(value.rules, ["model-profile", "stage"]);

const isCapabilities = (value: unknown): value is ArchitectureCapabilities => {
    if (!isRecord(value)) {
        return false;
    }
    return (
        [
            value.architecture,
            value.clip,
            value.stage,
            value.output,
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
            (raw.extras !== undefined && !isUniqueStringArray(raw.extras)) ||
            !Array.isArray(raw.profiles) ||
            !raw.profiles.every(isProfile) ||
            !hasCompleteBoundaryRules(raw.boundaryRules) ||
            !isRuleArray(raw.rules, ["architecture", "clip", "stage", "output"])
        ) {
            return null;
        }
        const profileIds = raw.profiles.map((profile) => profile.id);
        const executableRuleCodes = [
            ...Object.values(raw.boundaryRules).map((rule) => rule.code),
            ...raw.rules.map((rule) => rule.code),
        ];
        if (
            architectureIds.has(raw.id) ||
            new Set(profileIds).size !== profileIds.length ||
            new Set(executableRuleCodes).size !== executableRuleCodes.length
        ) {
            return null;
        }
        architectureIds.add(raw.id);
        architectures.push({
            id: raw.id,
            label: raw.label,
            defaultProfileId: raw.defaultProfileId,
            ...(raw.extras === undefined ? {} : { extras: [...raw.extras] }),
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
            !isTrimmedNonEmpty(raw.modelClassId) ||
            !isTrimmedNonEmpty(raw.compatibilityClassId) ||
            !Number.isSafeInteger(raw.frameGrid) ||
            Number(raw.frameGrid) < 1 ||
            Number(raw.frameGrid) > MAX_FRAME_GRID ||
            !isEntryModeArray(raw.entryModes) ||
            raw.entryModes.length === 0 ||
            (raw.entryAbilities !== undefined &&
                (!isEntryAbilityArray(raw.entryAbilities) ||
                    raw.entryAbilities.length === 0)) ||
            (raw.enhancements !== undefined &&
                (!isRecord(raw.enhancements) ||
                    !isUniqueStringArray(raw.enhancements.extras) ||
                    !isReferencePositionArray(
                        raw.enhancements.referencePositions,
                    )))
        ) {
            return null;
        }
        if (modelNames.has(raw.modelName)) {
            return null;
        }
        modelNames.add(raw.modelName);
        const rawEnhancements = raw.enhancements as
            | { extras: string[]; referencePositions: string[] }
            | undefined;
        models.push({
            modelName: raw.modelName,
            architectureId: raw.architectureId,
            modelProfileId: raw.modelProfileId,
            modelClassId: raw.modelClassId,
            compatibilityClassId: raw.compatibilityClassId,
            frameGrid: Number(raw.frameGrid),
            ...(raw.entryAbilities === undefined
                ? {}
                : { entryAbilities: [...raw.entryAbilities] }),
            ...(rawEnhancements === undefined
                ? {}
                : {
                      enhancements: {
                          extras: [...rawEnhancements.extras],
                          referencePositions: [
                              ...rawEnhancements.referencePositions,
                          ],
                      },
                  }),
            entryModes: [...raw.entryModes],
        });
    }
    return { architectures, models };
};
