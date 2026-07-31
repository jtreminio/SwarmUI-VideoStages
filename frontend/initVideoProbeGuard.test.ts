import { afterEach, describe, expect, it } from "@jest/globals";
import { minimalClip } from "./__test_helpers__/clipFixtures";
import {
    beginInitVideoProbeOperation,
    findClipByStableId,
    resetInitVideoProbeOperationsForTests,
} from "./initVideoProbeGuard";

afterEach(() => resetInitVideoProbeOperationsForTests());

describe("init-video probe operation guard", () => {
    const revision = 7;

    it.each([
        "clip reorder",
        "clip deletion",
        "clip replacement",
        "unrelated document edit",
    ])("rejects after a revision change caused by %s", () => {
        const operation = beginInitVideoProbeOperation("clip-a", revision);
        expect(operation.claim(revision + 1)).toBe(false);
    });

    it("finds the intended clip by stable identity after array reorder", () => {
        const first = minimalClip({ id: "clip-a" });
        const second = minimalClip({ id: "clip-b" });
        expect(findClipByStableId([second, first], "clip-a")).toBe(first);
    });

    it("does not match a deleted or same-position replacement clip", () => {
        const replacement = minimalClip({ id: "clip-c" });
        expect(findClipByStableId([replacement], "clip-a")).toBeUndefined();
    });

    it("rejects a cancelled probe", () => {
        const operation = beginInitVideoProbeOperation("clip-a", revision);
        operation.cancel();
        expect(operation.claim(revision)).toBe(false);
    });

    it("lets only the latest overlapping pick for one clip commit", () => {
        const first = beginInitVideoProbeOperation("clip-a", revision);
        const second = beginInitVideoProbeOperation("clip-a", revision);
        expect(first.claim(revision)).toBe(false);
        expect(second.claim(revision)).toBe(true);
    });

    it("keeps concurrent picks for different clips independent", () => {
        const first = beginInitVideoProbeOperation("clip-a", revision);
        const second = beginInitVideoProbeOperation("clip-b", revision);
        expect(first.claim(revision)).toBe(true);
        expect(second.claim(revision)).toBe(true);
    });
});
