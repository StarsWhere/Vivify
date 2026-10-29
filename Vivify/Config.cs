using System.Runtime.CompilerServices;
using IPA.Config.Stores;
using JetBrains.Annotations;

[assembly: InternalsVisibleTo(GeneratedStore.AssemblyVisibilityTarget)]

namespace Vivify;

[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public class Config
{
    // BSIPA's generated store observes virtual setters to persist changes.
    public virtual int MaxCamera2Cams { get; set; } = 2;

    public virtual bool AllowDownload { get; set; }

    public virtual string BundleRepository { get; set; } = "https://repo.totalbs.dev/api/v1/bundles/";
}
