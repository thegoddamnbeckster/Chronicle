using System.Runtime.CompilerServices;
using Chronicle.Services;

namespace Chronicle.Tests.Integration;

internal static class TestAssemblySetup
{
    [ModuleInitializer]
    internal static void Init() => DuplicateCleanupService.MergeBreather = TimeSpan.Zero;
}
