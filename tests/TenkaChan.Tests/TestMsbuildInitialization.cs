using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace TenkaChan.Tests;

internal static class TestMsbuildInitialization
{
    // MSBuildLocator registration is process-global. Several integration
    // classes previously raced between IsRegistered and RegisterDefaults.
    // A module initializer finishes before any tests in this module execute.
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
    }
}
