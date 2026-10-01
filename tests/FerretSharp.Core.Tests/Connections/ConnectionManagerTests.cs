using FerretSharp.Core.Connections;
using FerretSharp.Core.Tests.Fakes;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FerretSharp.Core.Tests.Connections;

public class ConnectionManagerTests
{
    private readonly IConnectionStore _store = Substitute.For<IConnectionStore>();
    private readonly InMemorySecretStore _secrets = new();
    private readonly ConnectionManager _manager;

    public ConnectionManagerTests() => _manager = new ConnectionManager(_store, _secrets);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Load_sorts_by_name_and_raises_changed()
    {
        _store.LoadAsync(Arg.Any<CancellationToken>()).Returns([TestProfiles.HostPort("b"), TestProfiles.HostPort("A")]);
        var raised = 0;
        _manager.Changed += () => raised++;

        await _manager.LoadAsync(Ct);

        Assert.Equal(["A", "b"], _manager.Profiles.Select(p => p.Name));
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Load_failure_is_reported_instead_of_thrown()
    {
        _store.LoadAsync(Arg.Any<CancellationToken>()).Throws(new ConnectionStoreException("kaputt"));

        await _manager.LoadAsync(Ct);

        Assert.Empty(_manager.Profiles);
        Assert.Equal("kaputt", _manager.LoadError);
    }

    [Fact]
    public async Task Save_new_profile_persists_profile_and_password_separately()
    {
        var profile = TestProfiles.HostPort();

        await _manager.SaveAsync(profile, "s3cret", Ct);

        await _store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<ConnectionProfile>>(l => l.Count == 1 && l[0] == profile), Arg.Any<CancellationToken>());
        Assert.Equal("s3cret", _secrets.GetPassword(profile.Id));
        Assert.True(_manager.HasPassword(profile.Id));
    }

    [Fact]
    public async Task Update_without_password_keeps_stored_password()
    {
        var profile = TestProfiles.HostPort();
        await _manager.SaveAsync(profile, "old", Ct);

        await _manager.SaveAsync(profile with { Name = "Renamed" }, null, Ct);

        Assert.Equal("Renamed", Assert.Single(_manager.Profiles).Name);
        Assert.Equal("old", _secrets.GetPassword(profile.Id));
    }

    [Fact]
    public async Task Invalid_profile_is_not_saved()
    {
        var profile = TestProfiles.HostPort() with { Name = "" };

        await Assert.ThrowsAsync<ArgumentException>(() => _manager.SaveAsync(profile, "pw", Ct));

        await _store.DidNotReceive().SaveAsync(Arg.Any<IReadOnlyList<ConnectionProfile>>(), Arg.Any<CancellationToken>());
        Assert.Empty(_secrets.Passwords);
    }

    [Fact]
    public async Task Delete_removes_profile_and_password()
    {
        var profile = TestProfiles.HostPort();
        await _manager.SaveAsync(profile, "pw", Ct);

        await _manager.DeleteAsync(profile.Id, Ct);

        Assert.Empty(_manager.Profiles);
        Assert.Null(_secrets.GetPassword(profile.Id));
    }
}
