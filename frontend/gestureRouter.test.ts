import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import {
    claimOnly,
    createGestureRouter,
    type GestureRouter,
    type GestureSession,
} from "./gestureRouter";

const mouse = (
    type: string,
    x = 0,
    y = 0,
    init: MouseEventInit = {},
): MouseEvent =>
    new MouseEvent(type, {
        bubbles: true,
        cancelable: true,
        clientX: x,
        clientY: y,
        ...init,
    });

describe("createGestureRouter", () => {
    let router: GestureRouter;
    let body: HTMLElement;
    let target: HTMLElement;

    beforeEach(() => {
        body = document.createElement("div");
        target = document.createElement("div");
        body.appendChild(target);
        document.body.appendChild(body);
        router = createGestureRouter();
        router.attach(body);
    });

    afterEach(() => {
        router.dispose();
        document.body.innerHTML = "";
    });

    interface MockSession extends GestureSession {
        onMove: jest.Mock;
        onCommit: jest.Mock;
        onTap: jest.Mock;
        onCancel: jest.Mock;
    }

    const session = (
        overrides: Partial<
            Pick<
                GestureSession,
                | "threshold"
                | "axis"
                | "suppressTapClick"
                | "suppressEscapeClick"
            >
        > = {},
    ): MockSession =>
        Object.assign(
            {
                onMove: jest.fn(),
                onCommit: jest.fn(),
                onTap: jest.fn(),
                onCancel: jest.fn(),
            },
            overrides,
        );

    const press = (x = 0, y = 0, init: MouseEventInit = {}): void => {
        target.dispatchEvent(mouse("mousedown", x, y, init));
    };
    const move = (x: number, y = 0): void => {
        document.dispatchEvent(mouse("mousemove", x, y));
    };
    const release = (x = 0, y = 0): void => {
        document.dispatchEvent(mouse("mouseup", x, y));
    };
    const pressEscape = (): void => {
        document.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
        );
    };
    const click = (): boolean => {
        // Returns whether a bubble-phase click listener on the body saw it.
        let seen = false;
        const listener = (): void => {
            seen = true;
        };
        body.addEventListener("click", listener);
        target.dispatchEvent(mouse("click"));
        body.removeEventListener("click", listener);
        return seen;
    };

    it("routes a press to the highest-priority claimant", () => {
        const low = session();
        const high = session();
        const lowPress = jest.fn(() => low);
        const highPress = jest.fn(() => high);
        router.register({ id: "low", priority: 1, onPress: lowPress });
        router.register({ id: "high", priority: 9, onPress: highPress });
        press();
        expect(highPress).toHaveBeenCalledTimes(1);
        expect(lowPress).not.toHaveBeenCalled();
    });

    it("falls through to lower priority when a route declines", () => {
        const low = session();
        router.register({ id: "high", priority: 9, onPress: () => null });
        router.register({ id: "low", priority: 1, onPress: () => low });
        press(0);
        move(50);
        expect(low.onMove).toHaveBeenCalled();
    });

    it("breaks priority ties by registration order", () => {
        const calls: string[] = [];
        router.register({
            id: "first",
            priority: 5,
            onPress: () => {
                calls.push("first");
                return null;
            },
        });
        router.register({
            id: "second",
            priority: 5,
            onPress: () => {
                calls.push("second");
                return null;
            },
        });
        press();
        expect(calls).toEqual(["first", "second"]);
    });

    it("stops a claimed press from reaching bubble handlers, passes unclaimed", () => {
        const bubbleSeen = jest.fn();
        body.addEventListener("mousedown", bubbleSeen);
        const s = session();
        let claim = true;
        router.register({
            id: "r",
            priority: 1,
            onPress: () => (claim ? s : null),
        });
        press();
        expect(bubbleSeen).not.toHaveBeenCalled();
        release();
        claim = false;
        press();
        expect(bubbleSeen).toHaveBeenCalledTimes(1);
    });

    it("honors an earlier capture listener's stopPropagation (chip claim)", () => {
        // Emulates the detail strip's chip handler: capture-phase on the same
        // body, registered BEFORE router.attach, claiming via stopPropagation.
        const chipRouter = createGestureRouter();
        const chipBody = document.createElement("div");
        const chip = document.createElement("div");
        chipBody.appendChild(chip);
        document.body.appendChild(chipBody);
        chipBody.addEventListener(
            "mousedown",
            (event) => event.stopPropagation(),
            true,
        );
        chipRouter.attach(chipBody);
        const onPress = jest.fn(() => session());
        chipRouter.register({ id: "r", priority: 1, onPress });
        chip.dispatchEvent(mouse("mousedown"));
        expect(onPress).not.toHaveBeenCalled();
        chipRouter.dispose();
    });

    it("ignores non-left buttons", () => {
        const onPress = jest.fn(() => session());
        router.register({ id: "r", priority: 1, onPress });
        press(0, 0, { button: 2 });
        expect(onPress).not.toHaveBeenCalled();
    });

    describe("activation", () => {
        it("holds onMove below the threshold, fires at and past it", () => {
            const s = session({ threshold: 5 });
            router.register({ id: "r", priority: 1, onPress: () => s });
            press(100);
            move(103);
            expect(s.onMove).not.toHaveBeenCalled();
            move(106);
            expect(s.onMove).toHaveBeenCalledTimes(1);
            expect(s.onMove.mock.calls[0][0]).toMatchObject({ dx: 6 });
            move(90);
            expect(s.onMove).toHaveBeenCalledTimes(2);
        });

        it("measures |dx| by default: vertical movement never activates", () => {
            const s = session({ threshold: 5 });
            router.register({ id: "r", priority: 1, onPress: () => s });
            press(100, 100);
            move(100, 200);
            expect(s.onMove).not.toHaveBeenCalled();
        });

        it('measures hypot with axis "xy": vertical movement activates', () => {
            const s = session({ threshold: 5, axis: "xy" });
            router.register({ id: "r", priority: 1, onPress: () => s });
            press(100, 100);
            move(100, 200);
            expect(s.onMove).toHaveBeenCalledTimes(1);
        });

        it("threshold 0 is active at press: bare press+release commits", () => {
            const s = session({ threshold: 0 });
            router.register({ id: "r", priority: 1, onPress: () => s });
            press(100);
            release(100);
            expect(s.onCommit).toHaveBeenCalledTimes(1);
            expect(s.onTap).not.toHaveBeenCalled();
        });
    });

    describe("mouseup", () => {
        it("commits an active drag and swallows exactly one click", () => {
            const s = session();
            router.register({ id: "r", priority: 1, onPress: () => s });
            press(0);
            move(50);
            release(50);
            expect(s.onCommit).toHaveBeenCalledTimes(1);
            expect(click()).toBe(false);
            expect(click()).toBe(true);
        });

        it("taps below threshold: onTap fires and the click passes", () => {
            const s = session();
            router.register({ id: "r", priority: 1, onPress: () => s });
            press(0);
            release(0);
            expect(s.onTap).toHaveBeenCalledTimes(1);
            expect(s.onCommit).not.toHaveBeenCalled();
            expect(click()).toBe(true);
        });

        it("suppressTapClick swallows the tap's click too", () => {
            const s = session({ suppressTapClick: true });
            router.register({ id: "r", priority: 1, onPress: () => s });
            press(0);
            release(0);
            expect(click()).toBe(false);
        });

        it("a fresh press voids a pending click swallow", () => {
            const s = session();
            router.register({
                id: "r",
                priority: 1,
                onPress: (event) => (event.target === target ? s : null),
            });
            press(0);
            move(50);
            release(50); // swallow armed
            // A new press elsewhere (unclaimed) voids it, like the old
            // per-module suppressClick reset on mousedown.
            body.dispatchEvent(mouse("mousedown"));
            expect(click()).toBe(true);
        });
    });

    describe("Escape", () => {
        it("cancels the session; default mode never swallows the click", () => {
            const s = session();
            router.register({ id: "r", priority: 1, onPress: () => s });
            press(0);
            move(50);
            pressEscape();
            expect(s.onCancel).toHaveBeenCalledTimes(1);
            release(50);
            expect(s.onCommit).not.toHaveBeenCalled();
            expect(click()).toBe(true);
        });

        it("suppressEscapeClick swallows only after activation", () => {
            const s = session({ suppressEscapeClick: true });
            router.register({ id: "r", priority: 1, onPress: () => s });
            press(0);
            pressEscape(); // inactive
            release(0);
            expect(click()).toBe(true);

            press(0);
            move(50); // active
            pressEscape();
            release(50);
            expect(click()).toBe(false);
        });
    });

    it("claimOnly blocks lower routes but never activates or eats the click", () => {
        const low = session();
        router.register({
            id: "high",
            priority: 9,
            onPress: () => claimOnly(),
        });
        router.register({ id: "low", priority: 1, onPress: () => low });
        press(0);
        move(500);
        release(500);
        expect(low.onMove).not.toHaveBeenCalled();
        expect(click()).toBe(true);
    });

    it("dispose cancels a live session and stops routing", () => {
        const s = session();
        const onPress = jest.fn(() => s);
        router.register({ id: "r", priority: 1, onPress });
        press(0);
        move(50);
        router.dispose();
        expect(s.onCancel).toHaveBeenCalledTimes(1);
        press(0);
        expect(onPress).toHaveBeenCalledTimes(1);
    });
});
