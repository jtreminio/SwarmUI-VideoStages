import type { ArchitectureBehavior } from "../behaviorRegistry";
import { reconcileIncomingIcLoraDrives } from "./icLoraDriveAvailability";
import * as icLoraNormalization from "./icLoraNormalization";
import { icLoraDisplayName } from "./icLoraPresets";

export const ltx2Behavior: ArchitectureBehavior = {
    normalizeIcLoras: icLoraNormalization.normalizeIcLoras,
    canonicalizeIcLoraFields: icLoraNormalization.canonicalizeIcLoraFields,
    reconcileIncomingIcLoraDrives,
    hasSlotSourcedIcLora: icLoraNormalization.hasSlotSourcedIcLora,
    isHdrFeature: icLoraNormalization.isHdrFeature,
    icLoraDisplayName,
};
