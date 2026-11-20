using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;

namespace Lumen.Api.Catalog;

/// <summary>
/// Lumen's own product images: PNG files shipped in <c>wwwroot/own-media</c>. A product stores them as
/// <c>own-media/{file}</c> in <see cref="ProductImage.SourceUrl"/>; the /media endpoint serves them to the browser
/// under the same Lumen URLs as partner images.
/// </summary>
internal sealed partial class OwnMediaLibrary(IWebHostEnvironment environment)
{
    public const string Folder = "own-media";

    public static string ToSourceUrl(string file) => $"{Folder}/{file}";

    /// <summary>The file name of an own-media source URL, or null for anything else (e.g. a partner URL).</summary>
    public static string? FileOf(string sourceUrl) =>
        sourceUrl.StartsWith(Folder + "/", StringComparison.Ordinal) ? sourceUrl[(Folder.Length + 1)..] : null;

    /// <summary>A bare lower-case file name: no directories, so a request can never reach outside the folder.</summary>
    public static bool IsValidFileName(string? file) => file is not null && FileNamePattern().IsMatch(file);

    public IFileInfo? Find(string? file)
    {
        if (!IsValidFileName(file))
        {
            return null;
        }

        var info = environment.WebRootFileProvider.GetFileInfo(ToSourceUrl(file!));
        return info.Exists && !info.IsDirectory ? info : null;
    }

    public bool Exists(string? file) => Find(file) is not null;

    public IReadOnlyList<string> List() =>
        [.. environment.WebRootFileProvider.GetDirectoryContents(Folder)
            .Where(f => !f.IsDirectory && IsValidFileName(f.Name))
            .Select(f => f.Name)
            .Order(StringComparer.Ordinal)];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}\\.png$")]
    private static partial Regex FileNamePattern();
}
