using System;
using System.Text.Json;
using MinorShift.Emuera.Forms;
using MinorShift.Emuera.Runtime;

namespace MinorShift.Emuera.GameView
{
    internal sealed class AgentMcpProtocol : AgentStdinProtocol
    {
        public AgentMcpProtocol(EmueraConsole console, MainWindow window)
            : base(console, window) { }

        protected override void HandleMessage(string line)
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
            switch (toolName)
            {
                case "emuera_get_state": GetState(id); break;
                case "emuera_step":     Step(id, params_); break;
                case "emuera_kill":     Kill(id); break;
            }
        }

        private void GetState(long id)
        {
            string turn = GetTurn();
            if (turn == null)
            {
                RespondError(id, $"Timed out waiting for game to be ready (current state: {console.State})");
                return;
            }
            Respond(id, new
            {
                content = new[] { new { type = "text", text = turn } }
            });
        }

        private void Step(long id, JsonElement params_)
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

            string turn = value != null ? SubmitAndGetTurn(value) : GetTurn();
            if (turn == null)
            {
                RespondError(id, $"Timed out waiting for game turn (current state: {console.State})");
                return;
            }

            Respond(id, new
            {
                content = new[] { new { type = "text", text = turn } }
            });
        }

        private void Kill(long id)
        {
            window.BeginInvoke(new Action(() => window.Close()));
            Respond(id, new
            {
                content = new[] { new { type = "text", text = "{\"killed\":true,\"message\":\"Game process terminated\"}" } }
            });
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
