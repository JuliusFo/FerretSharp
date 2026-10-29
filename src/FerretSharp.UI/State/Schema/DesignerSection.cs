using System.Globalization;
using FerretSharp.Core.Audit;
using FerretSharp.Core.Design;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>The tabs of the edit mode; the draft keeps the one shown.</summary>
public enum DesignerSection
{
    Columns,
    Constraints,
    Audit,

    /// <summary>Property lines for new columns of a table the C# model maps.</summary>
    CSharp,

    /// <summary>The DDL with its problems and warnings.</summary>
    Script,
}
