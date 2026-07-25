import { getRootDefaults } from "../rootDefaults";
import { createCapabilityViewResolver } from "./policy";

export const currentCapabilityViewResolver = () =>
    createCapabilityViewResolver(getRootDefaults().modelCatalog);
