using SwbsJailbreak;
using SwbsJailbreak.Core;
using SwbsJailbreak.Modules.Extras;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using System.Reflection;
using Xunit;

namespace SwbsJailbreak.Tests;

public class CommandCatalogTests
{
    private static IEnumerable<Type> PluginTypes() =>
        typeof(JailbreakPlugin).Assembly.GetTypes().Where(t => typeof(JbModule).IsAssignableFrom(t) || t == typeof(JailbreakPlugin));

    private static List<CommandEntry> Catalogue() => CommandCatalog.Build(PluginTypes());

    [Fact]
    public void Catalogue_ContainsTheCommandListAndCoversAllCommands()
    {
        var all = Catalogue();
        Assert.Contains(all, e => e.Name == "commands");
        // every [ConsoleCommand] method (aliases grouped) ends up in the catalogue
        var methods = PluginTypes().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Count(m => m.GetCustomAttributes<ConsoleCommandAttribute>(false).Any());
        Assert.Equal(methods, all.Count);
    }

    [Theory]
    [InlineData("beacon", "slay")]
    [InlineData("celldoors_edit", "cells.edit")]
    [InlineData("daysedit", "days.edit")]
    [InlineData("mute", "mute")]          // thin wrapper: the check lives in CommCommand
    [InlineData("timebomb", "slay")]      // thin wrapper: the check lives in BombCommand
    [InlineData("ctf_setflag", "zones")]  // thin wrapper: the check lives in SetPoint
    [InlineData("open", "cells.open")]    // admin !open / !o
    [InlineData("o", "cells.open")]
    [InlineData("cdo", "cells")]          // original root-only command is unchanged
    [InlineData("gamebuttons", "games.admin")]
    [InlineData("measure", "zones")]
    [InlineData("gap", "zones")]
    [InlineData("crewadmin", "crews.admin")]
    public void AdminCommands_AreGatedByTheirCapability(string name, string capability)
    {
        var e = Catalogue().FirstOrDefault(x => x.Name == name || x.Aliases.Contains(name));
        Assert.NotNull(e);
        Assert.Equal(capability, e!.Capability);
        Assert.True(e.IsGated);
    }

    [Theory]
    [InlineData("roll")]
    [InlineData("commands")]
    [InlineData("w")]
    [InlineData("guard")]  // body mentions the "Teams" config section: not a capability
    [InlineData("day")]    // body mentions the "Days" config section: not a capability
    [InlineData("games")]  // warden command (wardens are ordinary players); the admin override is not the gate
    [InlineData("hide")]
    [InlineData("crew")]
    [InlineData("shakedown")]
    public void PlayerCommands_AreOpenToEveryone(string name)
    {
        var e = Catalogue().FirstOrDefault(x => x.Name == name || x.Aliases.Contains(name));
        Assert.NotNull(e);
        Assert.False(e!.IsGated);
    }

    [Fact]
    public void NoCommandIsRegisteredByTwoMethods()
    {
        // Two methods with the same css_ name would both fire for one chat message.
        var dupes = PluginTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.GetCustomAttributes<ConsoleCommandAttribute>(false).Select(a => a.Command.ToLowerInvariant()).Distinct())
            .GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dupes);
    }

    [Fact]
    public void Display_UsesChatFormAndHasNoHtmlBreakingCharacters()
    {
        foreach (var e in Catalogue())
        {
            var d = e.Display();
            Assert.StartsWith("!" + e.Name, d);
            Assert.DoesNotContain('<', d);
            Assert.DoesNotContain('>', d);
            Assert.DoesNotContain('&', d);
        }
    }

    [Fact]
    public void OpenCapability_IsABasicAdminFlag_WhileTheOriginalStaysRoot()
    {
        Assert.Equal("@css/generic", Perms.Defaults["cells.open"]);
        Assert.Equal("@css/root", Perms.Defaults["cells"]);
    }
}
