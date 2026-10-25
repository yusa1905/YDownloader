using System;
using System.Collections.Generic;
using Xunit;
using YDownloader.Power;

namespace YDownloader.Tests;

/// <summary>
/// Prüft Plan und Countdown nur mit einer Attrappe: Hier wird nie ein Rechner heruntergefahren, in den Ruhezustand oder
/// in den Energiesparmodus versetzt.
/// </summary>
public class AfterCompletionTests
{
    private sealed class FakeActions : IPowerActions
    {
        public List<PowerAction> Executed { get; } = new();
        public List<string> Log { get; } = new();
        public string? Fail { get; set; }

        public bool IsAvailable(PowerAction action) => true;

        public string? Execute(PowerAction action)
        {
            Executed.Add(action);
            Log.Add("execute " + action);
            return Fail;
        }
    }

    private sealed class Clock
    {
        public DateTime Now { get; set; } = new(2026, 10, 16, 2, 0, 0);
        public void Add(double seconds) => Now = Now.AddSeconds(seconds);
    }

    /// <summary>Lässt Zeit verstreichen, wie es die Oberfläche tut: in kleinen Schritten mit einem Takt je Schritt.</summary>
    private static void Pass(AfterCompletion plan, Clock clock, double seconds)
    {
        for (double left = seconds; left > 1e-9; left -= 0.5)
        {
            clock.Add(Math.Min(0.5, left));
            plan.Tick();
        }
    }

    private static (AfterCompletion Plan, FakeActions Actions, Clock Clock) Make(double countdownSeconds = 60)
    {
        var actions = new FakeActions();
        var clock = new Clock();
        var plan = new AfterCompletion(actions, TimeSpan.FromSeconds(countdownSeconds), () => clock.Now);
        return (plan, actions, clock);
    }

    [Fact]
    public void Without_a_plan_nothing_happens()
    {
        var (plan, actions, _) = Make();

        plan.NotifyAllDone();
        plan.NotifyCompleted("a");
        plan.Tick();

        Assert.False(plan.IsArmed);
        Assert.False(plan.IsCountingDown);
        Assert.Empty(actions.Executed);
        Assert.Equal("", plan.StatusText);
    }

    [Fact]
    public void All_done_starts_the_countdown_but_executes_nothing_yet()
    {
        var (plan, actions, _) = Make();
        int started = 0;
        plan.CountdownStarted += (_, _) => started++;
        plan.Arm(PowerAction.Shutdown);

        plan.NotifyAllDone();

        Assert.True(plan.IsCountingDown);
        Assert.Equal(60, plan.RemainingSeconds);
        Assert.Equal(1, started);
        Assert.Empty(actions.Executed);
    }

    [Fact]
    public void Default_countdown_is_sixty_seconds()
    {
        var plan = new AfterCompletion(new FakeActions());
        Assert.Equal(TimeSpan.FromSeconds(60), AfterCompletion.DefaultCountdown);
        plan.Arm(PowerAction.Sleep);
        plan.NotifyAllDone();
        Assert.Equal(60, plan.RemainingSeconds);
        plan.Disarm();
    }

    [Fact]
    public void Counts_down_and_executes_exactly_once_when_time_is_up()
    {
        var (plan, actions, clock) = Make();
        plan.Arm(PowerAction.Shutdown);
        plan.NotifyAllDone();

        Pass(plan, clock, 30);
        Assert.Equal(30, plan.RemainingSeconds);
        Assert.Empty(actions.Executed);

        Pass(plan, clock, 29.5);
        Assert.Equal(1, plan.RemainingSeconds); // aufgerundet: noch nicht 0
        Assert.Empty(actions.Executed);

        Pass(plan, clock, 0.5);
        Assert.Equal(new[] { PowerAction.Shutdown }, actions.Executed);
        Assert.False(plan.IsArmed);
        Assert.False(plan.IsCountingDown);

        clock.Add(120);
        plan.Tick();
        plan.NotifyAllDone();
        Assert.Single(actions.Executed); // der Plan ist verbraucht, nichts läuft ein zweites Mal
    }

