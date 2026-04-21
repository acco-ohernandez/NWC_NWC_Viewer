using System.IO;
using PdfSharp.Fonts;

namespace NwdViewer.Desktop.Services;

/// <summary>
/// Minimal IFontResolver that reads TrueType files from the Windows Fonts folder.
/// Covers the common faces the app uses (Arial, Segoe UI, Times New Roman, Courier New).
/// Unknown families fall back to Arial so we never throw.
/// </summary>
public sealed class WindowsFontResolver : IFontResolver
{
    private static readonly string FontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    public byte[]? GetFont(string faceName)
    {
        var file = faceName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ? faceName : faceName + ".ttf";
        var path = Path.Combine(FontsDir, file);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var fam = (familyName ?? "").Trim().ToLowerInvariant();
        string face = fam switch
        {
            "arial"           => isBold ? (isItalic ? "arialbi" : "arialbd") : (isItalic ? "ariali"  : "arial"),
            "segoe ui"        => isBold ? (isItalic ? "segoeuiz": "segoeuib"): (isItalic ? "segoeuii": "segoeui"),
            "times new roman" => isBold ? (isItalic ? "timesbi" : "timesbd") : (isItalic ? "timesi"  : "times"),
            "courier new"     => isBold ? (isItalic ? "courbi"  : "courbd")  : (isItalic ? "couri"   : "cour"),
            "consolas"        => isBold ? (isItalic ? "consolaz": "consolab"): (isItalic ? "consolai": "consola"),
            _                 => isBold ? (isItalic ? "arialbi" : "arialbd") : (isItalic ? "ariali"  : "arial"),
        };
        return new FontResolverInfo(face);
    }
}
