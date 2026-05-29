using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using MinorShift.Emuera.Forms;
using MinorShift.Emuera.Runtime;

namespace MinorShift.Emuera.GameView
{
    internal sealed class AgentMcpProtocol : IAgentProtocol
    {
        private readonly EmueraConsole console;
        private readonly MainWindow window;
        private readonly Func<bool> isStopped;
        private const int TurnTimeoutMs = 30000;
        private const int PollIntervalMs = 50;

        public AgentMcpProtocol(EmueraConsole console, MainWindow window, Func<bool> isStopped)
        {
            this.console = console;
            this.window = window;
            this.isStopped = isStopped;
        }

        public void Run(string firstLine)
        {
            HandleMessage(firstLine);

            while (!isStopped())
            {
                string line;
                try { line = Console.ReadLine(); }
                catch (ThreadInterruptedException) { break; }
                if (line == null) break;
                HandleMessage(line);
            }
        }

        private bool WaitForTurn(out string turnJson)
        {
            var sw = Stopwatch.StartNew();
            while (!isStopped())
            {
                var state = console.State;
                if (state == ConsoleState.WaitInput || state == ConsoleState.Quit || state == ConsoleState.Error)
                {
                    var text = console.ReadAgentBuffer();
                    var req = console.CurrentRequest;
                    turnJson = JsonSerializer.Serialize(new
                    {
                        text,
                        state = state.ToString(),
                        inputType = req?.InputType.ToString(),
                        needValue = req?.NeedValue ?? false
                    });
                    return true;
                }
                if (sw.ElapsedMilliseconds > TurnTimeoutMs)
                    break;
                Thread.Sleep(PollIntervalMs);
            }
            turnJson = null;
            return false;
        }

        private string BuildTurn()
        {
            var text = console.ReadAgentBuffer();
            var req = console.CurrentRequest;
            return JsonSerializer.Serialize(new
            {
                text,
                state = console.State.ToString(),
                inputType = req?.InputType.ToString(),
                needValue = req?.NeedValue ?? false
            });
        }

        private void HandleMessage(string line)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string method = root.GetProperty("method").GetString();
            long id = root.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : -1;

            switch (method)
            {
                case "initialize":
                    Respond(id, new
                    {
                        protocolVersion = "2024-11-05",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "emuera", version = "1.0" }
                    });
                    break;

                case "notifications/initialized":
                    break;

                case "tools/list":
                    Respond(id, new
                    {
                        tools = new object[]
                        {
                            new
                            {
                                name = "emuera_step",
                                description = "Submit input and wait for next game output turn",
                                inputSchema = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        value = new { type = "string", description = "Input value; omit to read current state only" }
                                    }
                                }
                            },
                            new
                            {
                                name = "emuera_get_state",
                                description = "Wait for game to reach WaitInput and return state + output text (blocking until ready)",
                                inputSchema = new { type = "object", properties = new { } }
                            },
                            new
                            {
                                name = "emuera_kill",
                                description = "Force kill the Emuera game process and close its window",
                                inputSchema = new { type = "object", properties = new { } }
                            }
                        }
                    });
                    break;

                case "tools/call":
                    string toolName = root.GetProperty("params").GetProperty("name").GetString();
                    HandleToolCall(id, toolName, root.GetProperty("params"));
                    break;
            }
        }

        private void HandleToolCall(long id, string toolName, JsonElement params_)
        {
            if (toolName == "emuera_get_state")
            {
                if (!WaitForTurn(out var turnJson))
                {
                    RespondError(id, $"Timed out waiting for game to be ready (current state: {console.State})");
                    return;
                }
                Respond(id, new
                {
                    content = new[] { new { type = "text", text = turnJson } }
                });
            }
            else if (toolName == "emuera_step")
            {
                string value = null;
                if (params_.TryGetProperty("arguments", out var args)
                    && args.TryGetProperty("value", out var valEl)
                    && valEl.ValueKind == JsonValueKind.String)
                {
                    string s = valEl.GetString();
                    if (!string.IsNullOrEmpty(s))
                        value = s;
                }

                if (value != null)
                {
                    if (console.State != ConsoleState.WaitInput)
                    {
                        RespondError(id, $"Game is not waiting for input (current state: {console.State})");
                        return;
                    }

                    console.TakeAgentBuffer(); // discard previous turn output

                    // Synchronous Invoke — blocks agent thread until UI processes input
                    window.Invoke(new Action(() =>
                    {
                        if (console.State == ConsoleState.WaitInput)
                            console.PressEnterKey(false, value, false);
                    }));

                    // After Invoke returns, game is at WaitInput/Quit/Error
                    Respond(id, new
                    {
                        content = new[] { new { type = "text", text = BuildTurn() } }
                    });
                }
                else
                {
                    if (!WaitForTurn(out var turnJson))
                    {
                        RespondError(id, $"Timed out waiting for game turn (current state: {console.State})");
                        return;
                    }
                    Respond(id, new
                    {
                        content = new[] { new { type = "text", text = turnJson } }
                    });
                }
            }
            else if (toolName == "emuera_kill")
            {
                window.BeginInvoke(new Action(() => window.Close()));
                Respond(id, new
                {
                    content = new[] { new { type = "text", text = "{\"killed\":true,\"message\":\"Game process terminated\"}" } }
                });
            }
        }

        private static void Respond(long id, object result)
        {
            var msg = new { jsonrpc = "2.0", id, result };
            Console.WriteLine(JsonSerializer.Serialize(msg));
        }

        private static void RespondError(long id, string errorMessage)
        {
            var msg = new
            {
                jsonrpc = "2.0",
                id,
                error = new { code = -32000, message = errorMessage }
            };
            Console.WriteLine(JsonSerializer.Serialize(msg));
        }
    }
}
