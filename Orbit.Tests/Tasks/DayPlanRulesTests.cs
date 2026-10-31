using System.Text.Json;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Components.DayPlans;
using Orbit.Data.Entities;

namespace Orbit.Tests.Tasks;

/// <summary>Carry-overs on the day plan and the Today page's views (spec §6.12, decision 136).</summary>
public class DayPlanRulesTests
{
    private static readonly DateOnly Thursday = new(2026, 9, 24);
    private static readonly DateOnly Friday = new(2026, 9, 25);
    private static readonly DateOnly Saturday = new(2026, 9, 26);
    private static readonly DateOnly Monday = new(2026, 9, 28);

    /// <summary>What <c>TaskService.SetPlannedForAsync</c> writes: the Today tick, the task page's button and MCP <c>update_task</c>.</summary>
    private static string Tick(DateOnly? from, DateOnly? to) =>
        JsonSerializer.Serialize(new ChangeSet().Track("plannedFor", from, to).Changes, OrbitJson.Options);

    /// <summary>What <c>TaskService.CarryOverAsync</c> writes: "Carry selected over" and "Carry all over".</summary>
    private static string Button(DateOnly? from, DateOnly to) =>
        JsonSerializer.Serialize(new { plannedFor = new { from, to }, carriedOver = true }, OrbitJson.Options);

    /// <summary>DAY-001: a move to a later day's plan is a carry-over, whether made with the carry-over buttons or by ticking Today.</summary>
    [Fact]
    public void A_move_to_a_later_day_is_a_carry_over()
    {
        Assert.True(DayPlanRules.IsCarryOver(Button(Friday, Monday)));
        Assert.True(DayPlanRules.IsCarryOver(Tick(Friday, Saturday)));
    }

    /// <summary>DAY-002: a first planning, a move to an earlier or the same day, taking a task off the plan and any other entry are not.</summary>
    [Fact]
    public void Other_plan_changes_are_not_carry_overs()
    {
        Assert.False(DayPlanRules.IsCarryOver(Tick(null, Monday)));      // first planning: the null "from" is omitted
        Assert.False(DayPlanRules.IsCarryOver(Tick(Monday, Friday)));    // moved to an earlier day
        Assert.False(DayPlanRules.IsCarryOver(Button(Monday, Monday)));  // the service skips this, but it isn't a carry-over either
        Assert.False(DayPlanRules.IsCarryOver(Tick(Friday, null)));      // taken off the plan
        Assert.False(DayPlanRules.IsCarryOver(JsonSerializer.Serialize(new ChangeSet().Track("dueDate", Friday, Monday).Changes, OrbitJson.Options)));
        Assert.False(DayPlanRules.IsCarryOver("""{"plannedFor":{"from":"25/09/2026","to":"28/09/2026"}}"""));
        Assert.False(DayPlanRules.IsCarryOver("{}"));
        Assert.False(DayPlanRules.IsCarryOver("not json"));
        Assert.False(DayPlanRules.IsCarryOver(null));
    }

    /// <summary>DAY-003: carry-overs are counted per task, over the task's whole history; a task never carried over is absent.</summary>
    [Fact]
    public void Counts_carry_overs_per_task()
    {
        var slipping = Guid.NewGuid();
        var once = Guid.NewGuid();
        var never = Guid.NewGuid();

        var counts = DayPlanRules.CountCarryOvers(
        [
            (slipping, Tick(null, Friday)),
            (slipping, Button(Friday, Monday)),
            (slipping, Tick(Monday, new DateOnly(2026, 9, 29))),
            (slipping, Tick(null, new DateOnly(2026, 10, 5))),   // taken off in between and planned again: not a carry-over
            (slipping, Button(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 6))),
            (once, Tick(null, Friday)),
            (once, Button(Friday, Monday)),
            (never, Tick(null, Monday)),
        ]);

