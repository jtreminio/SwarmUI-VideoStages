import { afterEach, describe, expect, it } from "@jest/globals";
import { minimalClip } from "./__test_helpers__/clipFixtures";
import {
    beginClipMediaProbe,
    findClipByStableId,
    INIT_VIDEO_PROBE_SLOT,
    resetClipMediaProbesForTests,
} from "./clipMediaProbeGuard";

afterEach(() => resetClipMediaProbesForTests());

describe("clip media probe operation guard", () => {
    const revision = 7;
    const begin = (clipId: string, slot = INIT_VIDEO_PROBE_SLOT) =>
        beginClipMediaProbe(clipId, slot, revision);

    it.each([
        "clip reorder",
        "clip deletion",
        "clip replacement",
        "unrelated document edit",
    ])("rejects after a revision change caused by %s", () => {
        expect(begin("clip-a").claim(revision + 1)).toBe(false);
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
        const operation = begin("clip-a");
        operation.cancel();
        expect(operation.claim(revision)).toBe(false);
    });

    it("lets only the latest overlapping pick for one slot commit", () => {
        const first = begin("clip-a");
        const second = begin("clip-a");
        expect(first.claim(revision)).toBe(false);
        expect(second.claim(revision)).toBe(true);
    });

    it("keeps concurrent picks for different clips independent", () => {
        const first = begin("clip-a");
        const second = begin("clip-b");
        expect(first.claim(revision)).toBe(true);
        expect(second.claim(revision)).toBe(true);
    });

    // A clip has one source video but up to fifteen references, so two picks in
    // flight on one clip is ordinary rather than exotic.
    it("keeps concurrent picks for different slots on one clip independent", () => {
        const source = begin("clip-a");
        const reference = begin("clip-a", "reference-0");
        expect(source.claim(revision)).toBe(true);
        expect(reference.claim(revision)).toBe(true);
    });
});
