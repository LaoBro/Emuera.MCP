using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Terminal.Platform;

namespace MinorShift.Emuera.Server;

internal sealed class KestrelGameServer : IDisposable
{
    private const int TurnWaitTimeoutMs = 25000;

    private readonly WebApplication _app;
    private readonly ITerminalSetup _terminalSetup;
    private Session? _session;
    private readonly object _sessionLock = new();

    public KestrelGameServer(int port, ITerminalSetup terminalSetup)
    {
        _terminalSetup = terminalSetup;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        MapRoutes();
    }

    private void MapRoutes()
    {
        _app.MapPost("/session", (Delegate)HandleCreateSessionAsync);
        _app.MapGet("/turn", (Delegate)HandleGetTurnAsync);
        _app.MapPost("/input", (Delegate)HandlePostInputAsync);
        _app.MapGet("/state", (Delegate)HandleGetStateAsync);
        _app.MapDelete("/session", (Delegate)HandleDeleteSessionAsync);
    }

    public async Task StartAsync()
    {
        await _app.StartAsync();
        Console.Error.WriteLine("[server] Kestrel 监听已启动");
    }

    public async Task WaitForShutdownAsync()
    {
        await _app.WaitForShutdownAsync();
    }

    private IResult HandleCreateSessionAsync()
    {
        bool conflict;
        string? sessionId = null;
        DateTimeOffset createdAt = default;
        string? state = null;

        lock (_sessionLock)
        {
            if (_session != null && !_session.HasEnded)
            {
                conflict = true;
            }
            else
            {
                conflict = false;
                if (_session != null)
                {
                    _session.Dispose();
                    _session = null;
                }

                var io = new HttpSessionIO();
                _session = new Session(io, _terminalSetup);
                _session.Start();
                sessionId = _session.Id;
                createdAt = _session.CreatedAt;
                state = _session.StateString;
            }
        }

        if (conflict)
            return Results.Json(new { error = "A session is already active" }, statusCode: 409);

        return Results.Json(new { sessionId, createdAt, state }, statusCode: 201);
    }

    private async Task<IResult> HandleGetTurnAsync(HttpContext context)
    {
        Session? session;
        lock (_sessionLock)
        {
            session = _session;
        }

        if (session == null)
            return Results.Json(new { error = "No active session" }, statusCode: 404);

        if (session.IsFinalTurnDelivered)
            return Results.Json(new { error = "Session ended" }, statusCode: 404);

        var result = await session.WaitForTurnAsync(TurnWaitTimeoutMs, context.RequestAborted);
        switch (result.Status)
        {
            case TurnWaitStatus.Turn:
                return Results.Text(result.Turn!, "application/json", Encoding.UTF8, 200);
            case TurnWaitStatus.Timeout:
                return Results.NoContent();
            case TurnWaitStatus.Closed:
                return Results.Json(new { error = "Session ended" }, statusCode: 404);
            default:
                return Results.Json(new { error = "unexpected turn wait status" }, statusCode: 500);
        }
    }

    private async Task<IResult> HandlePostInputAsync(HttpContext context)
    {
        Session? session;
        lock (_sessionLock)
        {
            session = _session;
        }

        if (session == null)
            return Results.Json(new { error = "No active session" }, statusCode: 404);

        string? value;
        using (var reader = new StreamReader(context.Request.Body))
        {
            var body = await reader.ReadToEndAsync();
            try
            {
                var input = JsonSerializer.Deserialize<HttpInput>(body);
                value = input?.value;
            }
            catch
            {
                return Results.Json(new { error = "Invalid JSON, expected {\"value\":\"...\"}" }, statusCode: 400);
            }
        }

        if (value == null)
            return Results.Json(new { error = "Missing 'value' field" }, statusCode: 400);

        var jsonl = JsonSerializer.Serialize(new { type = "input", value });
        session.IO.EnqueueInput(jsonl);
        return Results.Json(new { received = true });
    }

    private IResult HandleGetStateAsync()
    {
        Session? session;
        lock (_sessionLock)
        {
            session = _session;
        }

        if (session == null)
            return Results.Json(new { state = "Idle", isRunning = false });

        return Results.Json(new
        {
            state = session.StateString,
            isRunning = session.IsRunning,
            sessionId = session.Id,
            createdAt = session.CreatedAt
        });
    }

    private IResult HandleDeleteSessionAsync()
    {
        bool removed;
        lock (_sessionLock)
        {
            if (_session != null)
            {
                _session.Dispose();
                _session = null;
                removed = true;
            }
            else
            {
                removed = false;
            }
        }

        return Results.Json(new { removed }, statusCode: removed ? 200 : 404);
    }

    private sealed class HttpInput
    {
        public string? value { get; set; }
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
        }
        ((IDisposable)_app).Dispose();
    }
}
