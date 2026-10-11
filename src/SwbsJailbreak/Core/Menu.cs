using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Menu;

namespace SwbsJailbreak.Core;

public sealed class JbMenuItem
{
    public required string Text { get; set; }
    public Action<CCSPlayerController>? OnSelect { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Keep the menu open (re-rendered) after this item is chosen.</summary>
    public bool KeepOpen { get; set; }
}

/// <summary>
/// Replacement for SourceMod menus (Menu/Panel). Rendered in the centre HUD and controlled with
///  - chat/console number keys !1 .. !9 (CSS's built-in css_1..css_9 commands) : 1-6 items, 7 back/prev, 8 next, 9 exit
///  - optionally W/S to move a cursor, E (+use) to select, R (+reload) to go back / exit.
/// </summary>
public sealed class JbMenu
{
    public JbMenu(string title)
    {
        Title = title;
    }

    public string Title { get; set; }
    public List<JbMenuItem> Items { get; } = new();
    public bool ExitButton { get; set; } = true;

    /// <summary>When set, key 7 on the first page / R returns to this menu.</summary>
    public JbMenu? Parent { get; set; }

    /// <summary>Seconds of inactivity before the menu auto-closes (0 = config default, &lt;0 = never).</summary>
    public int Timeout { get; set; }

    /// <summary>Called when the menu closes without a selection (exit, timeout, replaced).</summary>
    public Action<CCSPlayerController>? OnCancel { get; set; }

    /// <summary>Optional line under the title (e.g. instructions).</summary>
    public string? Subtitle { get; set; }

    public JbMenuItem Add(string text, Action<CCSPlayerController>? onSelect, bool enabled = true, bool keepOpen = false)
    {
        var item = new JbMenuItem { Text = text, OnSelect = onSelect, Enabled = enabled, KeepOpen = keepOpen };
        Items.Add(item);
        return item;
    }

    /// <summary>Disabled informational line.</summary>
    public JbMenuItem AddInfo(string text) => Add(text, null, false);

    public void Open(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || player.IsBot) return;
        MenuService.Open(player, this);
    }
}

/// <summary>Active menu state for one player. Registered with CSS's MenuManager so css_1..css_9 reach it.</summary>
internal sealed class JbMenuInstance : IMenuInstance
{
    public const int ItemsPerPage = 6;

    private static readonly IMenu DummyMenu = new ChatMenu("");

    public JbMenuInstance(CCSPlayerController player, JbMenu menu)
    {
        Player = player;
        JbMenu = menu;
        LastInput = Server.CurrentTime;
    }

    public CCSPlayerController Player { get; }
    public JbMenu JbMenu { get; }
    public int PageIndex { get; set; }
    public int Cursor { get; set; } // absolute item index
    public bool Closed { get; set; }
    public float LastInput { get; set; }

    public int PageCount => Math.Max(1, (JbMenu.Items.Count + ItemsPerPage - 1) / ItemsPerPage);

    // ---- IMenuInstance (explicit, protected members) ----
    IMenu IMenuInstance.Menu => DummyMenu;
    CCSPlayerController? IMenuInstance.Player => Player;
    bool IMenuInstance.CloseOnSelect => true;
    int IMenuInstance.Page => PageIndex;
    int IMenuInstance.CurrentOffset => PageIndex * ItemsPerPage;
    int IMenuInstance.NumPerPage => ItemsPerPage;
    Stack<int> IMenuInstance.PrevPageOffsets { get; } = new();

    public void NextPage()
    {
        if (PageIndex + 1 < PageCount)
        {
            PageIndex++;
            Cursor = PageIndex * ItemsPerPage;
        }
    }

    public void PrevPage()
    {
        if (PageIndex > 0)
        {
            PageIndex--;
            Cursor = PageIndex * ItemsPerPage;
        }
    }

    /// <summary>Called by CSS when another menu replaces ours.</summary>
    public void Reset()
    {
        if (Closed) return;
        Closed = true;
        MenuService.OnInstanceReset(this);
    }

    void IMenuInstance.Close() => MenuService.Close(Player);

    public void Display() => MenuService.Render(this, true);

    public void OnKeyPress(CCSPlayerController player, int key) => MenuService.HandleKey(this, key);
}

/// <summary>Owns menu instances, renders them into the HUD and handles input.</summary>
public static class MenuService
{
    private static readonly JbMenuInstance?[] Active = new JbMenuInstance?[Players.MaxSlots];

    public static bool HasMenu(CCSPlayerController? player) =>
        player != null && player.IsValid && Active[player.Slot] is { Closed: false };

