using System.Globalization;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

public sealed class P709CallbackTextTests
{
    private sealed class Site : IIgcSite
    {
        public readonly List<(bool Critical, string Text)> Text = [];
        public readonly List<(ChatTarget Target, short Recipient, short Voice, string Text, short Command, short Type)> Chats = [];
        public void PostPlainText(bool bCritical, string pszText) => Text.Add((bCritical, pszText));
        public void SendChat(IShipIgc? sender, ChatTarget target, short recipient, short voice, string text,
            short cid = -1, Allegiance.Core.IgcObjectType type = (Allegiance.Core.IgcObjectType)(-1),
            short id = -1, IModelIgc? model = null, bool objectModel = false) =>
            Chats.Add((target, recipient, voice, text, cid, (short)type));
    }

    [Fact]
    public void Formatted_helpers_dispatch_virtual_callbacks_once_with_native_defaults()
    {
        var site = new Site(); IIgcSite callback = site;
        callback.SendChatf(null!, ChatTarget.Team, 2, 41, "{0}: ${1}", "Miner", 120);
        Assert.Equal((ChatTarget.Team, (short)2, (short)41, "Miner: $120", (short)-1, (short)-1), Assert.Single(site.Chats));
        callback.PostText(true, "A {0} {{literal}} %s", 7);
        Assert.Equal((true, "A 7 {literal} %s"), Assert.Single(site.Text));
    }

    [Theory]
    [InlineData(254, 254)] [InlineData(255, 255)] [InlineData(256, 255)] [InlineData(1000, 255)]
    public void Formatting_reserves_a_terminator_at_the_native_buffer_boundary(int input, int expected) =>
        Assert.Equal(new string('x', expected), CallbackText.Format("{0}", new string('x', input)));

    [Fact]
    public void Formatting_is_invariant_and_does_not_split_a_unicode_pair()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("1.50", CallbackText.Format("{0:F2}", 1.5));
            Assert.Equal(new string('x', 254), CallbackText.Format("{0}", new string('x', 254) + "🚀!"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
