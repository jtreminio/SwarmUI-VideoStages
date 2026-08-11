import { afterEach, describe, expect, it, jest } from "@jest/globals";

import { closeManagedModal, createManagedModal } from "./modalManager";

const create = (
    overrides: {
        onKeyDown?: (event: KeyboardEvent) => void;
        onClose?: () => void;
    } = {},
) =>
    createManagedModal({
        modalClass: "example-modal",
        backdropClass: "example-backdrop",
        labelledBy: "example-title",
        ...overrides,
    });

describe("managed modal", () => {
    afterEach(() => {
        closeManagedModal();
        document.body.innerHTML = "";
    });

    it("builds and opens the shared modal shell", () => {
        const managed = create();
        const focus = document.createElement("button");
        managed.body.appendChild(focus);

        managed.open(focus);

        expect(document.querySelector(".example-backdrop")).not.toBeNull();
        expect(document.querySelector(".example-modal")).toBe(managed.modal);
        expect(managed.modal.getAttribute("role")).toBe("dialog");
        expect(managed.modal.getAttribute("aria-modal")).toBe("true");
        expect(managed.modal.querySelector(":scope .modal-content")).toBe(
            managed.content,
        );
        expect(managed.content.firstElementChild).toBe(managed.header);
        expect(managed.header.nextElementSibling).toBe(managed.body);
        expect(document.activeElement).toBe(focus);
    });

    it("closes from Escape, the backdrop, or the modal background", () => {
        const onClose = jest.fn();
        let managed = create({ onClose });
        managed.open();
        document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape" }));
        expect(document.querySelector(".example-modal")).toBeNull();

        managed = create({ onClose });
        managed.open();
        document.querySelector<HTMLElement>(".example-backdrop")?.click();
        expect(document.querySelector(".example-modal")).toBeNull();

        managed = create({ onClose });
        managed.open();
        managed.modal.dispatchEvent(
            new MouseEvent("mousedown", { bubbles: true }),
        );
        expect(document.querySelector(".example-modal")).toBeNull();
        expect(onClose).toHaveBeenCalledTimes(3);
    });

    it("keeps one modal open and forwards non-dismissal keys", () => {
        const firstClose = jest.fn();
        const onKeyDown = jest.fn();
        const first = create({ onClose: firstClose });
        first.open();

        const second = create({ onKeyDown });
        second.open();
        document.dispatchEvent(new KeyboardEvent("keydown", { key: "i" }));

        expect(firstClose).toHaveBeenCalledTimes(1);
        expect(document.querySelectorAll(".example-modal")).toHaveLength(1);
        expect(document.querySelector(".example-modal")).toBe(second.modal);
        expect(onKeyDown).toHaveBeenCalledTimes(1);
    });
});