    public static JbMenu? Current(CCSPlayerController? player) =>
        player != null && player.IsValid ? Active[player.Slot] is { Closed: false } i ? i.JbMenu : null : null;

    public static void Open(CCSPlayerController player, JbMenu menu)
    {
        // Replace any CSS menu or previous JbMenu without firing its cancel callback twice.
        var prev = Active[player.Slot];
        if (prev != null)
        {
            prev.Closed = true;
            Active[player.Slot] = null;
        }

        MenuManager.CloseActiveMenu(player);
        var inst = new JbMenuInstance(player, menu);
        Active[player.Slot] = inst;
        MenuManager.GetActiveMenus()[player.Handle] = inst;
        Render(inst, true);
        if (JailbreakPlugin.Instance?.Config.General.MenuChatEcho == true) EchoToChat(inst);
    }

    public static void Close(CCSPlayerController? player, bool cancelled = true)
    {
        if (player == null || !player.IsValid) return;
        var inst = Active[player.Slot];
        if (inst == null) return;
        inst.Closed = true;
        Active[player.Slot] = null;
        if (MenuManager.GetActiveMenus().TryGetValue(player.Handle, out var m) && ReferenceEquals(m, inst))
            MenuManager.GetActiveMenus().Remove(player.Handle);
        Hud.Send(player, Hud.Compose(player), true);
        if (cancelled) SafeInvoke(inst.JbMenu.OnCancel, player);
    }

    internal static void OnInstanceReset(JbMenuInstance inst)
    {
        var slot = inst.Player.IsValid ? inst.Player.Slot : -1;
        if (slot >= 0 && ReferenceEquals(Active[slot], inst)) Active[slot] = null;
        if (inst.Player.IsValid) SafeInvoke(inst.JbMenu.OnCancel, inst.Player);
    }

    internal static void ResetSlot(int slot)
    {
        if (slot is >= 0 and < Players.MaxSlots) Active[slot] = null;
    }

    internal static void HandleKey(JbMenuInstance inst, int key)
    {
        if (inst.Closed || !inst.Player.IsValid) return;
        inst.LastInput = Server.CurrentTime;
        var menu = inst.JbMenu;
        switch (key)
        {
            case 7:
                if (inst.PageIndex > 0) inst.PrevPage();
                else if (menu.Parent != null) GoBack(inst);
                Render(inst, true);
                return;
            case 8:
                inst.NextPage();
                Render(inst, true);
                return;
            case 9:
                if (menu.ExitButton) Close(inst.Player);
                return;
        }

        var index = inst.PageIndex * JbMenuInstance.ItemsPerPage + key - 1;
        if (key is < 1 or > JbMenuInstance.ItemsPerPage || index < 0 || index >= menu.Items.Count) return;
        Select(inst, index);
    }

    private static void GoBack(JbMenuInstance inst)
    {
        var parent = inst.JbMenu.Parent;
        if (parent == null)
        {
            Close(inst.Player);
            return;
        }

        Open(inst.Player, parent);
    }

    private static void Select(JbMenuInstance inst, int index)
    {
        var item = inst.JbMenu.Items[index];
        if (!item.Enabled || item.OnSelect == null) return;
        var player = inst.Player;
        if (!item.KeepOpen)
        {
            // Close first so the callback may open another menu.
            inst.Closed = true;
            Active[player.Slot] = null;
            if (MenuManager.GetActiveMenus().TryGetValue(player.Handle, out var m) && ReferenceEquals(m, inst))
                MenuManager.GetActiveMenus().Remove(player.Handle);
        }

        SafeInvoke(item.OnSelect, player);
        if (item.KeepOpen && !inst.Closed) Render(inst, true);
        else if (!HasMenu(player)) Hud.Send(player, Hud.Compose(player), true);
    }

    /// <summary>WASD navigation, driven by OnPlayerButtonsChanged from the core.</summary>
    internal static void OnButtons(CCSPlayerController player, PlayerButtons pressed)
    {
        var inst = Active[player.Slot];
        if (inst == null || inst.Closed) return;
        var cfg = JailbreakPlugin.Instance?.Config.General;
        if (cfg is { MenuWasd: false }) return;
        var menu = inst.JbMenu;
        var count = menu.Items.Count;
        if ((pressed & PlayerButtons.Forward) != 0 && count > 0)
        {
            inst.Cursor = (inst.Cursor - 1 + count) % count;
            inst.PageIndex = inst.Cursor / JbMenuInstance.ItemsPerPage;
            inst.LastInput = Server.CurrentTime;
            Render(inst, true);
        }
        else if ((pressed & PlayerButtons.Back) != 0 && count > 0)
        {
            inst.Cursor = (inst.Cursor + 1) % count;
            inst.PageIndex = inst.Cursor / JbMenuInstance.ItemsPerPage;
            inst.LastInput = Server.CurrentTime;
            Render(inst, true);
        }
        else if ((pressed & PlayerButtons.Use) != 0 && count > 0)
        {
            inst.LastInput = Server.CurrentTime;
            Select(inst, Math.Clamp(inst.Cursor, 0, count - 1));
        }
        else if ((pressed & PlayerButtons.Reload) != 0)
        {
            if (menu.Parent != null) GoBack(inst);
            else if (menu.ExitButton) Close(player);
        }
    }

