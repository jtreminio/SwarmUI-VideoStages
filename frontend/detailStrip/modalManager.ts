export interface ManagedModalSpec {
    modalClass: string;
    backdropClass: string;
    labelledBy: string;
    onKeyDown?: (event: KeyboardEvent) => void;
    onClose?: () => void;
}

export interface ManagedModal {
    modal: HTMLDivElement;
    content: HTMLDivElement;
    header: HTMLDivElement;
    body: HTMLDivElement;
    open(initialFocus?: HTMLElement): void;
    close(): void;
}

let currentModal: ManagedModal | null = null;

export const closeManagedModal = (): void => currentModal?.close();

export const createManagedModal = (spec: ManagedModalSpec): ManagedModal => {
    const backdrop = document.createElement("div");
    backdrop.className = `modal-backdrop fade show ${spec.backdropClass}`;

    const modal = document.createElement("div");
    modal.className = `modal fade show ${spec.modalClass}`;
    modal.style.display = "block";
    modal.tabIndex = -1;
    modal.setAttribute("role", "dialog");
    modal.setAttribute("aria-modal", "true");
    modal.setAttribute("aria-labelledby", spec.labelledBy);

    const dialog = document.createElement("div");
    dialog.className = "modal-dialog modal-dialog-centered";
    dialog.setAttribute("role", "document");
    const content = document.createElement("div");
    content.className = "modal-content";
    const header = document.createElement("div");
    header.className = "modal-header";
    const body = document.createElement("div");
    body.className = "modal-body";
    content.append(header, body);
    dialog.appendChild(content);
    modal.appendChild(dialog);

    let open = false;
    const onKeyDown = (event: KeyboardEvent): void => {
        if (event.key === "Escape") {
            event.preventDefault();
            managed.close();
            return;
        }
        spec.onKeyDown?.(event);
    };
    const managed: ManagedModal = {
        modal,
        content,
        header,
        body,
        open: (initialFocus) => {
            closeManagedModal();
            open = true;
            currentModal = managed;
            document.addEventListener("keydown", onKeyDown);
            document.body.append(backdrop, modal);
            initialFocus?.focus();
        },
        close: () => {
            if (!open) {
                return;
            }
            open = false;
            document.removeEventListener("keydown", onKeyDown);
            if (currentModal === managed) {
                currentModal = null;
            }
            try {
                spec.onClose?.();
            } finally {
                modal.remove();
                backdrop.remove();
            }
        },
    };
    backdrop.addEventListener("click", managed.close);
    modal.addEventListener("mousedown", (event) => {
        if (event.target === modal) {
            managed.close();
        }
    });
    return managed;
};
