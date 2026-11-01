using System.Buffers;

namespace FerretSharp.Core.IO;

/// <summary>
/// The file name a "Save as" dialog offers – built from names of the database (table, column, synonym, grantee) or of a
/// project, which may hold anything once quoted. A <c>\</c>, <c>/</c> or <c>..</c> in such a name made the dialog start in
/// another folder (review of WP-68); a device name like <c>NUL</c> would write nowhere. Windows' rules, fixed here rather
/// than <see cref="Path.GetInvalidFileNameChars"/>: Core also runs under Linux, where that knows only <c>/</c>. Beyond
/// them <c>%</c>, which Windows allows in a name but the dialog expands (<c>%APPDATA%</c> saved into <c>…\AppData</c>),
/// and the bidi controls, which show a name reversed (<c>U+202E</c>: <c>txt.exe</c> as <c>exe.txt</c>; review of the fix).
/// </summary>
public static class SuggestedFileName
{
    private const int MaxLength = 255;

    private static readonly SearchValues<char> Invalid = SearchValues.Create(
        "<>:\"/\\|?*%‎‏‪‫‬‭‮⁦⁧⁨⁩");

    private static readonly HashSet<string> Devices = new(
        ["CON", "PRN", "AUX", "NUL",
         "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
         "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="name"/> as one file name in the dialog's folder: characters Windows refuses (separators, <c>:</c>,
    /// control characters …), <c>%</c> and bidi controls become <c>_</c>, trailing dots and blanks go (Windows drops
    /// them), a device name gets a leading <c>_</c>, and a name over 255 characters is cut before its extension – never
    /// inside a surrogate pair.
    /// </summary>
    /// <returns>The file name; <c>_</c> if nothing is left.</returns>
    public static string Of(string name)
    {
        var chars = name.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]) || Invalid.Contains(chars[i]))
            {
                chars[i] = '_';
            }
        }

        var file = new string(chars).TrimEnd('.', ' ');
        if (file.Length == 0)
        {
            return "_";
        }

        if (Devices.Contains(file.Split('.')[0].TrimEnd(' ')))
        {
            file = "_" + file;
        }

        if (file.Length > MaxLength)
        {
            var extension = Path.GetExtension(file);
            file = extension.Length is > 0 and <= 16
                ? Cut(file, MaxLength - extension.Length) + extension
                : Cut(file, MaxLength).TrimEnd('.', ' ');
        }

        return file;
    }

    private static string Cut(string text, int length) =>
        text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];
}
