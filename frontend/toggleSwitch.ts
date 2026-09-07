export interface ToggleSwitchOptions {
    checked: boolean;
    disabled?: boolean;
    title: string;
    onChange(checked: boolean): void;
}

let toggleSequence = 0;

export function buildToggleSwitch(options: ToggleSwitchOptions): HTMLElement {
    const wrap = document.createElement("span");
    wrap.className =
        "form-check form-switch toggle-switch display-inline-block group-toggler-switch";
    const input = document.createElement("input");
    input.type = "checkbox";
    input.id = `vst_toggle_${++toggleSequence}`;
    input.className = "auto-slider-toggle form-check-input";
    input.checked = options.checked;
    input.disabled = options.disabled === true;
    input.title = options.title;
    input.setAttribute("aria-label", options.title);
    input.addEventListener("click", (event) => event.stopPropagation());
    input.addEventListener("change", (event) => {
        event.stopPropagation();
        options.onChange(input.checked);
    });
    const track = document.createElement("div");
    track.className = "auto-slider-toggle-content";
    wrap.append(input, track);
    return wrap;
}
