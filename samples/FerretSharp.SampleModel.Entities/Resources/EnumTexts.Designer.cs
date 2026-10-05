// Written like the code Visual Studio's ResXFileCodeGenerator generates for a .resx: the class [Display(ResourceType = …)]
// refers to. The texts are in EnumTexts.resx (neutral, English) and EnumTexts.de.resx (satellite assembly de\…).
#nullable enable

using System.Globalization;
using System.Resources;

namespace FerretSharp.SampleModel.Entities.Resources;

/// <summary>Display texts of the sample enums.</summary>
public static class EnumTexts
{
    private static ResourceManager? _resourceManager;

    public static ResourceManager ResourceManager =>
        _resourceManager ??= new ResourceManager("FerretSharp.SampleModel.Entities.Resources.EnumTexts", typeof(EnumTexts).Assembly);

    public static CultureInfo? Culture { get; set; }

    public static string Offen => Text(nameof(Offen));

    public static string Versandt => Text(nameof(Versandt));

    public static string Storniert => Text(nameof(Storniert));

    public static string Abgeschlossen => Text(nameof(Abgeschlossen));

    private static string Text(string name) => ResourceManager.GetString(name, Culture) ?? name;
}
