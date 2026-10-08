namespace FerretSharp.Core;

/// <summary>
/// Reports on whatever thread calls <see cref="Report"/>, unlike <see cref="Progress{T}"/>, which posts to the context it
/// captured – the services forward progress to their own <c>Changed</c> events, which the UI marshals itself.
/// </summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
