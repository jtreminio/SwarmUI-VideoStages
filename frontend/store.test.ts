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
    fpsExplicit: false,
    clips: [],
});

interface Harness {
    store: TimelineStore;
    carrier: FakeCarrier;
    deps: StoreDeps;
    parseCalls: string[];
    notifyHostCalls: number[];
    onNotifyHost: (() => void) | null;
}

const makeHarness = (): Harness => {
    const carrier: FakeCarrier = { data: "", prompt: "", dims: "w|h|f" };
    const harness = {} as Harness;
    const parseCalls: string[] = [];
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
        writeQuiet: (state: VideoStagesConfig): string => {
            const serialized = JSON.stringify({
                width: state.width,
                clips: state.clips.map((clip) => ({
                    duration: clip.duration,
                })),
            });
            carrier.data = serialized;
            carrier.prompt = state.clips.map((clip) => clip.prompt).join("|");
            return serialized;
        },
        notifyHost: (): void => {
            notifyHostCalls.push(harness.store.version());
            harness.onNotifyHost?.();
        },
    };
    harness.store = createTimelineStore(deps);
    harness.carrier = carrier;
    harness.deps = deps;
    harness.parseCalls = parseCalls;
    harness.notifyHostCalls = notifyHostCalls;
    harness.onNotifyHost = null;
    return harness;
};

const clipStub = (
    duration: number,
    prompt = "",
): VideoStagesConfig["clips"][number] =>
    ({ duration, prompt }) as VideoStagesConfig["clips"][number];

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
    });

    describe("save", () => {
        it("writes carriers, adopts the re-parsed model, and notifies host", () => {
            const state = emptyConfig();
            state.clips = [clipStub(3, "a fox")];
            const serialized = h.store.save(state, "linking", true);
            expect(h.carrier.data).toBe(serialized);
            expect(h.notifyHostCalls).toHaveLength(1);
            // Adopted model is the re-parsed post-write one (prompt overlay
            // re-derived from the carrier), served from cache with no parse.
            const parseCountAfterSave = h.parseCalls.length;
            expect(h.store.getState().clips[0].prompt).toBe("a fox");
            expect(h.parseCalls).toHaveLength(parseCountAfterSave);
        });

        it("skips the host notification when notifyDomChange is false", () => {
            h.store.save(emptyConfig(), "linking", false);
            expect(h.notifyHostCalls).toHaveLength(0);
        });

        it("notifies subscribers with commit meta after the host dispatch", () => {
            const seen: UpdateMeta[] = [];
            h.store.subscribe((_state, meta) => {
                seen.push(meta);
            });
            h.store.save(emptyConfig(), "detail-strip", true);
            expect(seen).toEqual([
                { origin: "detail-strip", kind: "commit", version: 1 },
            ]);
            // Host dispatch happened before subscriber notification.
            expect(h.notifyHostCalls).toEqual([1]);
        });

        it("absorbs its own synchronous host-dispatch echo as a no-op", () => {
            // The host change event dispatches synchronously into our own
            // carrier listener, which calls syncFromCarrier. That reentrant
            // call must see an up-to-date token and do nothing.
            const externalNotifications: UpdateMeta[] = [];
            h.store.subscribe((_state, meta) => {
                if (meta.kind === "external") {
                    externalNotifications.push(meta);
                }
            });
            let reentrantResult: boolean | null = null;
            h.onNotifyHost = () => {
                reentrantResult = h.store.syncFromCarrier();
            };
            h.store.save(emptyConfig(), "detail-strip", true);
            expect(reentrantResult).toBe(false);
            expect(externalNotifications).toEqual([]);
        });

        it("does not let caller mutations after save leak into the store", () => {
            const state = emptyConfig();
            state.clips = [clipStub(3)];
            h.store.save(state, "linking", false);
            state.clips[0].duration = 99;
            expect(h.store.getState().clips[0].duration).toBe(3);
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
            expect(seen).toEqual([
                { origin: "external", kind: "external", version: 1 },
            ]);
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
            expect(seen[0].kind).toBe("external");
            // And the sync is idempotent afterwards.
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

    describe("version", () => {
        it("increments on commits and external absorptions, not on reads", () => {
            expect(h.store.version()).toBe(0);
            h.carrier.data = '{"width":640,"clips":[]}';
            h.store.getState();
            expect(h.store.version()).toBe(0);
            h.store.save(emptyConfig(), "linking", false);
            expect(h.store.version()).toBe(1);
            h.carrier.data = '{"width":512,"clips":[]}';
            h.store.syncFromCarrier();
            expect(h.store.version()).toBe(2);
        });
    });
});
