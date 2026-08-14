#if WINDOWS
using Xunit;

namespace Emuera.Maui.Tests;

/// <summary>
/// GET /state 活跃会话判定——MAUI Windows 远程连接入口的纯函数契约。
/// 不测 HttpClient / DisplayAlert / WebView 导航（issue 05：MAUI E2E 不强行造 seam）。
/// </summary>
public class RemoteSessionProbeTests
{
	[Fact]
	public void Idle_state_is_not_an_active_remote_session()
	{
		var active = RemoteSessionProbe.TryParseActiveSession(
			"""{"state":"Idle","isRunning":false,"gameDir":null}""",
			out var gameDir,
			out var state);

		Assert.False(active);
		Assert.Null(gameDir);
		Assert.Equal("Idle", state);
	}

	[Fact]
	public void WaitInput_with_gameDir_is_an_active_remote_session()
	{
		var active = RemoteSessionProbe.TryParseActiveSession(
			"""{"state":"WaitInput","isRunning":true,"gameDir":"D:/games/era"}""",
			out var gameDir,
			out var state);

		Assert.True(active);
		Assert.Equal("D:/games/era", gameDir);
		Assert.Equal("WaitInput", state);
	}

	[Fact]
	public void Ended_session_with_gameDir_is_still_watchable()
	{
		var active = RemoteSessionProbe.TryParseActiveSession(
			"""{"state":"Quit","isRunning":false,"gameDir":"D:/games/era"}""",
			out var gameDir,
			out var state);

		Assert.True(active);
		Assert.Equal("D:/games/era", gameDir);
		Assert.Equal("Quit", state);
	}

	[Fact]
	public void WaitInput_without_gameDir_is_not_active()
	{
		var active = RemoteSessionProbe.TryParseActiveSession(
			"""{"state":"WaitInput","isRunning":true,"gameDir":null}""",
			out _,
			out _);

		Assert.False(active);
	}

	[Fact]
	public void Invalid_json_is_not_active()
	{
		var active = RemoteSessionProbe.TryParseActiveSession("not-json", out var gameDir, out var state);

		Assert.False(active);
		Assert.Null(gameDir);
		Assert.Null(state);
	}

	[Fact]
	public void Null_or_empty_payload_is_not_active()
	{
		Assert.False(RemoteSessionProbe.TryParseActiveSession(null, out _, out _));
		Assert.False(RemoteSessionProbe.TryParseActiveSession("", out _, out _));
	}

	[Fact]
	public void Remote_urls_point_at_localhost_8080()
	{
		Assert.Equal("http://localhost:8080", RemoteSessionProbe.DefaultBaseUrl);
		Assert.Equal("http://localhost:8080/", RemoteSessionProbe.HttpPageUrl);
		Assert.Equal("ws://localhost:8080/ws", RemoteSessionProbe.WebSocketUrl);
	}
}
#endif
