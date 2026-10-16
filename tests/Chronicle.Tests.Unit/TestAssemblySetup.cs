using System.Runtime.CompilerServices;
using Chronicle.Services;

namespace Chronicle.Tests.Unit;

internal static class TestAssemblySetup
{
    /// <summary>Production rests between duplicate merges so other writers get a turn; tests have no other writers and many merges.</summary>
    [ModuleInitializer]
    internal static void Init() => DuplicateCleanupService.MergeBreather = TimeSpan.Zero;
}
