using System;
using Server.Accounting;
using Server.Commands;
using Server.Mobiles;
using Server.Targeting;
using Server.Text;

namespace Server.Custom.Idle;

public static class IdleCommands
{
    public static void Configure()
    {
        CommandSystem.Register("Idle", AccessLevel.Player, Idle_OnCommand);
        CommandSystem.Register("IdleSim", AccessLevel.GameMaster, IdleSim_OnCommand);
        CommandSystem.Register("IdleApi", AccessLevel.GameMaster, IdleApi_OnCommand);
    }

    [Usage("Idle [stop | status | resume | list | recipes [filter] | here <task> | near <task> [count] | check <task or order> | route <task or order> | go <task or order> | at <location> [order] | smelt <amount> | craft <recipe> <amount> [material]]")]
    [Description(
        "Choose the spot your character works while you are logged out. With no argument, target the spot. " +
        "smelt/craft start an order where you stand; 'at <location>' can be followed by the same order. " +
        "'here <task>' starts the task at the nearest target within reach or says where the nearest one is; " +
        "'here smelt <amount>' and 'here craft ...' do the same for orders. 'near <task>' lists the nearest known spots. " +
        "'check <task>' (or 'check smelt <amount>', 'check craft ...') lists what is missing before going to work. " +
        "'route <task>' shows the spot you would walk to (the nearest on foot, or a safer one a little farther) and the others, with walking distance and time. " +
        "'go <task>' (or 'go smelt <amount>', 'go craft ...') walks you there and starts the task; 'go' alone shows the walk, 'stop' ends it."
    )]
    public static void Idle_OnCommand(CommandEventArgs e)
    {
        if (e.Mobile is not PlayerMobile pm)
        {
            return;
        }

        if (!IdleSystem.Settings.Enabled)
        {
            pm.SendMessage("Idle tasks are disabled.");
            return;
        }

        switch (e.Length > 0 ? e.GetString(0).ToLowerInvariant() : "")
        {
            case "":
                {
                    pm.SendMessage("Target the spot you want to work while you are away.");
                    pm.Target = new IdleSpotTarget();
                    break;
                }
            case "stop":
                {
                    // A walk in progress stops first; the task, if any, stays until the next [Idle stop.
                    if (IdleWalker.Stop(pm, "You stop walking."))
                    {
                        break;
                    }

                    pm.SendMessage(IdleSystem.StopTask(pm) ? "Your idle task has been cleared." : "You have no idle task.");
                    break;
                }
            case "go":
                {
                    Go(pm, e);
                    break;
                }
            case "status":
                {
                    SendStatus(pm);
                    break;
                }
            case "list":
                {
                    SendLocations(pm);
                    break;
                }
            case "here" when e.Length > 1:
                {
                    WorkHere(pm, e);
                    break;
                }
            case "near" when e.Length > 1:
                {
                    SendNearest(pm, e);
                    break;
                }
            case "check" when e.Length > 1:
                {
                    SendPreflight(pm, e);
                    break;
                }
            case "route" when e.Length > 1:
                {
                    SendRoutes(pm, e);
                    break;
                }
            case "at" when e.Length > 1:
                {
                    StartAt(pm, e);
                    break;
                }
            case "smelt" or "craft":
                {
                    StartHere(pm, e);
                    break;
                }
            case "resume":
                {
                    if (IdleOnlineWork.IsWorking(pm))
                    {
                        pm.SendMessage("You are already working.");
                    }
                    else if (IdleOnlineWork.TryStart(pm, out var error))
                    {
                        SendStarted(pm);
                    }
                    else
                    {
                        pm.SendMessage(0x22, error ?? "You cannot work right now.");
                    }

                    break;
                }
            case "recipes":
                {
                    SendRecipes(pm, e.Length > 1 ? e.GetString(1) : null);
                    break;
                }
            default:
                {
                    pm.SendMessage(
                        "Usage: [Idle [stop | status | resume | list | recipes [filter] | here <task> | near <task> [count] | check <task or order> | route <task or order> | go <task or order> | at <location> [order] | smelt <amount> | craft <recipe> <amount> [material]]"
                    );
                    break;
                }
        }
    }

