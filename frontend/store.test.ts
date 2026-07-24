import { beforeEach, describe, expect, it, jest } from "@jest/globals";
import {
    createTimelineStore,
    type StoreDeps,
    type TimelineStore,
    type UpdateMeta,
} from "./store";
import type { VideoStagesConfig } from "./types";

/**
 * Fake carrier: `data` stands in for the data param, `prompt` for the prompt
 * box, `dims` for the inherited-dims signature. parse() is a JSON round-trip
 * that stamps the current prompt onto the config, mimicking the prompt
 * overlay persistence.ts performs.
 */
interface FakeCarrier {
    data: string;
    prompt: string;
    dims: string;
}

const emptyConfig = (): VideoStagesConfig => ({
    width: 1024,
    height: 1024,
    fps: 24,
    dimsExplicit: false,
    clips: [],
});

interface Harness {
    store: TimelineStore;
    carrier: FakeCarrier;
    deps: StoreDeps;
    parseCalls: string[];
    writeQuietCalls: VideoStagesConfig[];
    notifyHostCalls: number[];
    onNotifyHost: (() => void) | null;
}

const makeHarness = (): Harness => {
    const carrier: FakeCarrier = { data: "", prompt: "", dims: "w|h|f" };
    const harness = {} as Harness;
    const parseCalls: string[] = [];
    const writeQuietCalls: VideoStagesConfig[] = [];
    const notifyHostCalls: number[] = [];
    const deps: StoreDeps = {
        readToken: () =>
            `${carrier.data}\x00${carrier.prompt}\x00${carrier.dims}`,
        readDataParam: () => carrier.data,
        parse: (serialized: string): VideoStagesConfig | null => {
            parseCalls.push(serialized);
            try {
                const parsed = JSON.parse(
                    serialized,
                ) as Partial<VideoStagesConfig>;
                if (typeof parsed !== "object" || parsed === null) {
                    return null;
                }
                const config = { ...emptyConfig(), ...parsed };
                config.clips = (config.clips ?? []).map((clip) => ({
                    ...clip,
                    prompt: carrier.prompt,
                }));
                return config;
            } catch {
                return null;
            }
        },
        parseEmpty: emptyConfig,
        serialize: (state: VideoStagesConfig): string =>
            JSON.stringify({
                width: state.width,
                clips: state.clips.map((clip) => ({
                    duration: clip.duration,
                })),
            }),
        writeQuiet: (state: VideoStagesConfig, serialized: string): void => {
            writeQuietCalls.push(structuredClone(state));
            carrier.data = serialized;
            carrier.prompt = state.clips.map((clip) => clip.prompt).join("|");
        },
        notifyHost: (): void => {
            notifyHostCalls.push(notifyHostCalls.length + 1);
            harness.onNotifyHost?.();
        },
    };
    harness.store = createTimelineStore(deps);
    harness.carrier = carrier;
    harness.deps = deps;
    harness.parseCalls = parseCalls;
    harness.writeQuietCalls = writeQuietCalls;
    harness.notifyHostCalls = notifyHostCalls;
    harness.onNotifyHost = null;
    return harness;
};