    [Fact]
    public void Cancel_in_the_countdown_stops_the_action_and_drops_the_plan()
    {
        var (plan, actions, clock) = Make();
        plan.Arm(PowerAction.Hibernate);
        plan.NotifyAllDone();
        Pass(plan, clock, 20);

        plan.Cancel();
        clock.Add(100);
        plan.Tick();
        plan.NotifyAllDone();

        Assert.Empty(actions.Executed);
        Assert.False(plan.IsArmed);
        Assert.False(plan.IsCountingDown);
        Assert.Equal(0, plan.RemainingSeconds);
    }

    [Fact]
    public void Cancel_without_countdown_leaves_the_plan_alone()
    {
        var (plan, _, _) = Make();
        plan.Arm(PowerAction.Shutdown);
        plan.Cancel();
        Assert.True(plan.IsArmed);
    }

    [Fact]
    public void Plan_for_one_download_waits_for_that_download_only()
    {
        var (plan, actions, clock) = Make();
        plan.Arm(PowerAction.Sleep, "a", "Film.mkv");

        plan.NotifyAllDone();
        plan.NotifyCompleted("b");
        Assert.False(plan.IsCountingDown);

        plan.NotifyCompleted("a");
        Assert.True(plan.IsCountingDown);
        Pass(plan, clock, 60);
        Assert.Equal(new[] { PowerAction.Sleep }, actions.Executed);
    }

    [Fact]
    public void Plan_for_all_done_ignores_single_completions()
    {
        var (plan, actions, _) = Make();
        plan.Arm(PowerAction.Shutdown);

        plan.NotifyCompleted("a");

        Assert.False(plan.IsCountingDown);
        Assert.Empty(actions.Executed);
    }

    [Fact]
    public void Removing_the_awaited_download_drops_the_plan()
    {
        var (plan, _, _) = Make();
        plan.Arm(PowerAction.Shutdown, "a", "Film.mkv");

        plan.NotifyRemoved("b");
        Assert.True(plan.IsArmed);
        plan.NotifyRemoved("a");
        Assert.False(plan.IsArmed);
    }

    [Fact]
    public void New_work_interrupts_an_all_done_countdown_but_keeps_the_plan()
    {
        var (plan, actions, clock) = Make();
        bool applies = true;
        plan.StillApplies = () => applies;
        plan.Arm(PowerAction.Shutdown);
        plan.NotifyAllDone();

        applies = false; // der Nutzer fügt neue Downloads hinzu
        clock.Add(10);
        plan.Tick();
        clock.Add(100);
        plan.Tick();

        Assert.False(plan.IsCountingDown);
        Assert.True(plan.IsArmed);
        Assert.Empty(actions.Executed);

        applies = true; // die neue Runde ist durch
        plan.NotifyAllDone();
        Assert.True(plan.IsCountingDown);
        Assert.Equal(60, plan.RemainingSeconds);
    }

    [Fact]
    public void New_work_does_not_interrupt_a_countdown_after_a_specific_download()
    {
        var (plan, actions, clock) = Make();
        plan.StillApplies = () => false;
        plan.Arm(PowerAction.Shutdown, "a", "Film.mkv");
        plan.NotifyCompleted("a");

        Pass(plan, clock, 61);

        Assert.Equal(new[] { PowerAction.Shutdown }, actions.Executed);
    }

    [Fact]
    public void Sleeping_during_the_countdown_does_not_make_it_run_out_on_wake_up()
    {
        var (plan, actions, clock) = Make();
        plan.Arm(PowerAction.Shutdown);
        plan.NotifyAllDone();
        Pass(plan, clock, 10);

        clock.Add(8 * 3600); // Deckel zu, am nächsten Morgen wieder auf
        plan.Tick();

        Assert.Empty(actions.Executed);
        Assert.True(plan.IsCountingDown);
        Assert.InRange(plan.RemainingSeconds, 49, 50); // es zählt dort weiter, wo es stehen blieb

        Pass(plan, clock, 50);
        Assert.Equal(new[] { PowerAction.Shutdown }, actions.Executed);
    }

