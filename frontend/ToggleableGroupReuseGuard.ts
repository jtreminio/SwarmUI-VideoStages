export interface ToggleableGroupReuseGuardOptions {
    groupContentId: string;
    getEnableToggle: () => HTMLInputElement | null;
    getGroupToggle?: () => HTMLInputElement | null;
    clearInactiveState?: () => boolean;
    afterStateChange?: () => void;
}

// Keep toggleable parameter groups from being spuriously activated while
// "Reuse Parameters" is applying metadata that does not actually use them.
export class ToggleableGroupReuseGuard {
    private static readonly guards: ToggleableGroupReuseGuard[] = [];
    private guardUntil = 0;
    private guardTimer: ReturnType<typeof setTimeout> | null = null;

    public constructor(
        private readonly options: ToggleableGroupReuseGuardOptions,
    ) {
        if (!ToggleableGroupReuseGuard.guards.includes(this)) {
            ToggleableGroupReuseGuard.guards.push(this);
        }
    }

    public tryInstallGroupToggleWrapper(): boolean {
        if (typeof doToggleGroup !== "function") {
            return false;
        }

        const wrappedExisting = doToggleGroup as typeof doToggleGroup & {
            __toggleableGroupReuseGuardWrapped?: boolean;
        };
        if (wrappedExisting.__toggleableGroupReuseGuardWrapped) {
            return true;
        }

        const prior = doToggleGroup;
        const wrapped = ((id: string) => {
            const toggle = document.getElementById(
                `${id}_toggle`,
            ) as HTMLInputElement | null;
            const matchingGuards = ToggleableGroupReuseGuard.guards.filter(
                (guard) => guard.matchesGroup(id),
            );
            const shouldSuppress =
                !!toggle?.checked &&
                matchingGuards.some((guard) =>
                    guard.shouldSuppressGroupActivation(id),
                );
            if (shouldSuppress && toggle) {
                toggle.checked = false;
            }
            return prior(id);
        }) as typeof doToggleGroup & {
            __toggleableGroupReuseGuardWrapped?: boolean;
        };
        wrapped.__toggleableGroupReuseGuardWrapped = true;
        doToggleGroup = wrapped;
        return true;
    }

    public enforceInactiveState(): void {
        let changed = false;
        const groupToggle = this.getGroupToggle();
        if (groupToggle?.checked) {
            groupToggle.checked = false;
            if (typeof doToggleGroup === "function") {
                doToggleGroup(this.options.groupContentId);
            }
            changed = true;
        }

        const enableToggle = this.options.getEnableToggle();
        if (enableToggle?.checked) {
            enableToggle.checked = false;
            changed = true;
        }

        if (this.options.clearInactiveState?.()) {
            changed = true;
        }

        if (changed) {
            this.options.afterStateChange?.();
        }
    }

    public start(durationMs = 1500): void {
        this.stop();
        this.guardUntil = Date.now() + durationMs;
        const tick = () => {
            if (Date.now() >= this.guardUntil) {
                this.stop();
                return;
            }
            this.enforceInactiveState();
            this.guardTimer = setTimeout(tick, 25);
        };
        this.guardTimer = setTimeout(tick, 25);
    }

    public stop(): void {
        if (this.guardTimer) {
            clearTimeout(this.guardTimer);
            this.guardTimer = null;
        }
        this.guardUntil = 0;
    }

    public shouldSuppressGroupActivation(groupId: string): boolean {
        if (
            groupId !== this.options.groupContentId ||
            Date.now() >= this.guardUntil
        ) {
            return false;
        }
        return !this.options.getEnableToggle()?.checked;
    }

    private matchesGroup(groupId: string): boolean {
        return groupId === this.options.groupContentId;
    }

    private getGroupToggle(): HTMLInputElement | null {
        return (
            this.options.getGroupToggle?.() ??
            (document.getElementById(
                `${this.options.groupContentId}_toggle`,
            ) as HTMLInputElement | null)
        );
    }
}
