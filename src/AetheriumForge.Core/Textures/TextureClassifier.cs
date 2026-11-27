namespace AetheriumForge.Core.Textures;

/// <summary>
/// Classifies textures by file-name suffix and knows which kinds are skipped by default (IMPLEMENTATION_GUIDE §8).
/// </summary>
public static class TextureClassifier
{
    // Longest suffix first so that, for example, "_envmask" wins over "_m", "_msn" over "_n" and "_bl" over "_b".
    private static readonly (string Suffix, TextureKind Kind)[] _suffixes =
    [
        ("_envmask", TextureKind.EnvironmentMask),
        ("_msn", TextureKind.ModelSpaceNormal),
        ("_em", TextureKind.Environment),
        ("_sk", TextureKind.SkinTint),
        ("_bl", TextureKind.Backlight),
        ("_n", TextureKind.Normal),
        ("_s", TextureKind.Specular),
        ("_g", TextureKind.Glow),
        ("_p", TextureKind.Parallax),
        ("_e", TextureKind.Environment),
        ("_m", TextureKind.Environment),
        ("_b", TextureKind.Backlight),
        ("_d", TextureKind.Diffuse),
    ];

    /// <summary>
    /// Returns the kind of the texture at <paramref name="path"/> from the suffix of its file name before the extension.
    /// </summary>
    /// <param name="path">A texture path in any case, with <c>\</c> or <c>/</c> separators, for example <c>textures\armor\iron\cuirass_n.dds</c>.</param>
    /// <returns>The texture kind; <see cref="TextureKind.Diffuse"/> when no known suffix is present.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public static TextureKind GetKind(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        ReadOnlySpan<char> name = path.AsSpan().Trim();
        int separator = name.LastIndexOfAny('\\', '/');
        if (separator >= 0)
        {
            name = name[(separator + 1)..];
        }

        int dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            name = name[..dot];
        }

        foreach ((string suffix, TextureKind kind) in _suffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return TextureKind.Diffuse;
    }

    /// <summary>
    /// Returns whether textures of <paramref name="kind"/> are left untouched by default.
    /// </summary>
    /// <param name="kind">The texture kind.</param>
    /// <returns>
    /// <see langword="true"/> for <see cref="TextureKind.ModelSpaceNormal"/>, <see cref="TextureKind.Environment"/> and
    /// <see cref="TextureKind.SkinTint"/>; otherwise <see langword="false"/>.
    /// </returns>
    public static bool IsSkippedByDefault(TextureKind kind) => kind is TextureKind.ModelSpaceNormal or TextureKind.Environment or TextureKind.SkinTint;
}
