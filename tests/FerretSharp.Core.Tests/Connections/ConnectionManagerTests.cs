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

    /// <summary>R4a: with the list empty after a load failure, the first new connection replaced the file – all others were lost.</summary>
    [Fact]
    public async Task A_file_that_could_not_be_read_is_never_written()
    {
        _store.LoadAsync(Arg.Any<CancellationToken>()).Throws(new ConnectionStoreException("kaputt"));
        await _manager.LoadAsync(Ct);

        var saved = await Assert.ThrowsAsync<IOException>(() => _manager.SaveAsync(TestProfiles.HostPort("Neu"), "pw", Ct));
        await Assert.ThrowsAsync<IOException>(() => _manager.DeleteAsync(Guid.NewGuid(), Ct));

        Assert.StartsWith("Die Verbindungsdatei ließ sich beim Start nicht lesen", saved.Message);
        await _store.DidNotReceive().SaveAsync(Arg.Any<IReadOnlyList<ConnectionProfile>>(), Arg.Any<CancellationToken>());
        Assert.Empty(_manager.Profiles);
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
    public async Task Script_folder_is_remembered_on_the_stored_profile_and_keeps_the_password()
    {
        var profile = TestProfiles.HostPort();
        await _manager.SaveAsync(profile, "pw", Ct);
        _store.ClearReceivedCalls();

        await _manager.SetScriptFolderAsync(profile.Id, @"C:\Repos\Erp\db", Ct);
        await _manager.SetScriptFolderAsync(profile.Id, @"C:\Repos\Erp\db", Ct); // unchanged: not saved again
        await _manager.SetScriptFolderAsync(Guid.NewGuid(), @"C:\anders", Ct); // gone: nothing

        Assert.Equal(@"C:\Repos\Erp\db", Assert.Single(_manager.Profiles).ScriptFolder);
        await _store.Received(1).SaveAsync(Arg.Any<IReadOnlyList<ConnectionProfile>>(), Arg.Any<CancellationToken>());
        Assert.Equal("pw", _secrets.GetPassword(profile.Id));
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

    [Fact]
    public async Task Save_many_writes_once_replaces_by_id_and_keeps_passwords()
    {
        var kept = TestProfiles.HostPort("Kept");
        var replaced = TestProfiles.HostPort("Old name");
        await _manager.SaveAsync(kept, "pw-kept", Ct);
        await _manager.SaveAsync(replaced, "pw-replaced", Ct);
        _store.ClearReceivedCalls();
        var raised = 0;
        _manager.Changed += () => raised++;

        await _manager.SaveManyAsync([replaced with { Name = "New name" }, TestProfiles.Tns()], Ct);

        Assert.Equal(["Kept", "New name", "Prod ERP"], _manager.Profiles.Select(p => p.Name));
        await _store.Received(1).SaveAsync(Arg.Any<IReadOnlyList<ConnectionProfile>>(), Arg.Any<CancellationToken>());
        Assert.Equal(1, raised);
        Assert.Equal("pw-replaced", _manager.GetPassword(replaced.Id));
    }

    [Fact]
    public async Task Save_many_saves_nothing_if_one_profile_is_invalid()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _manager.SaveManyAsync([TestProfiles.HostPort(), TestProfiles.HostPort() with { User = "" }], Ct));

        await _store.DidNotReceive().SaveAsync(Arg.Any<IReadOnlyList<ConnectionProfile>>(), Arg.Any<CancellationToken>());
        Assert.Empty(_manager.Profiles);
    }

    [Fact]
    public async Task A_password_for_this_run_only_is_used_but_not_saved()
    {
        var profile = TestProfiles.HostPort();
        await _manager.SaveAsync(profile, null, Ct);
        Assert.False(_manager.HasPassword(profile.Id));

        _manager.SetPassword(profile.Id, "session", remember: false);

        Assert.Equal("session", _manager.GetPassword(profile.Id));
        Assert.Empty(_secrets.Passwords);

        _manager.SetPassword(profile.Id, "saved", remember: true);

        Assert.Equal("saved", _secrets.GetPassword(profile.Id));
        await _manager.DeleteAsync(profile.Id, Ct);
        Assert.False(_manager.HasPassword(profile.Id));
    }
}