    /// <summary>Task kind that carries out an order verb.</summary>
    private static string KindForVerb(string verb) =>
        verb switch
        {
            "smelt" => "Smelt",
            "craft" => "Craft",
            _       => null
        };

    /// <summary>Reads "smelt &lt;amount&gt;" or "craft &lt;recipe&gt; &lt;amount&gt; [material]" starting at argument <paramref name="start"/>.</summary>
    private static bool TryParseOrder(CommandEventArgs e, int start, out string verb, out IdleOrder order, out string error)
    {
        verb = e.Length > start ? e.GetString(start).ToLowerInvariant() : null;
        order = null;
        error = null;

        if (verb == null)
        {
            return true;
        }

        switch (verb)
        {
            case "smelt" when e.Length > start + 1:
                {
                    order = new IdleOrder { Requested = e.GetInt32(start + 1) };
                    break;
                }
            case "craft" when e.Length > start + 2:
                {
                    order = new IdleOrder
                    {
                        RecipeId = e.GetString(start + 1),
                        Requested = e.GetInt32(start + 2),
                        Material = e.Length > start + 3 ? e.GetString(start + 3) : null
                    };
                    break;
                }
            default:
                {
                    error = "Orders are: smelt <amount>, or craft <recipe> <amount> [material].";
                    return false;
                }
        }

        if (order.Requested is <= 0 or > 100000)
        {
            error = "The amount must be between 1 and 100000.";
            return false;
        }

        return true;
    }

    private static void StartAt(PlayerMobile pm, CommandEventArgs e)
    {
        var locationId = e.GetString(1);

        if (!TryParseOrder(e, 2, out var verb, out var order, out var error))
        {
            pm.SendMessage(0x22, error);
            return;
        }

        if (verb == null)
        {
            if (IdleSystem.TryStartTaskAt(pm, locationId, out error))
            {
                SendStarted(pm);
            }
            else
            {
                pm.SendMessage(0x22, error);
            }

            return;
        }

        if (!IdleSystem.Locations.TryGetValue(locationId, out var loc))
        {
            pm.SendMessage(0x22, "Unknown idle location.");
            return;
        }

        var kind = KindForVerb(verb);
        string firstError = null;

        foreach (var taskId in loc.AllTaskIds())
        {
            if (IdleSystem.GetTask(taskId)?.Kind.InsensitiveEquals(kind) != true)
            {
                continue;
            }

            if (IdleSystem.TryStartTaskAt(pm, locationId, out error, taskId, order))
            {
                SendStarted(pm);
                return;
            }

            firstError ??= error;
        }

        pm.SendMessage(0x22, firstError ?? "That cannot be done at that location.");
    }

    private static void StartHere(PlayerMobile pm, CommandEventArgs e)
    {
        if (!TryParseOrder(e, 0, out var verb, out var order, out var error))
        {
            pm.SendMessage(0x22, error);
            return;
        }

        var kind = KindForVerb(verb);
        var target = new IdleTarget(IdleTarget.Here, pm.Map, pm.Location);
        string firstError = null;

        foreach (var def in IdleSystem.Tasks.Values)
        {
            if (!def.Kind.InsensitiveEquals(kind))
            {
                continue;
            }

            if (IdleSystem.TryStartTask(pm, def.Id, target, out error, order))
            {
                SendStarted(pm);
                return;
            }

            firstError ??= error;
        }

        pm.SendMessage(0x22, firstError ?? "That idle task is not available.");
    }

