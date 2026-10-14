using FerretSharp.Core.Connections;

namespace FerretSharp.Core.Links;

/// <summary>
/// The receiver's connection for a link (WP-37): <paramref name="Profile"/> if exactly one fits, else the
/// <paramref name="Candidates"/> that fit equally well (the user picks one); neither if none fits.
/// </summary>
public sealed record LinkConnectionMatch(ConnectionProfile? Profile, IReadOnlyList<ConnectionProfile> Candidates)
{
    /// <summary>
    /// Name and address first: a colleague's connection of the same name may point at another database, so a name alone
    /// counts only when the address finds nothing. By address (<see cref="OracleAddress.SameAs"/>), several fitting ones
    /// are narrowed to those reading the link's schema. Names compare without letter case.
    /// </summary>
    public static LinkConnectionMatch Find(IReadOnlyList<ConnectionProfile> profiles, TableLink link)
    {
        var byName = link.Connection is { } name
            ? profiles.Where(p => string.Equals(p.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        if (link.Address is { } address)
        {
            var byAddress = profiles.Where(p => p.Address.SameAs(address)).ToList();
            if (byName.Intersect(byAddress).ToList() is [var both])
            {
                return new(both, []);
            }

            if (byAddress.Count > 0)
            {
                var sameSchema = byAddress.Where(p => p.EffectiveSchema == link.Owner).ToList(); // the link has the dictionary name
                return Of(sameSchema.Count > 0 ? sameSchema : byAddress);
            }
        }

        return Of(byName);
    }

    private static LinkConnectionMatch Of(List<ConnectionProfile> fitting) => fitting.Count == 1 ? new(fitting[0], []) : new(null, fitting);
}
