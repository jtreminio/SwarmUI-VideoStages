using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.WebAPI;
using VideoStages.Architectures;

namespace VideoStages;

/// <summary>API routes the VideoStages frontend calls outside the generation pipeline.</summary>
public static class VideoStagesApi
{
    public static void Register()
    {
        API.RegisterAPICall(
            VideoStagesGetArchitectureCatalog,
            false,
            Permissions.FundamentalGenerateTabAccess);
        VideoArchitectureManifest.RegisterProductionApiRoutes();
    }

    /// <summary>Returns the authoritative architecture/capability catalog and current host models
    /// that resolve through production architecture modules.</summary>
    public static Task<JObject> VideoStagesGetArchitectureCatalog(Session session)
        => Task.FromResult(ArchitectureCatalogSerializer.Serialize(
            VideoArchitectureRegistry.Production.ForSession(session)));

}
