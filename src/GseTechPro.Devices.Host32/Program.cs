using System.IO.Pipes;
using System.Text.Json;
using GseTechPro.Devices.Ipc;

namespace GseTechPro.Devices.Host32;

/// <summary>
/// GSE Tech Pro 32-bit device host. Phase 1 is a minimal named-pipe server shell: it accepts a
/// connection on a duplex named pipe, reads a length-prefixed JSON request, and replies with a
/// JSON acknowledgment. Phase 2 loads the x86 J2534/RP1210 vendor DLLs here (the only place they
/// are ever loaded) and dispatches device commands to them.
/// </summary>
public static class Program
{
    public const string PipeName = "GseTechPro.Devices.Host32";

    // Per-session pipe name: a random instance id suffix keeps two host processes (or a stale
    // instance) from colliding and limits reachability to the app instance that was handed the
    // name at launch. Phase 2 hands this name to the app via the process/launch handshake.
    private static readonly string SessionPipeName = $"{PipeName}-{Guid.NewGuid():N}";

    /// <summary>CamelCase JSON on the wire; case-insensitive on read for robustness.</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static async Task Main(string[] args)
    {
        Console.WriteLine($"[Host32] GSE Tech Pro 32-bit device host starting. Pipe: '{SessionPipeName}'.");
        Console.WriteLine("[Host32] Protocol: x86. Phase 1 = named-pipe shell only (no vendor DLLs).");

        // Phase 2: load J2534/RP1210 vendor DLLs here (via their own small wrapper classes), and
        // discover installed devices from the registry (J2534 HKLM\...\PassThruSupport.04.04) and
        // RP121032.ini — before entering the serve loop below.

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        while (!shutdown.IsCancellationRequested)
            await ServeOneConnectionAsync(shutdown.Token).ConfigureAwait(false);

        Console.WriteLine("[Host32] Shutdown complete.");
    }

    private static async Task ServeOneConnectionAsync(CancellationToken ct)
    {
        using var server = new NamedPipeServerStream(
            SessionPipeName,
            PipeDirection.InOut,
            1, // one client at a time in Phase 1
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);

        // Phase 2: shared-token handshake — the app presents a per-launch token (first frame)
        // that the host validates before dispatching any device commands, so only the intended
        // app instance can drive a session.

        try
        {
            Console.WriteLine("[Host32] Waiting for a client connection...");
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            Console.WriteLine("[Host32] Client connected.");
            await ServeClientAsync(server, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[Host32] Connection error: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            Console.WriteLine($"[Host32] Connection error: malformed frame ({ex.Message}).");
        }
        catch (ObjectDisposedException ex)
        {
            Console.WriteLine($"[Host32] Connection error: pipe disposed ({ex.Message}).");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"[Host32] Connection error: access denied ({ex.Message}).");
        }
        finally
        {
            if (server.IsConnected)
                server.Disconnect();
            Console.WriteLine("[Host32] Client disconnected; waiting for the next connection...");
        }
    }

    private static async Task ServeClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        while (server.IsConnected && !ct.IsCancellationRequested)
        {
            string request;
            try
            {
                request = await PipeMessage.ReadAsync(server, ct).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                break; // client closed the pipe
            }
            catch (IOException)
            {
                break;
            }
            catch (InvalidDataException ex)
            {
                Console.WriteLine($"[Host32] Dropping connection: malformed frame ({ex.Message}).");
                break;
            }
            catch (ObjectDisposedException ex)
            {
                Console.WriteLine($"[Host32] Dropping connection: pipe disposed ({ex.Message}).");
                break;
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"[Host32] Dropping connection: access denied ({ex.Message}).");
                break;
            }

            Console.WriteLine($"[Host32] <- {request}");
            var response = HandleRequest(request);
            await PipeMessage.WriteAsync(server, response, ct).ConfigureAwait(false);
            Console.WriteLine($"[Host32] -> {response}");
        }
    }

    private static string HandleRequest(string requestJson)
    {
        try
        {
            var request = JsonSerializer.Deserialize<HostRequest>(requestJson, JsonOpts)
                ?? throw new JsonException("Empty request.");

            // Phase 2: dispatch request.Command to the J2534/RP1210 device layer here.

            var response = new HostResponse(
                Ok: true,
                Command: request.Command,
                Message: "ack",
                Status: "simulated-ok");
            return JsonSerializer.Serialize(response, JsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new HostResponse(
                Ok: false,
                Command: null,
                Message: ex.Message,
                Status: "error"), JsonOpts);
        }
    }
}