describe("createTimelineStore", () => {
    let h: Harness;

    beforeEach(() => {
        h = makeHarness();
    });

    describe("getState", () => {
        it("parses once and serves cache hits while the token is stable", () => {
            h.carrier.data = '{"width":640,"clips":[]}';
            const first = h.store.getState();
            const second = h.store.getState();
            expect(first.width).toBe(640);
            expect(second.width).toBe(640);
            expect(h.parseCalls).toEqual(['{"width":640,"clips":[]}']);
        });

        it("re-parses when the data param changes", () => {
            h.carrier.data = '{"width":640,"clips":[]}';
            h.store.getState();
            h.carrier.data = '{"width":512,"clips":[]}';
            expect(h.store.getState().width).toBe(512);
            expect(h.parseCalls).toHaveLength(2);
        });

        it("re-parses when only the prompt text changes", () => {
            h.carrier.data = '{"width":640,"clips":[{"duration":2}]}';
            h.carrier.prompt = "old";
            expect(h.store.getState().clips[0].prompt).toBe("old");
            h.carrier.prompt = "new";
            expect(h.store.getState().clips[0].prompt).toBe("new");
        });

        it("re-parses when only the dims signature changes", () => {
            h.carrier.data = '{"width":640,"clips":[]}';
            h.store.getState();
            h.carrier.dims = "other|h|f";
            h.store.getState();
            expect(h.parseCalls).toHaveLength(2);
        });

        it("returns isolated clones: caller mutations never leak back", () => {
            h.carrier.data = '{"width":640,"clips":[{"duration":2}]}';
            const copy = h.store.getState();
            copy.width = 1;
            copy.clips[0].duration = 99;
            const fresh = h.store.getState();
            expect(fresh.width).toBe(640);
            expect(fresh.clips[0].duration).toBe(2);
        });

        it("returns the empty config when both carrier and fallback are empty", () => {
            expect(h.store.getState()).toEqual(emptyConfig());
        });

        it("falls back to the last good serialized state on corrupt data", () => {
            h.carrier.data = '{"width":640,"clips":[]}';
            h.store.getState();
            h.carrier.data = "{corrupt";
            expect(h.store.getState().width).toBe(640);
        });

        it("falls back to empty config when corrupt with no last good state", () => {
            h.carrier.data = "{corrupt";
            expect(h.store.getState()).toEqual(emptyConfig());
        });

        it("invalidate() forces the next read to re-parse", () => {
            h.carrier.data = '{"width":640,"clips":[]}';
            h.store.getState();
            h.store.invalidate();
            h.store.getState();
            expect(h.parseCalls).toHaveLength(2);
        });

        it("returns an atomic isolated snapshot with a monotonic revision", () => {
            h.carrier.data = '{"width":640,"clips":[]}';
            const first = h.store.getSnapshot();
            const cached = h.store.getSnapshot();
            expect(first.revision).toBeGreaterThan(0);
            expect(cached.revision).toBe(first.revision);

            first.state.width = 1;
            expect(h.store.getState().width).toBe(640);

            h.carrier.data = '{"width":512,"clips":[]}';
            const changed = h.store.getSnapshot();
            expect(changed.state.width).toBe(512);
            expect(changed.revision).toBe(first.revision + 1);
            expect(h.store.revision()).toBe(changed.revision);
        });
    });

    describe("dispatch", () => {
        it("atomically applies a command with one write and one host notification", () => {
            const snapshot = h.store.getSnapshot();
            const result = h.store.dispatch(
                {
                    type: "root.patch",
                    patch: { width: 640, dimsExplicit: true },
                },
                "timeline",
                true,
                snapshot.revision,
            );

            expect(result).toEqual({
                applied: true,
                revision: snapshot.revision + 1,
                impacts: ["value", "capabilities"],
            });
            expect(h.writeQuietCalls).toHaveLength(1);
            expect(h.notifyHostCalls).toHaveLength(1);
            expect(h.store.getState().width).toBe(640);
        });

        it("rejects an unparseable serialized candidate before mutating carriers", () => {
            const before = h.store.getSnapshot();
            jest.spyOn(h.deps, "serialize").mockReturnValue("{malformed");

            const result = h.store.dispatch(
                {
                    type: "root.patch",
                    patch: { width: 640, dimsExplicit: true },
                },
                "timeline",
                true,
                before.revision,
            );

            expect(result).toEqual({
                applied: false,
                failure: "invalid-serialized-state",
                revision: before.revision,
                impacts: [],
            });
            expect(h.writeQuietCalls).toHaveLength(0);
            expect(h.notifyHostCalls).toHaveLength(0);
            expect(h.carrier.data).toBe("");
            expect(h.store.getSnapshot()).toEqual(before);
        });

        it("fails closed on a missing target without writing or notifying", () => {
            const before = h.store.getSnapshot();
            const result = h.store.dispatch(
                { type: "clip.remove", clipId: "missing" },
                "timeline",
                true,
                before.revision,
            );

            expect(result).toEqual({
                applied: false,
                failure: "missing-target",
                revision: before.revision,
                impacts: [],
            });
            expect(h.writeQuietCalls).toHaveLength(0);
            expect(h.notifyHostCalls).toHaveLength(0);
            expect(h.store.getSnapshot()).toEqual(before);
        });

        it("treats an empty atomic batch as a no-op without writing or notifying", () => {
            const before = h.store.getSnapshot();
            const seen: UpdateMeta[] = [];
            h.store.subscribe((_state, meta) => {
                seen.push(meta);
            });

            const result = h.store.dispatch(
                { type: "batch", commands: [] },
                "detail-strip",
                true,
                before.revision,
                "value-only",
            );

            expect(result).toEqual({
                applied: true,
                revision: before.revision,
                impacts: [],
            });
            expect(h.writeQuietCalls).toHaveLength(0);
            expect(h.notifyHostCalls).toHaveLength(0);
            expect(seen).toEqual([]);
            expect(h.store.getSnapshot()).toEqual(before);
        });

        it("revalidates and rejects a stale revision before reducing or writing", () => {
            h.carrier.data = '{"width":640,"clips":[]}';
            const stale = h.store.getSnapshot();
            h.carrier.data = '{"width":512,"clips":[]}';

            const result = h.store.dispatch(
                {
                    type: "root.patch",
                    patch: { width: 999, dimsExplicit: true },
                },
                "timeline",
                true,
                stale.revision,
            );

            expect(result).toEqual({
                applied: false,
                failure: "stale-revision",
                revision: stale.revision + 1,
                impacts: [],
            });
            expect(h.writeQuietCalls).toHaveLength(0);
            expect(h.notifyHostCalls).toHaveLength(0);
            expect(h.store.getState().width).toBe(512);
        });

        it("notifies subscribers once with command impacts after the host echo", () => {
            const seen: UpdateMeta[] = [];
            h.store.subscribe((_state, meta) => {
                seen.push(meta);
            });
            let reentrantResult: boolean | null = null;
            h.onNotifyHost = () => {
                reentrantResult = h.store.syncFromCarrier();
            };

            const result = h.store.dispatch(
                { type: "root.patch", patch: { fps: 30 } },
                "detail-strip",
                true,
                undefined,
                "value-only",
            );

            expect(result.applied).toBe(true);
            expect(reentrantResult).toBe(false);
            expect(seen).toEqual([
                {
                    origin: "detail-strip",
                    hint: "value-only",
                    impacts: ["value", "capabilities"],
                },
            ]);
            expect(h.writeQuietCalls).toHaveLength(1);
            expect(h.notifyHostCalls).toHaveLength(1);
        });
    });

    describe("syncFromCarrier", () => {
        it("returns false when nothing changed", () => {
            h.carrier.data = '{"width":640,"clips":[]}';
            h.store.getState();
            expect(h.store.syncFromCarrier()).toBe(false);
        });

        it("absorbs an external change and notifies with external meta", () => {
            h.carrier.data = '{"width":640,"clips":[]}';
            h.store.getState();
            const seen: UpdateMeta[] = [];
            h.store.subscribe((state, meta) => {
                seen.push(meta);
                expect(state.width).toBe(512);
            });
            h.carrier.data = '{"width":512,"clips":[]}';
            expect(h.store.syncFromCarrier()).toBe(true);
            expect(seen).toEqual([{ origin: "external" }]);
            expect(h.store.getState().width).toBe(512);
        });

        it("still notifies when a getState() read raced the external change", () => {
            // An inherited-dims change has no carrier change event — only the
            // poll's syncFromCarrier sees it. A getState() call landing in the
            // poll window re-parses (advancing the cache) but renders nothing;
            // the next sync must STILL notify, or the change never repaints.
            h.carrier.data = '{"width":640,"clips":[]}';
            h.store.getState();
            expect(h.store.syncFromCarrier()).toBe(false);

            h.carrier.dims = "other|h|f";
            h.store.getState(); // racing read: cache adopts the new token
            const seen: UpdateMeta[] = [];
            h.store.subscribe((_state, meta) => {
                seen.push(meta);
            });
            expect(h.store.syncFromCarrier()).toBe(true);
            expect(seen).toHaveLength(1);
            expect(seen[0].origin).toBe("external");
            expect(h.store.syncFromCarrier()).toBe(false);
        });

        it("gives each subscriber a snapshot isolated from the canonical model", () => {
            h.carrier.data = '{"width":640,"clips":[{"duration":2}]}';
            h.store.getState();
            h.store.subscribe((state) => {
                state.clips[0].duration = 99;
            });
            h.carrier.data = '{"width":640,"clips":[{"duration":5}]}';
            h.store.syncFromCarrier();
            expect(h.store.getState().clips[0].duration).toBe(5);
        });

        it("keeps notifying later subscribers when an earlier one throws", () => {
            window.__VIDEO_STAGES_DEBUG__ = true;
            const debug = jest
                .spyOn(console, "debug")
                .mockImplementation(() => {});
            h.carrier.data = '{"width":640,"clips":[]}';
            h.store.getState();
            const seen: string[] = [];
            h.store.subscribe(() => {
                seen.push("first");
                throw new Error("boom");
            });
            h.store.subscribe(() => {
                seen.push("second");
            });
            h.carrier.data = '{"width":512,"clips":[]}';
            h.store.syncFromCarrier();
            expect(seen).toEqual(["first", "second"]);
            expect(debug).toHaveBeenCalledWith(
                "[VideoStages debug store]",
                "subscriber notification failed",
                expect.objectContaining({
                    error: expect.any(Error),
                    origin: "external",
                }),
            );
            delete window.__VIDEO_STAGES_DEBUG__;
        });

        it("stops notifying after unsubscribe", () => {
            const cb = jest.fn();
            const unsubscribe = h.store.subscribe(cb);
            unsubscribe();
            h.carrier.data = '{"width":512,"clips":[]}';
            h.store.syncFromCarrier();
            expect(cb).not.toHaveBeenCalled();
        });
    });
});