    /// <summary>A task by id or display name, e.g. "lumberjacking" or "Lumberjacking".</summary>
    private static IdleTaskDefinition FindTask(string name)
    {
        if (IdleSystem.GetTask(name) is { } def)
        {
            return def;
        }

        foreach (var task in IdleSystem.Tasks.Values)
        {
            if (task.Name.InsensitiveEquals(name))
            {
                return task;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a task name, or an order verb with its order ("smelt 50", "craft longsword 5 iron"), from argument
    /// <paramref name="start"/>. <paramref name="pm"/> is the character the order is for; problems go to <paramref name="from"/>.
    /// </summary>
    private static bool TryResolveTask(
        Mobile from, PlayerMobile pm, CommandEventArgs e, int start, out IdleTaskDefinition def, out IdleOrder order
    )
    {
        def = null;
        order = null;

        if (e.Length <= start)
        {
            SendTaskNames(from);
            return false;
        }

        var arg = e.GetString(start).ToLowerInvariant();
        if (arg is not ("smelt" or "craft"))
        {
            def = FindTask(arg);
            if (def == null)
            {
                SendTaskNames(from);
            }

            return def != null;
        }

        if (!TryParseOrder(e, start, out var verb, out order, out var error))
        {
            from.SendMessage(0x22, error);
            return false;
        }

        def = IdlePreflight.FindTaskForOrder(pm, KindForVerb(verb), order);
        if (def == null)
        {
            from.SendMessage(0x22, "That idle task is not available.");
        }

        return def != null;
    }

    private static void SendPreflight(PlayerMobile pm, CommandEventArgs e)
    {
        if (!TryResolveTask(pm, pm, e, 1, out var def, out var order))
        {
            return;
        }

        var info = IdlePreflight.Run(pm, def.Id, order);

        if (info.Ready)
        {
            pm.SendMessage(0x3F, $"{info.TaskName}: ready to go.");
        }
        else
        {
            pm.SendMessage(0x22, $"{info.TaskName}: not ready.");
        }

        foreach (var line in info.Checks)
        {
            if (!line.Ok)
            {
                pm.SendMessage(0x22, $"  Missing - {line.Check}: {line.Message}");
            }
            else if (!line.Blocking)
            {
                pm.SendMessage(0x35, $"  Note - {line.Check}: {line.Message}");
            }
            else
            {
                pm.SendMessage(0x3B2, $"  OK - {line.Check}: {line.Message}");
            }
        }
    }

    private static void SendRoutes(PlayerMobile pm, CommandEventArgs e)
    {
        if (!TryResolveTask(pm, pm, e, 1, out var def, out _))
        {
            return;
        }

        var info = IdleApi.PickSpot(pm, def.Id);
        if (info.Status != "Ok")
        {
            pm.SendMessage(0x22, info.Message);
            return;
        }

        pm.SendMessage(0x3F, $"{def.Name}: where you would walk (distances are estimates).");
        SendRoute(pm, "Pick", info.Pick, 0x44);
        pm.SendMessage(0x44, $"  Why: {info.Message}");

        if (info.Alternatives.Count > 0)
        {
            pm.SendMessage(0x3F, "Other spots:");
        }

        var i = 0;
        foreach (var r in info.Alternatives)
        {
            SendRoute(pm, $"{++i}", r, 0x3B2);
        }
    }

    private static void Go(PlayerMobile pm, CommandEventArgs e)
    {
        if (e.Length < 2)
        {
            if (IdleWalker.Describe(pm) is { } walking)
            {
                pm.SendMessage(0x3F, walking);
            }
            else
            {
                pm.SendMessage("You are not walking anywhere. Usage: [Idle go <task>, [Idle go smelt <amount>, [Idle go craft ...");
            }

            return;
        }

        if (!TryResolveTask(pm, pm, e, 1, out var def, out var order))
        {
            return;
        }

        if (IdleWalker.TryGo(pm, def.Id, order, out var problems))
        {
            return;
        }

        pm.SendMessage(0x22, $"You cannot set off for {def.Name.ToLowerInvariant()}:");
        foreach (var problem in problems)
        {
            pm.SendMessage(0x22, $"  - {problem}");
        }
    }

    private static void SendRoute(PlayerMobile pm, string label, IdleRouteInfo r, int hue)
    {
        var s = r.Spot;
        pm.SendMessage(
            hue,
            $"{label}. {s.Name} ({s.Id}): {r.WalkTiles} tiles on foot ({s.Distance} straight {s.Direction}), " +
            $"about {IdleRouting.FormatEta(r.EtaSeconds)}.{DescribeRoute(r)}"
        );
    }

    /// <summary>" Guarded all the way.", " 120 tiles unguarded, 1 teleporter."</summary>
    private static string DescribeRoute(IdleRouteInfo r)
    {
        using var sb = ValueStringBuilder.Create();

        if (r.UnguardedTiles == 0)
        {
            sb.Append(" Guarded all the way");
        }
        else if (r.UnguardedTiles >= r.WalkTiles)
        {
            sb.Append(" Unguarded all the way");
        }
        else
        {
            sb.Append($" {r.UnguardedTiles} tiles unguarded");
        }

        if (r.Teleports > 0)
        {
            sb.Append($", {r.Teleports} teleporter{(r.Teleports == 1 ? "" : "s")}");
        }

        if (r.Spot.HostileSpawners > 0)
        {
            sb.Append($", {r.Spot.HostileSpawners} hostile spawner(s) at the spot");
        }
        else if (r.Risk == "UnguardedSpot")
        {
            sb.Append(", the spot is outside guard zones");
        }

        sb.Append('.');
        return sb.ToString();
    }

    private static void SendTaskNames(Mobile m)
    {
        using var names = ValueStringBuilder.Create();
        foreach (var id in IdleSystem.Tasks.Keys)
        {
            if (names.Length > 0)
            {
                names.Append(", ");
            }

            names.Append(id);
        }

        m.SendMessage(0x22, $"Unknown task. Tasks: {names.AsSpan()}.");
    }

    private static void WorkHere(PlayerMobile pm, CommandEventArgs e)
    {
        var arg = e.GetString(1).ToLowerInvariant();
        IdleWorkHereInfo result = null;

        if (arg is "smelt" or "craft")
        {
            if (!TryParseOrder(e, 1, out var verb, out var order, out var error))
            {
                pm.SendMessage(0x22, error);
                return;
            }

            var kind = KindForVerb(verb);
            foreach (var def in IdleSystem.Tasks.Values)
            {
                if (!def.Kind.InsensitiveEquals(kind))
                {
                    continue;
                }

                var attempt = IdleWorkHere.Run(pm, def.Id, order);

                // Started wins; otherwise keep the most useful answer (directions over a refusal).
                if (attempt.Status == "Started" || result == null || result.Status == "Failed")
                {
                    result = attempt;
                }

                if (attempt.Status == "Started")
                {
                    break;
                }
            }

            if (result == null)
            {
                pm.SendMessage(0x22, "That idle task is not available.");
                return;
            }
        }
        else
        {
            var def = FindTask(arg);
            if (def == null)
            {
                SendTaskNames(pm);
                return;
            }

            result = IdleWorkHere.Run(pm, def.Id);
        }

        SendWorkHere(pm, result);
    }

    private static void SendWorkHere(PlayerMobile pm, IdleWorkHereInfo result)
    {
        switch (result.Status)
        {
            case "Started":
                {
                    SendStarted(pm);
                    break;
                }
            case "Failed" or "NotFound":
                {
                    pm.SendMessage(0x22, result.Message);
                    break;
                }
            default:
                {
                    pm.SendMessage(0x59, result.Message);
                    break;
                }
        }
    }

    private static void SendNearest(PlayerMobile pm, CommandEventArgs e)
    {
        var def = FindTask(e.GetString(1));
        if (def == null)
        {
            SendTaskNames(pm);
            return;
        }

        var count = e.Length > 2 ? Math.Clamp(e.GetInt32(2), 1, 20) : 5;
        var spots = IdleApi.NearestSpots(pm, def.Id, count);

        if (spots.Count == 0)
        {
            pm.SendMessage($"No known {def.Name} spots on this map.");
            return;
        }

        foreach (var s in spots)
        {
            pm.SendMessage($"{s.Distance} tiles {s.Direction}: {s.Name} ({s.Id}){DescribeRisk(s)}");
        }

        pm.SendMessage($"Go to a spot and use [Idle at <id>, or [Idle here {def.Id} when you are close.");
    }

    /// <summary>Short risk labels for a spot, e.g. " - wilderness, 2 hostile spawners, not reachable on foot".</summary>
    private static string DescribeRisk(IdleSpotInfo s)
    {
        using var sb = ValueStringBuilder.Create();

        if (s.RegionKind != null)
        {
            sb.Append(" - ");
            sb.Append(
                s.RegionKind switch
                {
                    "Town"          => "town",
                    "UnguardedTown" => "unguarded town",
                    "Guarded"       => "guarded",
                    "Dungeon"       => "dungeon",
                    _               => "wilderness"
                }
            );
        }

        if (s.HostileSpawners > 0)
        {
            sb.Append($", {s.HostileSpawners} hostile spawner(s) nearby");
        }

        if (s.Reachable == false)
        {
            sb.Append(", not reachable on foot");
        }

        return sb.ToString();
    }

    private static void SendStarted(PlayerMobile pm)
    {
        var state = IdleSystem.GetState(pm);
        var def = IdleSystem.GetTask(state?.TaskId);
        if (def == null)
        {
            return;
        }

        var range = IdleTaskHandler.Get(def.Kind).GetRange(def);
        pm.SendMessage(0x59, $"Idle task set: {def.Name} at {state.Target.Location} ({state.Target.LocationId}).");

        if (state.Order != null)
        {
            pm.SendMessage(0x59, $"Order: {DescribeOrder(state.Order)}.");
        }

        if (IdleOnlineWork.IsWorking(pm))
        {
            pm.SendMessage(0x59, $"You start working. Moving more than {range} tiles away, fighting, or working by hand stops it.");
            pm.SendMessage(0x59, "Log out here and your character keeps working while you are away.");
        }
        else
        {
            pm.SendMessage(0x59, $"Log out within {range} tiles of this spot and your character will keep working while you are away.");
        }
    }

    private static void SendStatus(PlayerMobile pm)
    {
        var state = IdleSystem.GetState(pm);
        var def = IdleSystem.GetTask(state?.TaskId);

        if (def == null)
        {
            pm.SendMessage("You have no idle task. Use [Idle to choose a spot.");
            return;
        }

        pm.SendMessage($"Idle task: {def.Name} at {state.Target.Location} on {state.Target.Map} ({state.Target.LocationId}).");

        if (state.Order != null)
        {
            pm.SendMessage($"Order: {DescribeOrder(state.Order)}, {state.Order.Remaining:N0} of {state.Order.Requested:N0} left.");
        }

        var session = IdleOnlineWork.GetSession(pm);
        if (session != null)
        {
            var rate = IdleOnlineWork.GetLiveRate(pm, state, def);
            var since = IdleSummaryGump.FormatTime(Core.Now - IdleOnlineWork.GetWorkingSince(pm));
            pm.SendMessage(0x59, $"Working now for {since} at {rate:P0} speed: {session.Attempts:N0} attempts, {session.Successes:N0} successes.");

            foreach (var item in session.Items)
            {
                if (!item.Discarded)
                {
                    pm.SendMessage(0x59, $"  {item.Amount:N0} {item.Name} (pack {item.ToPack:N0}, bank {item.ToBank:N0})");
                }
            }
        }
        else
        {
            pm.SendMessage("Not working right now. [Idle resume starts again at the spot.");
        }

        var focus = IdleFocus.GetInfo(pm);
        if (focus.Active)
        {
            pm.SendMessage(0x59, $"Focus: active, {focus.SpeedBonus:P0} faster for {IdleSummaryGump.FormatTime(TimeSpan.FromSeconds(focus.SecondsLeft))} more.");
        }
        else if (focus.Enabled && focus.ReadyInSeconds > 0)
        {
            pm.SendMessage($"Focus: ready again in {IdleSummaryGump.FormatTime(TimeSpan.FromSeconds(focus.ReadyInSeconds))}.");
        }
        else if (focus.Enabled)
        {
            pm.SendMessage("Focus: ready ([Focus).");
        }

        var result = IdleSystem.Evaluate(pm, state, pm.Map, pm.Location);
        if (result == IdleLogoutResult.Eligible)
        {
            pm.SendMessage(0x59, "If you log out here, your character will keep working.");
        }
        else
        {
            pm.SendMessage(0x22, $"If you log out now: {IdleSystem.GetResultMessage(result)}");
        }
    }

    /// <summary>Recipes of every craft task, with this character's current success chance.</summary>
    private static void SendRecipes(PlayerMobile pm, string filter)
    {
        var shown = 0;

        foreach (var def in IdleSystem.Tasks.Values)
        {
            var system = def.Kind.InsensitiveEquals("Craft") ? CraftIdleHandler.GetSystem(def) : null;
            if (system == null)
            {
                continue;
            }

            pm.SendMessage(0x35, $"{def.Name} recipes ([Idle craft <id> <amount> [material]):");

            foreach (var recipe in IdleRecipes.GetRecipes(system))
            {
                if (filter != null && !recipe.Id.InsensitiveContains(filter) && !recipe.Name.InsensitiveContains(filter) &&
                    !recipe.Group.InsensitiveContains(filter))
                {
                    continue;
                }

                using var cost = ValueStringBuilder.Create();
                foreach (var res in recipe.Resources)
                {
                    if (cost.Length > 0)
                    {
                        cost.Append(", ");
                    }

                    cost.Append($"{res.Amount} {res.Name}");
                }

                var chance = Math.Clamp(recipe.CraftItem.GetSuccessChance(pm, null, system, false, out _), 0.0, 1.0);
                pm.SendMessage(
                    $"{recipe.Id}: {recipe.Name} ({recipe.Group}), {recipe.MinSkill:F1}-{recipe.MaxSkill:F1}, {cost.AsSpan()}, your chance {chance:P0}"
                );
                shown++;
            }
        }

        if (shown == 0)
        {
            pm.SendMessage("No recipes match.");
        }
    }

    public static string DescribeOrder(IdleOrder order) =>
        order.RecipeId == null
            ? $"smelt {order.Requested:N0} ore"
            : $"craft {order.Requested:N0} {order.RecipeId}{(order.Material != null ? $" from {order.Material}" : "")}";

    private static void SendLocations(PlayerMobile pm)
    {
        if (IdleSystem.Locations.Count == 0)
        {
            pm.SendMessage("There are no named idle locations.");
            return;
        }

        var generated = 0;

        foreach (var loc in IdleSystem.Locations.Values)
        {
            if (loc is IdleSpot { Generated: true })
            {
                generated++;
                continue;
            }

            using var tasks = ValueStringBuilder.Create();
            foreach (var taskId in loc.AllTaskIds())
            {
                if (tasks.Length > 0)
                {
                    tasks.Append(", ");
                }

                tasks.Append(IdleSystem.GetTask(taskId)?.Name ?? taskId);
            }

            pm.SendMessage($"{loc.Id}: {loc.Name} ({tasks.AsSpan()}) at {loc.Location} on {loc.Map}");
        }

        if (generated > 0)
        {
            pm.SendMessage($"Plus {generated:N0} generated spots across the world.");
        }
    }

    private class IdleSpotTarget : Target
    {
        public IdleSpotTarget() : base(12, true, TargetFlags.None)
        {
        }

        protected override void OnTarget(Mobile from, object targeted)
        {
            if (from is not PlayerMobile pm)
            {
                return;
            }

            string firstError = null;

            foreach (var def in IdleSystem.Tasks.Values)
            {
                var handler = IdleTaskHandler.Get(def.Kind);
                if (handler?.TryCreateTarget(pm, def, targeted, out var target) != true)
                {
                    continue;
                }

                if (IdleSystem.TryStartTask(pm, def.Id, target, out var error))
                {
                    SendStarted(pm);
                    return;
                }

                firstError ??= error;
            }

            pm.SendMessage(0x22, firstError ?? "You cannot do an idle task there.");
        }
    }

    [Usage("IdleApi <status | checkin> <character name> | near <character name> <task> [count] | here <character name> <task> | check <character name> <task or order> | route <character name> <task or order> | pick <character name> <task or order>")]
    [Description(
        "Shows exactly what a phone or web client would get for a character with an idle task, online or offline. " +
        "'checkin' settles the offline time since the character left or was last checked. 'near' and 'here' are the " +
        "spot lookup and work-here calls, from where the character is or logged out ('here' really starts the task). " +
        "'check' is the preflight checklist; 'route' the walking routes to the nearest spots; 'pick' the spot it would walk to."
    )]
    public static void IdleApi_OnCommand(CommandEventArgs e)
    {
        const string usage =
            "Usage: [IdleApi <status | checkin> <character name> | near <character name> <task> [count] | here <character name> <task> | check <character name> <task or order> | route <character name> <task or order> | pick <character name> <task or order>";

        if (e.Length < 2)
        {
            e.Mobile.SendMessage(usage);
            return;
        }

        var verb = e.GetString(0).ToLowerInvariant();
        var spotCall = verb is "near" or "here" or "check" or "route" or "pick";
        var pm = spotCall ? FindPlayer(e.GetString(1)) : IdleSystem.FindPlayerWithTask(e.GetString(1));

        if (pm == null)
        {
            e.Mobile.SendMessage(spotCall ? "No player character has that name." : "No character with an idle task has that name.");
            return;
        }

        IdleTaskDefinition task = null;
        IdleOrder order = null;
        if (spotCall && !TryResolveTask(e.Mobile, pm, e, 2, out task, out order))
        {
            return;
        }

        object result;
        switch (verb)
        {
            case "near":
                {
                    result = IdleApi.NearestSpots(pm, task.Id, e.Length > 3 ? e.GetInt32(3) : 5);
                    break;
                }
            case "here":
                {
                    result = IdleApi.WorkHere(pm, task.Id, order);
                    break;
                }
            case "check":
                {
                    result = IdleApi.Preflight(pm, task.Id, order);
                    break;
                }
            case "route":
                {
                    result = IdleApi.Routes(pm, task.Id);
                    break;
                }
            case "pick":
                {
                    result = IdleApi.PickSpot(pm, task.Id);
                    break;
                }
            case "status":
                {
                    result = IdleApi.GetStatus(pm);
                    break;
                }
            case "checkin":
                {
                    result = IdleApi.CheckIn(pm, out var message);
                    if (result == null)
                    {
                        e.Mobile.SendMessage(0x22, message ?? "Nothing to settle.");
                        return;
                    }

                    break;
                }
            default:
                {
                    e.Mobile.SendMessage(usage);
                    return;
                }
        }

        var json = System.Text.Json.JsonSerializer.Serialize(result, result.GetType());

        // Chat lines are limited in length, so the JSON is sent in pieces.
        for (var i = 0; i < json.Length; i += 200)
        {
            e.Mobile.SendMessage(0x3B2, json.Substring(i, Math.Min(200, json.Length - i)));
        }
    }

    /// <summary>A player character by name, in the world or logged out. Staff command use only.</summary>
    private static PlayerMobile FindPlayer(string name)
    {
        if (IdleSystem.FindPlayerWithTask(name) is { } withTask)
        {
            return withTask;
        }

        foreach (var account in Accounts.GetAccounts())
        {
            for (var i = 0; i < account.Length; i++)
            {
                if (account[i] is PlayerMobile pm && !pm.Deleted && pm.RawName.InsensitiveEquals(name))
                {
                    return pm;
                }
            }
        }

        return null;
    }

    [Usage("IdleSim <hours> [preview]")]
    [Description(
        "Settles <hours> of fake offline time for a targeted player's idle task and prints the exact numbers and rules. " +
        "'preview' only reports and changes nothing."
    )]
    public static void IdleSim_OnCommand(CommandEventArgs e)
    {
        var hours = e.Length > 0 ? e.GetDouble(0) : 0;

        if (hours <= 0 || hours > 1000)
        {
            e.Mobile.SendMessage("Usage: [IdleSim <hours> [preview]   (hours between 0 and 1000)");
            return;
        }

        var preview = e.Length > 1 && e.GetString(1).InsensitiveEquals("preview");
        e.Mobile.SendMessage($"Target a player to simulate {hours:0.##} hour(s) offline{(preview ? " (preview only)" : "")}.");
        e.Mobile.Target = new IdleSimTarget(TimeSpan.FromHours(hours), preview);
    }

    private class IdleSimTarget : Target
    {
        private readonly TimeSpan _away;
        private readonly bool _preview;

        public IdleSimTarget(TimeSpan away, bool preview) : base(-1, false, TargetFlags.None)
        {
            _away = away;
            _preview = preview;
        }

        protected override void OnTarget(Mobile from, object targeted)
        {
            if (targeted is not PlayerMobile pm)
            {
                from.SendMessage("That is not a player.");
                return;
            }

            var state = IdleSystem.GetState(pm);
            if (state == null)
            {
                from.SendMessage("That player has no idle task.");
                return;
            }

            // Same rules as a real logout, checked where the character stands now.
            if (pm.Map != Map.Internal)
            {
                var result = IdleSystem.Evaluate(pm, state, pm.Map, pm.Location);
                if (result != IdleLogoutResult.Eligible)
                {
                    from.SendMessage(0x22, $"Not eligible: {IdleSystem.GetResultMessage(result)}");
                    return;
                }
            }

            var mode = _preview ? IdleSettleMode.Preview : IdleSettleMode.Simulate;
            var report = IdleSystem.Settle(pm, state, _away, mode);

            if (report == null)
            {
                from.SendMessage(0x22, "That player's idle task is no longer available.");
                return;
            }

            from.SendMessage(0x35, $"--- IdleSim: {pm.Name}, {report.TaskName}, {(_preview ? "PREVIEW (nothing changed)" : "APPLIED")} ---");

            foreach (var note in report.Notes)
            {
                from.SendMessage(0x3B2, note);
            }

            foreach (var used in report.Used)
            {
                from.SendMessage(0x59, $"Used: {used.Amount:N0} {used.Name}");
            }

            foreach (var item in report.Items)
            {
                if (item.Discarded)
                {
                    from.SendMessage(0x59, $"Discarded: {item.Amount:N0} {item.Name}");
                }
                else if (_preview)
                {
                    from.SendMessage(0x59, $"Would give: {item.Amount:N0} {item.Name}");
                }
                else
                {
                    from.SendMessage(
                        0x59,
                        $"Gave: {item.Amount:N0} {item.Name} (pack {item.ToPack:N0}, bank {item.ToBank:N0}, lost {item.Lost:N0})"
                    );
                }

                if (item.Exceptional > 0)
                {
                    from.SendMessage(0x59, $"  of which exceptional: {item.Exceptional:N0}");
                }
            }

            foreach (var move in report.MovedToBank)
            {
                from.SendMessage(0x22, $"Overloaded: moved {move.Amount:N0} {move.Name} ({move.Weight} stones) from pack to bank");
            }

            if (!_preview)
            {
                from.SendMessage(
                    0x59,
                    $"Weight: {report.WeightBefore} -> {report.WeightAfter} / {report.WeightLimit}, reserve {report.KeepFreeStones} (as the status bar shows it)"
                );
            }

            foreach (var skill in report.Skills)
            {
                from.SendMessage(0x59, $"Skill: {skill.Name} {skill.From:F1} -> {skill.To:F1} (+{skill.To - skill.From:F1})");
            }

            foreach (var stat in report.Stats)
            {
                from.SendMessage(0x59, $"Stat: {stat.Name} {stat.From} -> {stat.To}");
            }

            if (report.OrderStatus != null)
            {
                from.SendMessage(0x59, $"Order: {report.OrderStatus}");
            }

            if (report.StopReason != null)
            {
                from.SendMessage(0x22, $"Stopped early: {report.StopReason}");
            }

            if (!_preview)
            {
                IdleSystem.ShowReport(pm, report);
            }
        }
    }
}
