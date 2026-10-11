// A stand-in for FerretSharp.ModelHost (R4b). Console mode: --console <pipe> --scenario <name>; export mode (as the model
// runner starts the host): --assembly <path> --output <file> --context <scenario>. Each scenario plays one way the real
// host behaves or fails.
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FerretSharp.Core.ClrModel;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i + 1 < args.Length; i += 2)
{
    options[args[i]] = args[i + 1];
}

if (options.TryGetValue("--output", out var output))
{
    return Export(output, options.GetValueOrDefault("--context") ?? "");
}

var scenario = options.GetValueOrDefault("--scenario") ?? "ready";
Console.WriteLine(ModelHostResult.ProgressPrefix + "Fake host starting");
switch (scenario)
{
    case "exit-at-start":
        Console.Error.WriteLine("The fake host gave up.");
        return 3;
    case "never-connects":
        Thread.Sleep(TimeSpan.FromMinutes(2));
        return 0;
}

await using var pipe = new NamedPipeClientStream(".", options["--console"], PipeDirection.InOut, PipeOptions.Asynchronous);
await pipe.ConnectAsync(30_000);
using var reader = new StreamReader(pipe, new UTF8Encoding(false));
await using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

if (scenario == "start-error")
{
    await SendAsync(new LinqResponse(0, Error: new ModelHostError(ModelHostErrorKind.NoContext, "No DbContext in the fake project.", "fake detail")));
    return 1;
}

await SendAsync(new LinqResponse(0));
while (await reader.ReadLineAsync() is { } line)
{
    var request = JsonSerializer.Deserialize<LinqRequest>(line, LinqProtocol.JsonOptions)!;
    if (request.Kind == LinqProtocol.Shutdown)
    {
        return 0;
    }

    switch (scenario)
    {
        case "crash-in-run":
            Console.Error.WriteLine("The fake host crashed.");
            Environment.Exit(5);
            break;
        case "unreadable":
            await writer.WriteLineAsync("{ this is not json");
            break;
        case "hangs-in-run":
            await Task.Delay(Timeout.Infinite);
            break;
        default:
            await SendAsync(new LinqResponse(request.Id, new LinqRunResult([], [], [], [new CapturedCommand(LinqProtocol.Reader, "SELECT 1 FROM DUAL", [])],
                "List<int>", null, TimeSpan.FromMilliseconds(3))));
            break;
    }
}

return 0;

Task SendAsync(LinqResponse response) => writer.WriteLineAsync(JsonSerializer.Serialize(response, LinqProtocol.JsonOptions));

static int Export(string output, string scenario)
{
    switch (scenario)
    {
        case "no-result":
            Console.Error.WriteLine("The fake host ended without a model.");
            return 4;
        case "truncated":
            File.WriteAllText(output, "{\"model\":{\"contextType\":");
            return 0;
        default:
            File.WriteAllText(output, JsonSerializer.Serialize(
                new ModelHostResult(null, new ModelHostError(ModelHostErrorKind.NoContext, "The fake project has no DbContext.")), ModelHostResult.JsonOptions));
            return 0;
    }
}
