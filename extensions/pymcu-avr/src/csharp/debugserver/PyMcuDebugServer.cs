// SPDX-License-Identifier: MIT
using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace PyMCU.AVR.DebugServer;

/// <summary>
/// TCP server that accepts one client connection and bridges JSON commands
/// to a <see cref="DebugSession"/>. Protocol is newline-delimited JSON.
///
/// Client → Server:
///   {"type":"launch","hexFile":"...","lineMapFile":"..."}
///   {"type":"setBreakpoints","file":"src/main.py","lines":[10,42]}
///   {"type":"continue"}
///   {"type":"stepOver"}
///   {"type":"stepInto"}
///   {"type":"stepInstruction"}
///   {"type":"pause"}
///   {"type":"getRegisters"}
///   {"type":"getMemory","address":256,"length":32}
///   {"type":"terminate"}
///
/// Server → Client:
///   {"type":"ready"}
///   {"type":"stopped","reason":"breakpoint|step|pause","file":"...","line":42,"pc":4660}
///   {"type":"running"}
///   {"type":"terminated"}
///   {"type":"registers","data":{...}}
///   {"type":"memory","address":256,"data":[...]}
///   {"type":"error","message":"..."}
/// </summary>
public static class PyMcuDebugServer
{
    public static async Task<int> RunAsync(int port, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"[DEBUGSERVER] Listening on 127.0.0.1:{port}");

