using System.Runtime.CompilerServices;

[assembly: DisableRuntimeMarshalling]

namespace AetheriumForge.Native;

/// <summary>
/// Marker for the native interop assembly. Runtime marshalling is disabled: every P/Invoke and COM signature here
/// must be blittable or use source-generated marshalling.
/// </summary>
public static class NativeAssembly
{
}
