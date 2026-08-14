using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MinorShift.Emuera.Tests;

public sealed class ControllerTests
{
    [Fact]
    public void Acquire_release_and_steal_follow_controller_rules()
    {
        using var controller = new MinorShift.Emuera.Server.Controller(TimeSpan.FromMinutes(1));
        var agent = MinorShift.Emuera.Server.ControlIdentity.Agent("agent-1");
        var otherAgent = MinorShift.Emuera.Server.ControlIdentity.Agent("agent-2");

        Assert.Equal(MinorShift.Emuera.Server.ControlAcquireStatus.Acquired, controller.Acquire(agent).Status);
        Assert.Equal(MinorShift.Emuera.Server.ControlAcquireStatus.HeldByAgent, controller.Acquire(otherAgent).Status);
        Assert.Equal(MinorShift.Emuera.Server.ControlAcquireStatus.Acquired, controller.Acquire(agent).Status);

        var stolen = controller.Acquire(MinorShift.Emuera.Server.ControlIdentity.User);
        Assert.Equal(MinorShift.Emuera.Server.ControlAcquireStatus.Acquired, stolen.Status);
        Assert.Equal("stolen", stolen.Event!.Type);
        Assert.Equal(MinorShift.Emuera.Server.ControlReleaseStatus.NotOwner, controller.Release(agent).Status);
        Assert.Equal(MinorShift.Emuera.Server.ControlReleaseStatus.Released, controller.Release(MinorShift.Emuera.Server.ControlIdentity.User).Status);
        Assert.Equal(MinorShift.Emuera.Server.Controller.StateIdle, controller.State);
    }

    [Fact]
    public void Input_gate_allows_idle_and_rejects_non_controller()
    {
        using var controller = new MinorShift.Emuera.Server.Controller(TimeSpan.FromMinutes(1));
        var agent = MinorShift.Emuera.Server.ControlIdentity.Agent("agent-1");

        Assert.Equal(MinorShift.Emuera.Server.ControlGateStatus.Allowed, controller.CheckInput(MinorShift.Emuera.Server.ControlIdentity.User, false).Status);
        controller.Acquire(agent);
        var rejected = controller.CheckInput(MinorShift.Emuera.Server.ControlIdentity.User, false);
        Assert.Equal(MinorShift.Emuera.Server.ControlGateStatus.NotController, rejected.Status);
        Assert.Equal("CONTROL_HELD_BY_AGENT", rejected.Reason);
        Assert.Equal(MinorShift.Emuera.Server.ControlGateStatus.Allowed, controller.CheckInput(agent, false).Status);
        Assert.Equal(MinorShift.Emuera.Server.ControlGateStatus.Allowed, controller.CheckInput(MinorShift.Emuera.Server.ControlIdentity.User, true).Status);
    }

    [Fact]
    public async Task Lease_expiry_releases_agent_and_notifies_waiter()
    {
        using var controller = new MinorShift.Emuera.Server.Controller(TimeSpan.FromMilliseconds(40));
        controller.Acquire(MinorShift.Emuera.Server.ControlIdentity.Agent("agent-1"));

        var controlEvent = await controller.WaitForEventAsync(1000, CancellationToken.None);

        Assert.NotNull(controlEvent);
        Assert.Equal("lease_expired", controlEvent!.Type);
        Assert.Equal(MinorShift.Emuera.Server.Controller.StateIdle, controller.State);
    }

    [Fact]
    public async Task Owner_change_completes_in_flight_signal()
    {
        using var controller = new MinorShift.Emuera.Server.Controller(TimeSpan.FromMinutes(1));
        var ownerChanged = controller.OwnerChangedTask;
        controller.Acquire(MinorShift.Emuera.Server.ControlIdentity.User);

        await ownerChanged.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(MinorShift.Emuera.Server.Controller.StateHeld, controller.State);
    }
}
