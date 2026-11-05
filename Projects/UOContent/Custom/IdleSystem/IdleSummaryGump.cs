using System;
using Server.Gumps;
using Server.Mobiles;
using Server.Text;

namespace Server.Custom.Idle;

/// <summary>The "while you were away" window shown after an offline settlement.</summary>
public class IdleSummaryGump : DynamicGump
{
    private const int Width = 420;
    private const int Height = 420;

    private readonly IdleReport _report;

    public override bool Singleton => true;

    private IdleSummaryGump(IdleReport report) : base(120, 80) => _report = report;

    public static void DisplayTo(PlayerMobile pm, IdleReport report)
    {
        if (pm?.NetState == null || report == null)
        {
            return;
        }

        pm.SendGump(new IdleSummaryGump(report));
    }

    /// <summary>Chat-log copy of the summary, so it survives the gump being closed.</summary>
    public static void SendText(PlayerMobile pm, IdleReport report)
    {
        if (pm?.NetState == null || report == null)
        {
            return;
        }

        pm.SendMessage(
            0x59,
            $"While you were away ({report.TaskName}{(report.Simulated ? ", simulated" : "")}): {FormatTime(report.Counted)} counted, {report.Attempts:N0} attempts, {report.Successes:N0} successes."
        );

        foreach (var item in report.Items)
        {
            pm.SendMessage(0x59, $"  {item.Amount:N0} {item.Name} (pack {item.ToPack:N0}, bank {item.ToBank:N0}, lost {item.Lost:N0})");
        }

        foreach (var skill in report.Skills)
        {
            pm.SendMessage(0x59, $"  {skill.Name}: {skill.From:F1} -> {skill.To:F1}");
        }

        foreach (var stat in report.Stats)
        {
            pm.SendMessage(0x59, $"  {stat.Name}: {stat.From} -> {stat.To}");
        }

        if (report.StopReason != null)
        {
            pm.SendMessage(0x22, $"  Stopped early: {report.StopReason}");
        }
    }

    protected override void BuildLayout(ref DynamicGumpBuilder builder)
    {
        builder.AddPage();
        builder.AddBackground(0, 0, Width, Height, 9270);
        builder.AddAlphaRegion(15, 15, Width - 30, Height - 30);

        builder.AddHtml(20, 22, Width - 40, 20, "While you were away", "#FFD700", align: TextAlignment.Center);

        using var sb = ValueStringBuilder.Create(1024);
        var r = _report;

        sb.Append($"<BASEFONT COLOR=#FFFFFF><B>Task:</B> {r.TaskName}");
        if (r.Simulated)
        {
            sb.Append(" <I>(staff simulation)</I>");
        }

        sb.Append($"<BR><B>Time away:</B> {FormatTime(r.Away)}");
        if (r.Capped)
        {
            sb.Append($"<BR><B>Time counted:</B> {FormatTime(r.Counted)} (maximum reached)");
        }

        sb.Append($"<BR><B>Attempts:</B> {r.Attempts:N0}, <B>Successes:</B> {r.Successes:N0}<BR><BR>");

        sb.Append("<B>Items gained</B><BR>");
        if (r.Items.Count == 0)
        {
            sb.Append("- Nothing<BR>");
        }

        foreach (var item in r.Items)
        {
            sb.Append($"- {item.Amount:N0} {item.Name}");
            if (item.ToBank > 0)
            {
                sb.Append($" - {item.ToBank:N0} sent to bank");
            }

            if (item.Lost > 0)
            {
                sb.Append($" - <BASEFONT COLOR=#FF6060>{item.Lost:N0} lost (no room)</BASEFONT><BASEFONT COLOR=#FFFFFF>");
            }

            sb.Append("<BR>");
        }

        sb.Append("<BR><B>Skill gains</B><BR>");
        if (r.Skills.Count == 0)
        {
            sb.Append("- None<BR>");
        }

        foreach (var skill in r.Skills)
        {
            sb.Append($"- {skill.Name}: {skill.From:F1} -> {skill.To:F1} (+{skill.To - skill.From:F1})<BR>");
        }

        foreach (var stat in r.Stats)
        {
            sb.Append($"- {stat.Name}: {stat.From} -> {stat.To}<BR>");
        }

        if (r.StopReason != null)
        {
            sb.Append($"<BR><BASEFONT COLOR=#FF6060>Stopped early: {r.StopReason}</BASEFONT>");
        }

        builder.AddHtml(25, 50, Width - 50, Height - 110, sb.AsSpan(), background: false, scrollbar: true);

        builder.AddButton(Width / 2 - 30, Height - 45, 247, 248, 0); // OKAY; button 0 simply closes
    }

    public static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}h {time.Minutes}m" : $"{time.Minutes}m {time.Seconds}s";
}
