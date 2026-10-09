using Bridge.Contracts;

namespace Bridge.Web;

/// <summary>
/// What the members' r_i become when the master level G of a group moves (docs/PLAN.md "Volume model", SPEC-audio.md
/// 6.3), so the Groups page can show the scaled levels live while the master slider is dragged, before the service
/// answers. Mirrors the service's <c>GroupVolumeModel</c> (Airplay2.Sender/Playback/Volume.cs): each member keeps the
/// weight r_i / G (r_i / 100 when G is 0), the new level is <c>round(weight * G')</c> away from zero, clamped to 0..100.
/// </summary>
/// <remarks>
/// When G goes to 0 the service keeps the stored member levels (it records the G they belong to and mutes the player),
/// so the preview shows them unchanged as well. When G is 0 the service scales from that stored reference level (else
/// 100); the web app does not see the reference and previews with 100. The <c>groupChanged</c> that follows carries the
/// real values.
/// </remarks>
public static class GroupVolumePreview
{
    /// <summary>One member's level at master level <paramref name="newMaster"/> &gt; 0 (the r_i scaling of GroupVolumeModel).</summary>
    public static int Scale(int memberLevel, int currentMaster, int newMaster)
    {
        memberLevel = Math.Clamp(memberLevel, 0, 100);
        newMaster = Math.Clamp(newMaster, 0, 100);
        var weight = currentMaster > 0 ? (double)memberLevel / currentMaster : memberLevel / 100.0;
        return Math.Clamp((int)Math.Round(weight * newMaster, MidpointRounding.AwayFromZero), 0, 100);
    }

    /// <summary>The members of <paramref name="group"/> with their levels at master level <paramref name="newMaster"/>.</summary>
    public static IReadOnlyList<GroupMemberDto> Preview(GroupDto group, int newMaster)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (newMaster == group.MasterLevel || newMaster <= 0)
        {
            return group.Members; // G' = 0 keeps the r_i (OutputManager.SetGroupLevelAsync)
        }

        return [.. group.Members.Select(m => m with { Level = Scale(m.Level, group.MasterLevel, newMaster) })];
    }

    /// <summary>True when some member would clamp at 100 before G reaches <paramref name="newMaster"/> (the room balance changes).</summary>
    public static bool Clamps(GroupDto group, int newMaster)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Members.Any(m =>
        {
            var weight = group.MasterLevel > 0 ? (double)m.Level / group.MasterLevel : m.Level / 100.0;
            return weight * newMaster > 100.5;
        });
    }
}
