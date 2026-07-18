using System.IO.Pipes;
using System.Text;
using System.Text.Json;

const string pipeName = "Sefirah.Control.v1";

try
{
    var request = BuildRequest(args);
    await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(3000);
    using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
    await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    await writer.WriteLineAsync(JsonSerializer.Serialize(request));
    var response = await reader.ReadLineAsync() ?? throw new InvalidOperationException("Sefirah returned no response");
    using var document = JsonDocument.Parse(response);
    Console.WriteLine(JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true }));
    return document.RootElement.GetProperty("ok").GetBoolean() ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        ok = false,
        error = new { code = "cli_failed", message = ex.Message },
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 1;
}

static object BuildRequest(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
    {
        Console.WriteLine("""
            sefirahctl status
            sefirahctl bluetooth endpoints
            sefirahctl bluetooth list [local|phone|endpoint-id]
            sefirahctl bluetooth discover
            sefirahctl bluetooth config
            sefirahctl bluetooth switch <headset-id> <endpoint>
            sefirahctl bluetooth command <endpoint> <connect|disconnect> <device-key>
            sefirahctl bluetooth command <endpoint> setRadio <on|off>
            """);
        Environment.Exit(0);
    }

    if (args is ["status"])
        return Request("status", new { });
    if (args is ["bluetooth", "endpoints"])
        return Request("bluetooth.endpoints", new { });
    if (args is ["bluetooth", "list"])
        return Request("bluetooth.catalog", new { endpoint = "local" });
    if (args is ["bluetooth", "list", var endpoint])
        return Request("bluetooth.catalog", new { endpoint });
    if (args is ["bluetooth", "discover"])
        return Request("bluetooth.discover", new { });
    if (args is ["bluetooth", "config"])
        return Request("bluetooth.config", new { });
    if (args is ["bluetooth", "switch", var headsetId, var target])
        return Request("bluetooth.handoff", new { headsetId, endpoint = target });
    if (args is ["bluetooth", "command", var commandEndpoint, "setRadio", var radioState])
    {
        var enabled = radioState.ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            _ => throw new InvalidOperationException("setRadio requires on or off."),
        };
        return Request("bluetooth.command", new
        {
            endpoint = commandEndpoint,
            action = "setRadio",
            enabled,
        });
    }
    if (args is ["bluetooth", "command", var deviceEndpoint, var action, var deviceKey] &&
        action is "connect" or "disconnect")
        return Request("bluetooth.command", new { endpoint = deviceEndpoint, action, deviceKey });

    throw new InvalidOperationException("Unknown or incomplete command. Run sefirahctl --help.");
}

static object Request(string command, object arguments) => new { command, arguments };
