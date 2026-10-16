using Chronicle.Plugins.Models;

namespace Chronicle.Plugins;

/// <summary>
/// Optional. A metadata provider implements this to tell Chronicle how its ids behave (a shared prefix other plugins also
/// report, entity prefixes stored in another form, ids that are unusable, whether the id is a real identity, how it appears in
/// another provider's cross-reference block). Chronicle knows no provider by name, so anything it must do differently for one
/// provider's ids is declared here by that provider. Providers that implement nothing get plain handling.
/// </summary>
public interface IExternalIdPolicyProvider
{
    ExternalIdPolicy GetExternalIdPolicy();
}