    /// <summary>Per-tick upkeep: timeouts + periodic re-render (HUD messages expire after ~1s).</summary>
    internal static void Tick()
    {
        var cfg = JailbreakPlugin.Instance?.Config.General;
        var defaultTimeout = cfg?.MenuTimeoutSeconds ?? 30;
        var now = Server.CurrentTime;
        for (var slot = 0; slot < Players.MaxSlots; slot++)
        {
            var inst = Active[slot];
            if (inst == null) continue;
            if (inst.Closed || !inst.Player.IsValid)
            {
                Active[slot] = null;
                continue;
            }

            var timeout = inst.JbMenu.Timeout == 0 ? defaultTimeout : inst.JbMenu.Timeout;
            if (timeout > 0 && now - inst.LastInput > timeout)
            {
                Close(inst.Player);
                continue;
            }

            Render(inst, false);
        }
    }

    internal static void Render(JbMenuInstance inst, bool force)
    {
        if (inst.Closed || !inst.Player.IsValid) return;
        Hud.Send(inst.Player, BuildHtml(inst), force);
    }

    private static string BuildHtml(JbMenuInstance inst)
    {
        var menu = inst.JbMenu;
        var wasd = JailbreakPlugin.Instance?.Config.General.MenuWasd ?? true;
        var sb = new System.Text.StringBuilder();
        sb.Append("<font color='#FFD700'><b>").Append(menu.Title).Append("</b></font>");
        if (inst.PageCount > 1) sb.Append($" <font color='#AAAAAA' class='fontSize-s'>({inst.PageIndex + 1}/{inst.PageCount})</font>");
        sb.Append("<br>");
        if (!string.IsNullOrEmpty(menu.Subtitle))
            sb.Append("<font color='#CCCCCC' class='fontSize-s'>").Append(menu.Subtitle).Append("</font><br>");

        var start = inst.PageIndex * JbMenuInstance.ItemsPerPage;
        var end = Math.Min(start + JbMenuInstance.ItemsPerPage, menu.Items.Count);
        for (var i = start; i < end; i++)
        {
            var item = menu.Items[i];
            var num = i - start + 1;
            var selected = wasd && i == inst.Cursor;
            var color = !item.Enabled ? "#777777" : selected ? "#00FF7F" : "#FFFFFF";
            sb.Append($"<font color='{color}'>");
            sb.Append(selected ? "&#9658; " : "");
            if (item.Enabled && item.OnSelect != null) sb.Append($"{num}. ");
            sb.Append(item.Text).Append("</font><br>");
        }

        var footer = new List<string>();
        if (inst.PageIndex > 0 || menu.Parent != null) footer.Add("7.Back");
        if (inst.PageIndex + 1 < inst.PageCount) footer.Add("8.Next");
        if (menu.ExitButton) footer.Add("9.Exit");
        var keys = string.Join(" ", footer);
        var hint = wasd ? "W/S move, E select, R back" : "type !1-!9";
        sb.Append($"<font color='#AAAAAA' class='fontSize-s'>{keys} | {hint}</font>");
        return sb.ToString();
    }

    private static void EchoToChat(JbMenuInstance inst)
    {
        var menu = inst.JbMenu;
        Chat.Print(inst.Player, $"{{gold}}{Colors.Strip(StripHtml(menu.Title))}", false);
        var start = inst.PageIndex * JbMenuInstance.ItemsPerPage;
        var end = Math.Min(start + JbMenuInstance.ItemsPerPage, menu.Items.Count);
        for (var i = start; i < end; i++)
        {
            var it = menu.Items[i];
            Chat.Print(inst.Player, $"{(it.Enabled ? "{green}" : "{grey}")}!{i - start + 1}{{default}} {StripHtml(it.Text)}", false);
        }
    }

    private static string StripHtml(string s) => System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", "");

    private static void SafeInvoke(Action<CCSPlayerController>? action, CCSPlayerController player)
    {
        if (action == null) return;
        try
        {
            action(player);
        }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(JailbreakPlugin.Instance!.Logger, ex, "Menu callback failed");
        }
    }
}