    [Fact]
    public void Execute_now_skips_the_wait_but_only_during_a_countdown()
    {
        var (plan, actions, _) = Make();
        plan.Arm(PowerAction.Hibernate);

        plan.ExecuteNow(); // kein Countdown: nichts
        Assert.Empty(actions.Executed);
        Assert.True(plan.IsArmed);

        plan.NotifyAllDone();
        plan.ExecuteNow();
        Assert.Equal(new[] { PowerAction.Hibernate }, actions.Executed);
        Assert.False(plan.IsArmed);
    }

    [Fact]
    public void Saves_before_the_action_and_a_failing_save_does_not_stop_it()
    {
        var (plan, actions, clock) = Make();
        plan.BeforeExecute = () => actions.Log.Add("save");
        plan.Arm(PowerAction.Shutdown);
        plan.NotifyAllDone();
        Pass(plan, clock, 60);
        Assert.Equal(new[] { "save", "execute Shutdown" }, actions.Log);

        var (plan2, actions2, clock2) = Make();
        plan2.BeforeExecute = () => throw new System.IO.IOException("Platte voll");
        plan2.Arm(PowerAction.Shutdown);
        plan2.NotifyAllDone();
        Pass(plan2, clock2, 60);
        Assert.Equal(new[] { PowerAction.Shutdown }, actions2.Executed);
    }

    [Fact]
    public void Reports_a_failing_action_and_still_consumes_the_plan()
    {
        var (plan, actions, clock) = Make();
        actions.Fail = "Ruhezustand ist nicht eingeschaltet.";
        plan.Arm(PowerAction.Hibernate);
        plan.NotifyAllDone();
        Pass(plan, clock, 60);

        Assert.Equal("Ruhezustand ist nicht eingeschaltet.", plan.Error);
        Assert.False(plan.IsArmed);
        Assert.Single(actions.Executed);
    }

    [Fact]
    public void Arming_again_replaces_the_plan_and_aborts_a_running_countdown()
    {
        var (plan, actions, clock) = Make();
        plan.Arm(PowerAction.Shutdown);
        plan.NotifyAllDone();

        plan.Arm(PowerAction.Sleep);

        Assert.False(plan.IsCountingDown);
        Assert.Equal(PowerAction.Sleep, plan.Kind);
        clock.Add(100);
        plan.Tick();
        Assert.Empty(actions.Executed);

        plan.Arm(PowerAction.None);
        Assert.False(plan.IsArmed);
    }

    [Fact]
    public void Status_text_names_the_action_and_the_download()
    {
        var (plan, _, _) = Make();
        plan.Arm(PowerAction.Shutdown);
        Assert.Equal("Danach: Herunterfahren", plan.StatusText);
        plan.Arm(PowerAction.Sleep, "a", "Film.mkv");
        Assert.Equal("Nach „Film.mkv“: Energie sparen", plan.StatusText);
        plan.Arm(PowerAction.Hibernate);
        Assert.Equal("Danach: Ruhezustand", plan.StatusText);
    }

    [Fact]
    public void Raises_changed_when_the_plan_changes()
    {
        var (plan, _, clock) = Make();
        int changed = 0;
        var names = new List<string?>();
        plan.Changed += (_, _) => changed++;
        plan.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        plan.Arm(PowerAction.Shutdown);
        plan.NotifyAllDone();
        clock.Add(5);
        plan.Tick();

        Assert.True(changed >= 2);
        Assert.Contains("StatusText", names);
        Assert.Contains("RemainingSeconds", names);
    }

    [Fact]
    public void The_default_actions_do_nothing()
    {
        var actions = NoPowerActions.Instance;
        Assert.Null(actions.Execute(PowerAction.Shutdown));
        Assert.Null(actions.Execute(PowerAction.Hibernate));
        Assert.Null(actions.Execute(PowerAction.Sleep));
        Assert.False(actions.IsAvailable(PowerAction.None));
    }
}
