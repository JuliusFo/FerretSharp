using FerretSharp.UI.State;
using Microsoft.JSInterop;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FerretSharp.UI.Tests.State;

/// <summary>R5b (review 3.31.0, B9): one way to import, call and dispose an ES module for a component.</summary>
public sealed class JsModuleTests
{
    private const string Path = "./_content/FerretSharp.UI/js/x.js";

    private readonly IJSRuntime _js = Substitute.For<IJSRuntime>();
    private readonly IJSObjectReference _module = Substitute.For<IJSObjectReference>();

    private void ImportReturns(ValueTask<IJSObjectReference> import) =>
        _js.InvokeAsync<IJSObjectReference>("import", Arg.Any<object?[]?>()).Returns(import);

    [Fact]
    public async Task The_module_is_imported_once_on_first_use()
    {
        ImportReturns(ValueTask.FromResult(_module));
        var module = new JsModule(_js, Path);

        Assert.False(module.IsLoaded);
        Assert.True(await module.InvokeVoidAsync("a", 1));
        await module.InvokeVoidAsync("b");

        Assert.True(module.IsLoaded);
        await _js.Received(1).InvokeAsync<IJSObjectReference>("import", Arg.Is<object?[]?>(a => a![0] as string == Path));
        await _module.Received(1).InvokeVoidAsync("a", Arg.Is<object?[]?>(a => (int)a![0]! == 1));
    }

    /// <summary>TabBar and the shortcut settings kept a module that arrived after they were gone, and never disposed it.</summary>
    [Fact]
    public async Task A_module_arriving_after_the_dispose_is_disposed_and_not_called()
    {
        var import = new TaskCompletionSource<IJSObjectReference>();
        ImportReturns(new ValueTask<IJSObjectReference>(import.Task));
        var module = new JsModule(_js, Path);

        var call = module.InvokeAsync<bool>("cellEditing").AsTask();
        await module.DisposeAsync();
        import.SetResult(_module);

        Assert.False(await call);
        await _module.Received(1).DisposeAsync();
        await _module.DidNotReceive().InvokeAsync<bool>(Arg.Any<string>(), Arg.Any<object?[]?>());
    }

    [Fact]
    public async Task After_the_dispose_nothing_is_imported_or_called()
    {
        var module = new JsModule(_js, Path);
        await module.DisposeAsync();

        Assert.False(await module.LoadAsync());
        Assert.False(await module.InvokeVoidAsync("a"));

        await _js.DidNotReceive().InvokeAsync<IJSObjectReference>(Arg.Any<string>(), Arg.Any<object?[]?>());
    }

    [Fact]
    public async Task The_last_call_runs_before_the_module_is_disposed()
    {
        ImportReturns(ValueTask.FromResult(_module));
        var module = new JsModule(_js, Path);
        await module.LoadAsync();

        await module.DisposeAsync("destroy", "grid-1");

        Received.InOrder(() =>
        {
            _module.InvokeVoidAsync("destroy", Arg.Any<object?[]?>());
            _module.DisposeAsync();
        });
    }

    [Fact]
    public async Task A_page_that_is_gone_is_no_error()
    {
        ImportReturns(ValueTask.FromResult(_module));
        _module.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>("a", Arg.Any<object?[]?>()).ThrowsAsync(new JSDisconnectedException("gone"));
        _module.DisposeAsync().ThrowsAsync(new JSDisconnectedException("gone"));
        var module = new JsModule(_js, Path);

        Assert.False(await module.InvokeVoidAsync("a"));
        await module.DisposeAsync();
    }

    [Fact]
    public async Task A_script_error_of_a_living_component_is_thrown()
    {
        ImportReturns(ValueTask.FromResult(_module));
        _module.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>("a", Arg.Any<object?[]?>()).ThrowsAsync(new JSException("bug"));

        await Assert.ThrowsAsync<JSException>(async () => await new JsModule(_js, Path).InvokeVoidAsync("a"));
    }
}
