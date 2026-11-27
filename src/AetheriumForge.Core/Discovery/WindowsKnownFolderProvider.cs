using System.Runtime.InteropServices;

namespace AetheriumForge.Core.Discovery;

/// <summary>
/// <see cref="IKnownFolderProvider"/> backed by the Windows Known Folder API (<c>SHGetKnownFolderPath</c>).
/// </summary>
/// <remarks>
/// Users often move Documents to another drive, so the path is never built from <c>%USERPROFILE%</c> (IMPLEMENTATION_GUIDE §4.2).
/// </remarks>
public sealed class WindowsKnownFolderProvider : IKnownFolderProvider
{
    private static readonly Guid _folderIdDocuments = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    private static readonly Guid _folderIdLocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

    /// <inheritdoc />
    public string GetDocumentsPath() => GetKnownFolderPath(_folderIdDocuments);

    /// <inheritdoc />
    public string GetLocalApplicationDataPath() => GetKnownFolderPath(_folderIdLocalAppData);

    private static string GetKnownFolderPath(Guid folderId)
    {
        var hresult = NativeMethods.SHGetKnownFolderPath(in folderId, 0, IntPtr.Zero, out var buffer);
        try
        {
            Marshal.ThrowExceptionForHR(hresult);

            return Marshal.PtrToStringUni(buffer)
                ?? throw new InvalidOperationException($"SHGetKnownFolderPath returned no path for {folderId}.");
        }
        finally
        {
            // The shell allocates the buffer with CoTaskMemAlloc even when the call fails.
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    /// <remarks>
    /// <c>DllImport</c> rather than <c>LibraryImport</c>: the LibraryImport source generator always emits <c>unsafe</c> code
    /// (SYSLIB1062) and the project does not enable <c>AllowUnsafeBlocks</c>. Every parameter here is blittable,
    /// so the runtime marshaller adds no per-call cost.
    /// </remarks>
    private static class NativeMethods
    {
        [DllImport("shell32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
    }
}
