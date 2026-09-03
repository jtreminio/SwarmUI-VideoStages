using VideoStages.Authoring;
using VideoStages.CharacterRefine;

namespace VideoStages.Architectures.Ltx2.Planning;

internal sealed record LtxCharacterRefineReferencePlan(
    string Source,
    UploadedMediaSpec Media);

internal sealed record LtxCharacterRefinePlan(
    CharacterRefineSettingsPlan Settings,
    IReadOnlyList<LtxCharacterRefineReferencePlan> IdentityReferences);
