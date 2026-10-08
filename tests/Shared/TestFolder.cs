using FerretSharp.Core.IO;

namespace FerretSharp.Testing;

/// <summary>
/// A test's own temp folder, the only way tests delete a folder with its contents (enforced by
/// <c>SafeDeleteTests.Only_the_guarded_helpers_delete_folders</c>): created fresh below the temp path, deleted through
/// <see cref="SafeDelete"/>, so a wrong path can never take anything else with it. Linked into every test project.
/// </summary>
internal sealed class TestFolder : IDisposable
{
    public const string Prefix = "ferret-test-";

    public TestFolder() => Path = Directory.CreateTempSubdirectory(Prefix).FullName;

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>
    /// Retries a few times: work started by the test may still write here (a reconnect runs in the background, a killed
    /// process lets go of its files late). Leaves the folder behind rather than failing the test.
    /// </summary>
    public void Dispose()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                SafeDelete.DirectoryBelow(System.IO.Path.GetTempPath(), Path);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