        Assert.Equal(3, counts[slipping]);
        Assert.Equal(1, counts[once]);
        Assert.False(counts.ContainsKey(never));
    }

    /// <summary>
    /// DAY-004: the tasks carried over onto a day's plan are read from the same moves, with the day each came from; the last plan
    /// is the later of the latest earlier day with tasks still dated it and the latest day tasks were carried over from, so a day
    /// whose open tasks were all carried over is still the last plan.
    /// </summary>
    [Fact]
    public void Carried_over_tasks_and_the_last_plan_come_from_the_moves()
    {
        var fromFriday = Guid.NewGuid();
        var fromThursday = Guid.NewGuid();
        var twice = Guid.NewGuid();
        var firstPlanned = Guid.NewGuid();
        var movedBack = Guid.NewGuid();
        var elsewhere = Guid.NewGuid();

        var carried = DayPlanRules.CarriedInto(
        [
            (fromFriday, Tick(null, Thursday)),
            (fromFriday, Button(Thursday, Friday)),
            (fromFriday, Tick(Friday, Monday)),
            (fromThursday, Button(Thursday, Monday)),
            (twice, Tick(Thursday, Monday)),
            (twice, Tick(Monday, Friday)),
            (twice, Tick(Friday, Monday)),                           // came over twice: the later day counts
            (firstPlanned, Tick(null, Monday)),                      // a first planning isn't a carry-over
            (movedBack, Tick(new DateOnly(2026, 9, 30), Monday)),    // nor is a move back from a later day
            (elsewhere, Button(Friday, Saturday)),                   // carried over, but onto another day
        ], Monday);

        Assert.Equal(3, carried.Count);
        Assert.Equal(Friday, carried[fromFriday]);
        Assert.Equal(Thursday, carried[fromThursday]);
        Assert.Equal(Friday, carried[twice]);

        Assert.Equal(Friday, DayPlanRules.LastPlanDate(Thursday, carried.Values)); // Friday's open tasks were all carried over
        Assert.Equal(Friday, DayPlanRules.LastPlanDate(Friday, []));
        Assert.Equal(Friday, DayPlanRules.LastPlanDate(null, [Thursday, Friday]));
        Assert.Null(DayPlanRules.LastPlanDate(null, []));
    }

    /// <summary>
    /// DAY-005: the view in the address is resolved leniently - Yesterday when there is a last plan, a person (by id, in any case)
    /// or the unassigned tasks when they are on today's plan - and anything else is the Summary; every view's key resolves to it.
    /// </summary>
    [Fact]
    public void The_view_falls_back_to_the_summary()
    {
        var zoe = Assigned.Person(Guid.NewGuid(), "Zoe");
        var zoes = new TaskItem { Assignments = Assigned.ToPeople(zoe) };
        var groups = DayPlanRules.GroupByAssignee([zoes]);

        foreach (var nothing in new[] { null, "", "  ", "summary", "nonsense", "1", Guid.NewGuid().ToString(), "unassigned" })
            Assert.Equal(DayPlanView.Summary, DayPlanRules.ResolveView(nothing, groups, hasYesterday: true));
        Assert.Equal(DayPlanView.Yesterday, DayPlanRules.ResolveView(" Yesterday ", groups, hasYesterday: true));
        Assert.Equal(DayPlanView.Summary, DayPlanRules.ResolveView("yesterday", groups, hasYesterday: false));
        Assert.Equal(DayPlanView.Person(zoe.Id), DayPlanRules.ResolveView(zoe.Id.ToString().ToUpperInvariant(), groups, hasYesterday: false));

        var withNobody = DayPlanRules.GroupByAssignee([zoes, new TaskItem { Assignments = Assigned.To() }]);
        Assert.Equal(DayPlanView.Person(null), DayPlanRules.ResolveView("UNASSIGNED", withNobody, hasYesterday: true));
        var views = new[] { DayPlanView.Summary, DayPlanView.Yesterday, DayPlanView.Person(zoe.Id), DayPlanView.Person(null) };
        foreach (var view in views)
            Assert.Equal(view, DayPlanRules.ResolveView(view.Key, withNobody, hasYesterday: true));
        Assert.Equal(["summary", "yesterday", zoe.Id.ToString(), "unassigned"], views.Select(v => v.Key));
    }

    /// <summary>
    /// DAY-006: an earlier plan is named "Yesterday", by its weekday within the last week, and by its date before that - as a label,
    /// in a sentence ("today" too, for a task done this morning) and as a plan ("Friday's plan").
    /// </summary>
    [Fact]
    public void Earlier_plans_are_named_by_how_long_ago_they_were()
    {
        var sunday = new DateOnly(2026, 9, 27);
        Assert.Equal("Yesterday", DayPlanRules.WhenLabel(sunday, Monday));
        Assert.Equal("Friday", DayPlanRules.WhenLabel(Friday, Monday));
        Assert.Equal("Tuesday", DayPlanRules.WhenLabel(new DateOnly(2026, 9, 22), Monday));
        Assert.Equal("21 Sep", DayPlanRules.WhenLabel(new DateOnly(2026, 9, 21), Monday));    // a week ago
        Assert.Equal("28 Sep", DayPlanRules.WhenLabel(Monday, Monday));

        Assert.Equal("today", DayPlanRules.WhenPhrase(Monday, Monday));
        Assert.Equal("yesterday", DayPlanRules.WhenPhrase(sunday, Monday));
        Assert.Equal("on Friday", DayPlanRules.WhenPhrase(Friday, Monday));
        Assert.Equal("on 21 Sep", DayPlanRules.WhenPhrase(new DateOnly(2026, 9, 21), Monday));

        Assert.Equal("yesterday's plan", DayPlanRules.PlanName(sunday, Monday));
        Assert.Equal("Friday's plan", DayPlanRules.PlanName(Friday, Monday));
        Assert.Equal("the plan for 21 Sep", DayPlanRules.PlanName(new DateOnly(2026, 9, 21), Monday));
    }

    /// <summary>
    /// DAY-007: today's open tasks are flagged for being overdue, blocked or waiting, gated by a dependency, or carried over three
    /// times or more, with their reasons in that order; tasks keep their order, and a closed task is never flagged.
    /// </summary>
    [Fact]
    public void Flags_name_what_is_worth_raising()
    {
        var overdueBlocked = new TaskItem { Number = "T-1", Status = TaskItemStatus.Blocked, DueDate = Friday };
        var gated = new TaskItem { Number = "T-2" };
        var slipping = new TaskItem { Number = "T-3", Status = TaskItemStatus.InProgress };
        var slippedTwice = new TaskItem { Number = "T-4" };
        var closed = new TaskItem { Number = "T-5", Status = TaskItemStatus.Done, DueDate = Friday };
        var dueToday = new TaskItem { Number = "T-6", DueDate = Monday };
        var waiting = new TaskItem { Number = "T-7", Status = TaskItemStatus.Waiting };

        var flags = DayPlanRules.Flags([overdueBlocked, gated, slipping, slippedTwice, closed, dueToday, waiting], Monday,
            new Dictionary<Guid, int> { [slipping.Id] = 3, [slippedTwice.Id] = 2, [closed.Id] = 5 },
            new Dictionary<Guid, WaitingSummary> { [gated.Id] = new(1, "Waits for T-9 to finish"), [closed.Id] = new(1, "ignored") });

        Assert.Equal([overdueBlocked, gated, slipping, waiting], flags.Select(f => f.Task));
        Assert.Equal(["Overdue (due 2026-09-25)", "Blocked"], flags[0].Reasons);
        Assert.Equal(["Gated: Waits for T-9 to finish"], flags[1].Reasons);
        Assert.Equal(["Carried over 3×"], flags[2].Reasons);
        Assert.Equal(["Waiting"], flags[3].Reasons);
    }

    /// <summary>
    /// DAY-008: a day's estimate is drawn against a working day - closed, open and the working day's mark as parts of a bar a
    /// working day long, or the estimate's length when that is longer - and is overloaded when more than a working day is still
    /// open; the two parts never round past the bar, and the unassigned tasks are never anyone's overload.
    /// </summary>
    [Fact]
    public void A_load_is_drawn_against_a_working_day()
    {
        Assert.Equal(new DayLoad(25, 50, 100, false), DayLoad.Of(120, 240, 480));     // 2h closed, 4h open, an 8h day
        Assert.Equal(new DayLoad(0, 100, 80, true), DayLoad.Of(0, 600, 480));         // 10h open
        Assert.Equal(new DayLoad(50, 50, 80, false), DayLoad.Of(300, 300, 480));      // 10h, but only 5h of it open
        Assert.Equal(new DayLoad(33, 34, 100, false), DayLoad.Of(1, 1, 3));           // the open part is what is left of 67
        Assert.Equal(new DayLoad(0, 0, 100, false), DayLoad.Of(0, 0, 480));
        Assert.False(DayLoad.Of(0, 600, 480, person: false).Overloaded);

        var zoe = Assigned.Person(Guid.NewGuid(), "Zoe");
        var groups = DayPlanRules.GroupByAssignee(
        [
            new TaskItem { EstimateMinutes = 600, Assignments = Assigned.ToPeople(zoe) },
            new TaskItem { EstimateMinutes = 600, Assignments = Assigned.To() }
        ]);
        Assert.True(groups[0].Load(480).Overloaded);
        Assert.False(groups[1].Load(480).Overloaded);
    }

    /// <summary>
    /// DAY-009: a person's stand-up holds their part of today's plan and of the last one - closed, carried over, not finished - and
    /// the time they logged that day, biggest first, with today's flags. A shared task is each assignee's; the unassigned tasks have
    /// their own, with no logged time.
    /// </summary>
    [Fact]
    public void A_stand_up_holds_ones_own_part_of_both_plans()
    {
        var (plan, zoe, adam, _, t) = MondayAfterFriday();

        var people = DayPlanRules.People(plan, new Dictionary<Guid, WaitingSummary>());

        Assert.Equal(["Adam", "Zoe", "Unassigned"], people.Select(p => p.Name));
        var (adams, zoes, nobodys) = (people[0], people[1], people[2]);
        Assert.Equal(DayPlanView.Person(adam.Id), adams.View);
        Assert.Equal(zoe.Id, zoes.AssigneeId);

        Assert.Equal([t.Shared], adams.Today.Tasks);
        Assert.Empty(adams.Closed);
        Assert.Equal([t.Shared], adams.CarriedOver);
        Assert.Equal([t.AdamsLeft], adams.NotFinished);
        Assert.Equal(60, adams.LoggedMinutes);

        Assert.Equal([t.Shared, t.ZoesCarried], zoes.Today.Tasks);
        Assert.Equal([t.ZoesDone], zoes.Closed);
        Assert.Equal([t.Shared, t.ZoesCarried], zoes.CarriedOver);
        Assert.Empty(zoes.NotFinished);
        Assert.Equal(["T-9", "T-4"], zoes.Logged.Select(l => l.Number));
        Assert.Equal(120, zoes.LoggedMinutes);
        Assert.Equal([t.ZoesCarried], zoes.Flags.Select(f => f.Task));                // carried over three times

        Assert.Equal(DayPlanView.Person(null), nobodys.View);
        Assert.Equal([t.Nobodys], nobodys.Today.Tasks);
        Assert.Equal([t.Nobodys], nobodys.CarriedOver);
        Assert.Equal([t.NobodysLeft], nobodys.NotFinished);
        Assert.Empty(nobodys.Logged);
    }

    /// <summary>
    /// DAY-010: the last plan's outcome counts that day's tasks only, in the Yesterday view's table and the Summary's donut alike
    /// (decision 137): by person, a shared task for each assignee, someone who only logged time listed, by name, then the unassigned
    /// tasks; the donut leaves out an empty outcome. A task carried over from an older plan is kept apart, not counted as Friday's.
    /// </summary>
    [Fact]
    public void The_last_plans_outcome_is_counted_by_person()
    {
        var (plan, zoe, adam, bea, t) = MondayAfterFriday();

        var last = DayPlanRules.LastPlan(plan);
        Assert.Equal(Friday, last.Day);
        Assert.Equal([t.ZoesDone], last.Closed);
        Assert.Equal([t.Shared, t.ZoesCarried], last.CarriedOver);
        Assert.Equal([t.AdamsLeft, t.NobodysLeft], last.NotFinished);
        Assert.Equal([t.Nobodys], last.CarriedFromEarlier);                // carried over from Thursday
        Assert.Empty(last.LeftEarlier);

        Assert.Equal(
        [
            new DayPlanOutcome(adam.Id, "Adam", 0, 1, 1, 60),
            new DayPlanOutcome(bea.Id, "Bea", 0, 0, 0, 45),
            new DayPlanOutcome(zoe.Id, "Zoe", 1, 2, 0, 120),
            new DayPlanOutcome(null, "Unassigned", 0, 0, 1, 0)
        ], DayPlanRules.Outcomes(plan));

        var donut = DayPlanLook.LastPlan(last);
        Assert.Equal(["Done", "Carried over", "Not finished"], donut.Slices.Select(s => s.Label));
        Assert.Equal([1, 2, 2], donut.Slices.Select(s => s.Count));

        Assert.Empty(DayPlanRules.Outcomes(new DayPlan { Date = Monday }));
    }

    /// <summary>
    /// DAY-011: when the latest day with open tasks left on it is older than the last plan - Friday's open tasks were all carried
    /// over, Thursday's weren't - the last plan's table and donut count none of Thursday's tasks, left on its plan or carried over
    /// from it, while the stand-up still lists them, after Friday's (decision 137).
    /// </summary>
    [Fact]
    public void An_older_plans_tasks_are_not_the_last_plans()
    {
        var zoe = Assigned.Person(Guid.NewGuid(), "Zoe");
        var fromFriday = new TaskItem { Number = "T-1", PlannedFor = Monday, Assignments = Assigned.ToPeople(zoe) };
        var fromThursday = new TaskItem { Number = "T-2", PlannedFor = Monday, Assignments = Assigned.ToPeople(zoe) };
        var leftOnThursday = new TaskItem { Number = "T-3", PlannedFor = Thursday, Assignments = Assigned.ToPeople(zoe) };
        var plan = new DayPlan
        {
            Date = Monday,
            Planned = [fromThursday, fromFriday],
            PreviousDate = Thursday,
            Unfinished = [leftOnThursday],
            CarriedIn = new Dictionary<Guid, DateOnly> { [fromFriday.Id] = Friday, [fromThursday.Id] = Thursday },
            LastPlanDate = Friday,
            LoggedDate = Friday
        };

        var last = DayPlanRules.LastPlan(plan);
        Assert.Equal([fromFriday], last.CarriedOver);
        Assert.Empty(last.NotFinished);
        Assert.Equal([fromThursday], last.CarriedFromEarlier);
        Assert.Equal([leftOnThursday], last.LeftEarlier);

        Assert.Equal([new DayPlanOutcome(zoe.Id, "Zoe", 0, 1, 0, 0)], DayPlanRules.Outcomes(plan));
        var donut = DayPlanLook.LastPlan(last);
        Assert.Equal(["Carried over"], donut.Slices.Select(s => s.Label));
        Assert.Equal([1], donut.Slices.Select(s => s.Count));

        var zoes = Assert.Single(DayPlanRules.People(plan, new Dictionary<Guid, WaitingSummary>()));
        Assert.Equal([fromFriday, fromThursday], zoes.CarriedOver);
        Assert.Equal([leftOnThursday], zoes.NotFinished);
    }

    /// <summary>
    /// DAY-012: a task on the last plan done after that day - this morning, say - is still the plan's, and gives the day it was done;
    /// one done that day, a cancelled one (cancelling records no time), an open one and one with no time recorded don't.
    /// </summary>
    [Fact]
    public void A_task_done_after_its_plans_day_gives_the_day()
    {
        var mondayMorning = new DateTime(2026, 9, 28, 7, 30, 0, DateTimeKind.Utc);
        Assert.Equal(Monday, DayPlanRules.DoneAfter(new TaskItem { Status = TaskItemStatus.Done, CompletedAt = mondayMorning }, Friday));
        Assert.Null(DayPlanRules.DoneAfter(new TaskItem { Status = TaskItemStatus.Done, CompletedAt = new DateTime(2026, 9, 25, 23, 59, 0, DateTimeKind.Utc) }, Friday));
        Assert.Null(DayPlanRules.DoneAfter(new TaskItem { Status = TaskItemStatus.Cancelled }, Friday));
        Assert.Null(DayPlanRules.DoneAfter(new TaskItem { Status = TaskItemStatus.InProgress, CompletedAt = mondayMorning }, Friday));
        Assert.Null(DayPlanRules.DoneAfter(new TaskItem { Status = TaskItemStatus.Done }, Friday));
    }

    private sealed record Fixture(TaskItem Shared, TaskItem ZoesCarried, TaskItem Nobodys, TaskItem ZoesDone, TaskItem AdamsLeft, TaskItem NobodysLeft);

    /// <summary>
    /// Monday's plan after Friday's: Zoe and Adam share a task and Zoe has another, both carried over from Friday, and an unassigned
    /// task carried over from Thursday. On Friday's plan Zoe closed one, and Adam and nobody left one each. Bea logged time on Friday
    /// without being on either plan.
    /// </summary>
    private static (DayPlan Plan, ApplicationUser Zoe, ApplicationUser Adam, ApplicationUser Bea, Fixture Tasks) MondayAfterFriday()
    {
        var zoe = Assigned.Person(Guid.NewGuid(), "Zoe");
        var adam = Assigned.Person(Guid.NewGuid(), "Adam");
        var bea = Assigned.Person(Guid.NewGuid(), "Bea");
        var t = new Fixture(
            Shared: new TaskItem { Number = "T-1", PlannedFor = Monday, Assignments = Assigned.ToPeople(zoe, adam) },
            ZoesCarried: new TaskItem { Number = "T-2", PlannedFor = Monday, Assignments = Assigned.ToPeople(zoe) },
            Nobodys: new TaskItem { Number = "T-3", PlannedFor = Monday, Assignments = Assigned.To() },
            ZoesDone: new TaskItem { Number = "T-4", Status = TaskItemStatus.Done, PlannedFor = Friday, Assignments = Assigned.ToPeople(zoe) },
            AdamsLeft: new TaskItem { Number = "T-5", PlannedFor = Friday, Assignments = Assigned.ToPeople(adam) },
            NobodysLeft: new TaskItem { Number = "T-6", PlannedFor = Friday, Assignments = Assigned.To() });
        var plan = new DayPlan
        {
            Date = Monday,
            Planned = [t.Shared, t.ZoesCarried, t.Nobodys],
            PreviousDate = Friday,
            Unfinished = [t.AdamsLeft, t.NobodysLeft],
            CarryOvers = new Dictionary<Guid, int> { [t.ZoesCarried.Id] = 3, [t.Shared.Id] = 1, [t.Nobodys.Id] = 1 },
            CarriedIn = new Dictionary<Guid, DateOnly> { [t.Shared.Id] = Friday, [t.ZoesCarried.Id] = Friday, [t.Nobodys.Id] = Thursday },
            LastPlanDate = Friday,
            LastPlanClosed = [t.ZoesDone],
            LoggedDate = Friday,
            Logged =
            [
                new LoggedTask(zoe.Id, "Zoe", t.ZoesDone.Id, "T-4", "done", 30),
                new LoggedTask(zoe.Id, "Zoe", Guid.NewGuid(), "T-9", "support", 90),
                new LoggedTask(adam.Id, "Adam", t.AdamsLeft.Id, "T-5", "left", 60),
                new LoggedTask(bea.Id, "Bea", Guid.NewGuid(), "T-8", "unplanned", 45)
            ]
        };
        return (plan, zoe, adam, bea, t);
    }
}