        try
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            Console.WriteLine("[DEBUGSERVER] Client connected.");
            await HandleClientAsync(client, ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
        }
        return 0;
    }

    private static async Task HandleClientAsync(TcpClient client, CancellationToken outerCt)
    {
        using var cts    = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var       ct     = cts.Token;
        var       stream = client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };

        DebugSession?     session     = null;
        BreakpointSet?    breakpoints = null;
        LineMap?          lineMap     = null;
        Task?             runTask     = null;

        void Send(string json)
        {
            try { writer.WriteLine(json); }
            catch { /* client disconnected */ }
        }

        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        static string Msg(string type)                     => $"{{\"type\":\"{type}\"}}";
        static string MsgErr(string msg)                   => $"{{\"type\":\"error\",\"message\":\"{Esc(msg)}\"}}";
        static string MsgRunning()                         => Msg("running");
        static string MsgReady()                           => Msg("ready");
        static string MsgTerminated()                      => Msg("terminated");
        static string MsgStopped(string reason, string file, int line, uint pc,
                                  List<(string file, int line, uint pc)> frames)
        {
            var sb = new System.Text.StringBuilder(
                $"{{\"type\":\"stopped\",\"reason\":\"{Esc(reason)}\",\"file\":\"{Esc(file)}\",\"line\":{line},\"pc\":{pc},\"frames\":[");
            for (int i = 0; i < frames.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var f = frames[i];
                sb.Append($"{{\"file\":\"{Esc(f.file)}\",\"line\":{f.line},\"pc\":{f.pc}}}");
            }
            sb.Append("]}");
            return sb.ToString();
        }
        static string MsgRegisters(Dictionary<string, int> data)
        {
            var sb = new System.Text.StringBuilder("{\"type\":\"registers\",\"data\":{");
            bool first = true;
            foreach (var (k, v) in data)
            {
                if (!first) sb.Append(',');
                sb.Append('"').Append(k).Append("\":").Append(v);
                first = false;
            }
            sb.Append("}}");
            return sb.ToString();
        }
        static string MsgMemory(int address, byte[] bytes)
        {
            var sb = new System.Text.StringBuilder($"{{\"type\":\"memory\",\"address\":{address},\"data\":[");
            for (int i = 0; i < bytes.Length; i++) { if (i > 0) sb.Append(','); sb.Append(bytes[i]); }
            sb.Append("]}");
            return sb.ToString();
        }

        try
        {
            while (!ct.IsCancellationRequested && client.Connected)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonNode? msg;
                try { msg = JsonNode.Parse(line); }
                catch { Send(MsgErr("Invalid JSON")); continue; }

                var type = msg?["type"]?.GetValue<string>() ?? "";

                switch (type)
                {
                    case "launch":
                    {
                        var hexFile     = msg!["hexFile"]?.GetValue<string>()     ?? "";
                        var lineMapFile = msg!["lineMapFile"]?.GetValue<string>() ?? "";

                        if (!File.Exists(hexFile))
                        {
                            Send(MsgErr($"HEX file not found: {hexFile}"));
                            break;
                        }
                        if (!File.Exists(lineMapFile))
                        {
                            Send(MsgErr($"Linemap not found: {lineMapFile}"));
                            break;
                        }

                        try
                        {
                            lineMap     = LineMap.Load(lineMapFile);
                            breakpoints = new BreakpointSet();
                            Console.Error.WriteLine($"[DEBUGSERVER] Linemap loaded: {lineMap.EntryCount} entries from {lineMapFile}");
                            Console.Error.WriteLine($"[DEBUGSERVER] Sample entries: {lineMap.DebugSummary()}");
                            session?.Dispose();
                            session = new DebugSession(File.ReadAllText(hexFile), lineMap, breakpoints);

                            session.OnStopped += (reason, file, lineno, pc, frames) =>
                                Send(MsgStopped(reason, file, lineno, pc, frames));
                            session.OnTerminated += () =>
                                Send(MsgTerminated());

                            runTask = session.RunAsync(ct);
                            Send(MsgReady());
                        }
                        catch (Exception ex)
                        {
                            Send(MsgErr(ex.Message));
                        }
                        break;
                    }

                    case "setBreakpoints":
                    {
                        if (session is null || lineMap is null || breakpoints is null)
                        {
                            Send(MsgErr("Session not launched."));
                            break;
                        }
                        var file  = msg!["file"]?.GetValue<string>() ?? "";
                        var lines = msg["lines"]?.AsArray()
                                       .Select(n => n?.GetValue<int>() ?? 0)
                                       .ToList() ?? [];
                        Console.Error.WriteLine($"[DEBUGSERVER] setBreakpoints file={file} lines=[{string.Join(",",lines)}]");
                        breakpoints.SetForFile(lineMap, file, lines);
                        Console.Error.WriteLine($"[DEBUGSERVER] After setBreakpoints: {breakpoints.DebugSummary()}");
                        break;
                    }

                    case "continue":
                        if (session is null) { Send(MsgErr("Session not launched.")); break; }
                        session.Continue();
                        Send(MsgRunning());
                        break;

                    case "stepOver":
                        if (session is null) { Send(MsgErr("Session not launched.")); break; }
                        session.StepOver();
                        Send(MsgRunning());
                        break;

                    case "stepInto":
                        if (session is null) { Send(MsgErr("Session not launched.")); break; }
                        session.StepInto();
                        Send(MsgRunning());
                        break;

                    case "stepInstruction":
                        if (session is null) { Send(MsgErr("Session not launched.")); break; }
                        session.StepInstruction();
                        Send(MsgRunning());
                        break;

                    case "pause":
                        session?.Pause();
                        break;

                    case "getRegisters":
                    {
                        if (session is null) { Send(MsgErr("Session not launched.")); break; }
                        var data = session.GetRegisters();
                        Send(MsgRegisters(data));
                        break;
                    }

                    case "getMemory":
                    {
                        if (session is null) { Send(MsgErr("Session not launched.")); break; }
                        var address = msg!["address"]?.GetValue<int>() ?? 0;
                        var length  = msg["length"]?.GetValue<int>()   ?? 32;
                        var bytes   = session.GetMemory(address, length);
                        Send(MsgMemory(address, bytes));
                        break;
                    }

                    case "terminate":
                        cts.Cancel();
                        Send(MsgTerminated());
                        return;

                    default:
                        Send(MsgErr($"Unknown command: {type}"));
                        break;
                }
            }
        }
        finally
        {
            cts.Cancel();
            session?.Dispose();
            if (runTask is not null)
                try { await runTask; } catch { /* ignore */ }
        }
    }

    // ── Entry point ──────────────────────────────────────────────────────────

    public static async Task<int> Main(string[] args)
    {
        var portOpt = new Option<int>("--port")
        {
            Description = "TCP port to listen on",
            DefaultValueFactory = _ => 57000
        };

        var root = new RootCommand("pymcuc-avr-debugserver — PyMCU AVR emulator debug server");
        root.Options.Add(portOpt);
        root.SetAction(async pr =>
        {
            var port = pr.GetValue(portOpt);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            Environment.ExitCode = await RunAsync(port, cts.Token);
        });

        var result = root.Parse(args);
        await result.InvokeAsync();
        return Environment.ExitCode;
    }
}
