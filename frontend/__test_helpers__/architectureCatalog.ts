import { resetArchitectureCatalogForTests as resetRepository } from "../architectures/catalogRepository";

export const resetArchitectureCatalogForTests = (): void => {
    resetRepository();
};
