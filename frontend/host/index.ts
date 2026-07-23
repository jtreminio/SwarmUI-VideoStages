import { createDefaultLtxHostBridge } from "./defaultLtxHostBridge";
import type { LtxHostBridge } from "./LtxHostBridge";

let bridge: LtxHostBridge = createDefaultLtxHostBridge();

export const getLtxHostBridge = (): LtxHostBridge => bridge;

/** Test injection seam; pass null to restore the production bridge. */
export const setLtxHostBridgeForTests = (
    replacement: LtxHostBridge | null,
): void => {
    bridge = replacement ?? createDefaultLtxHostBridge();
};

export type {
    HostOptionList,
    HostRegistrySnapshot,
    LtxHostBridge,
    PromptPrefixExamples,
} from "./LtxHostBridge";
