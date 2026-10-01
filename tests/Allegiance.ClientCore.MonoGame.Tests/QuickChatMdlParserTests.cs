using Allegiance.ClientCore.MonoGame.QuickChat;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Unit tests for <see cref="QuickChatMdlParser"/> — the P217 quick-chat
/// (SonicChat) voice-macro data parser. These are the exact kind of non-trivial,
/// pure (no MonoGame/graphics) logic this project's discipline expects tested:
/// given real content from the original's own <c>quickchat.mdl</c>/<c>sounddef.mdl</c>,
/// the parser must yield the correct menu tree and index→caption→sound table.
/// </summary>
public class QuickChatMdlParserTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────
    // Real declarations lifted verbatim from /home/domingo/Development/Artwork/
    // quickchat.mdl (same construct patterns, real symbols and real values),
    // including the real sonicChats wire-index numbering (7=attackFighter,
    // 9=attackBase, …, 15=Yes, 16=No) and a commented-out entry to prove comment
    // handling.

    private const string QuickchatSample = """
use "gamepanes";
use "quickchatheader";
use "sounddef";

ttFriendly = 0x0001;
ttEnemy    = 0x0002;
ttNeutral  = 0x0004;
ttShip     = 0x0008;
ttStation  = 0x0010;
ttNearest  = 0x0800;

cidNone = -1;
cidAttack = 1;
cidGoto = 5;

  attackFighterSonicChat = SonicChat([ SonicChatVoice(voAttackSound, "Attack enemy fighter") ]);
    attackBaseSonicChat = SonicChat([ SonicChatVoice(voAttackSound, "Attack enemy base") ]);
   attackTargetSonicChat = SonicChat([ SonicChatVoice(voAttackTargetSound, "Attack my target!") ]);
        helpMeSonicChat = SonicChat([ SonicChatVoice(voNeedHelpSound, "I need help!") ]);
      followMeSonicChat = SonicChat([ SonicChatVoice(voFollowSound, "Follow me!") ]);
           YesSonicChat = SonicChat([ SonicChatVoice(voAffirmativeSound, "Affirmative!") ]);
            NoSonicChat = SonicChat([ SonicChatVoice(voNegativeSound, "Negative!") ]);
        missingFileChat = SonicChat([ SonicChatVoice(voMissingSound, "A voice with no wave") ]);

sonicChats =
    [
         ( 7,  attackFighterSonicChat),
         ( 9,     attackBaseSonicChat),
         (10,   attackTargetSonicChat),
         (11,         helpMeSonicChat),
         (12,       followMeSonicChat),
         (15,            YesSonicChat),
         (16,             NoSonicChat),
         (99,        missingFileChat),
//       (235,            youSuckSonicChat),   // commented out — must be excluded
    ];

attackFighterCommand = QuickChatCommand( attackFighterSonicChat, cidAttack, ttEnemy | ttShip | ttNearest, habmFighter);
    attackBaseCommand = QuickChatCommand(    attackBaseSonicChat, cidAttack, ttEnemy | ttStation | ttNearest, 0);
  attackTargetCommand = QuickChatCommand(  attackTargetSonicChat, cidAttack, ttMyTarget, 0);
        helpMeCommand = QuickChatCommand(        helpMeSonicChat, cidAttack, ttWorstEnemy, 0);
      followMeCommand = QuickChatCommand(      followMeSonicChat, cidGoto, ttMe, 0);
           YesCommand = QuickChatCommand(           YesSonicChat, cidNone, 0, 0);
            NoCommand = QuickChatCommand(            NoSonicChat, cidNone, 0, 0);

attackMenu =
    QuickChatMenu([
        QuickChatMenuItem('f', "Attack fighters.", attackFighter2Command),
//      QuickChatMenuItem('d', "Attack defenders.", attackDefenderCommand),
        QuickChatMenuItem('b', "Attack base." , attackBaseCommand)
    ]);

mainMenu =
    QuickChatMenu([
        QuickChatMenuItem('1', "Affirmative", YesCommand),
        QuickChatMenuItem('2', "Negative", NoCommand),
        QuickChatMenuItem('5', "Attack my target", attackTargetCommand),
        QuickChatMenuItem('6', "I need help!", helpMeCommand),
        QuickChatMenuItem('A', "ATTACK...", attackMenu)
    ]);
""";

    private const string SounddefSample = """
voAttackSound = SerializedSound(MutexVO, DefaultVOTimeout, 20, true, GainSound(VoiceOverGain, ImportWave("vo_player_attack")));
voAttackTargetSound = SerializedSound(MutexVO, DefaultVOTimeout, 20, true, GainSound(VoiceOverGain, ImportWave("vo_player_attacktarget")));
voNeedHelpSound = SerializedSound(MutexVO, DefaultVOTimeout, 20, true, GainSound(VoiceOverGain, ImportWave("vo_player_needhelp")));
voFollowSound = SerializedSound(MutexVO, DefaultVOTimeout, 20, true, GainSound(VoiceOverGain, ImportWave("vo_player_follow")));
voAffirmativeSound = SerializedSound(MutexVO, DefaultVOTimeout, 20, true, GainSound(VoiceOverGain, ImportWave("vo_player_affirmative")));
voNegativeSound = SerializedSound(MutexVO, DefaultVOTimeout, 20, true, GainSound(VoiceOverGain, ImportWave("vo_player_negative")));
""";

    private static QuickChatDatabase ParseSample() =>
        QuickChatMdlParser.Parse(QuickchatSample, SounddefSample);

    // ── SonicChat table (wire-index → entry) ───────────────────────────────

    [Fact]
    public void Parses_WireIndicesIntoEntries()
    {
        var db = ParseSample();
        // Every explicit sonicChats index becomes a key, preserving the real
        // (non-contiguous) numbering rather than folding to list position.
        Assert.Equal(8, db.EntriesByIndex.Count);
        Assert.Equal("Affirmative!", db.ByVoiceOver(15)!.Caption);
        Assert.Equal("Negative!", db.ByVoiceOver(16)!.Caption);
    }

    [Fact]
    public void Resolves_SoundIdentifier_ToRealAssetStem_ThroughSounddef()
    {
        var db = ParseSample();
        // voAffirmativeSound -> ImportWave("vo_player_affirmative").
        Assert.Equal("vo_player_affirmative", db.ByVoiceOver(15)!.SoundStem);
        Assert.Equal("vo_player_attack", db.ByVoiceOver(7)!.SoundStem);
        Assert.Equal("vo_player_needhelp", db.ByVoiceOver(11)!.SoundStem);
    }

    [Fact]
    public void UnknownSoundIdentifier_YieldsEmptyStem()
    {
        // missingFileChat references voMissingSound which sounddef does not
        // define — the entry still exists (caption shows) but has no sound.
        var e = ParseSample().ByVoiceOver(99)!;
        Assert.Equal("A voice with no wave", e.Caption);
        Assert.Equal("", e.SoundStem);
    }

    [Fact]
    public void CommentedOutSonicChatsEntry_IsExcluded()
    {
        // youSuckSonicChat (index 235) is commented out in the sonicChats list
        // and is not otherwise defined here, so it must not appear.
        Assert.Null(ParseSample().ByVoiceOver(235));
    }

    [Fact]
    public void UnknownVoiceOverIndex_ReturnsNull()
    {
        Assert.Null(ParseSample().ByVoiceOver(123456));
        Assert.Null(ParseSample().ByVoiceOver(-1));   // NA / no-voice
    }

    // ── Menu tree ───────────────────────────────────────────────────────────

    [Fact]
    public void MainMenu_TopLevelQuickItems_AreLeafCommands()
    {
        var mm = ParseSample().MainMenu!;
        Assert.Equal(5, mm.Choices.Count);

        var affirm = mm.ChoiceFor('1')!;
        Assert.Equal("Affirmative", affirm.Label);
        Assert.Null(affirm.Submenu);
        var send = affirm.Send!;
        Assert.Equal(15, send.Entry.Index);
        Assert.Equal("Affirmative!", send.Entry.Caption);   // sent text = sonic caption
        Assert.Equal(ChatTarget.Team, send.Target);
    }

    [Fact]
    public void MainMenu_SubmenuItem_DescendsNotSends()
    {
        var mm = ParseSample().MainMenu!;
        var att = mm.ChoiceFor('A')!;
        Assert.Equal("ATTACK...", att.Label);
        Assert.Null(att.Send);
        Assert.Equal("attackMenu", att.Submenu!.Name);
        Assert.Equal(2, att.Submenu.Choices.Count);   // commented 'd' excluded
    }

    [Fact]
    public void Descending_ThenLeaf_SendsTheCommandsOwnSonicChat()
    {
        var mm = ParseSample().MainMenu!;
        var attack = mm.ChoiceFor('A')!.Submenu!;
        var baseChoice = attack.ChoiceFor('b')!;
        var send = baseChoice.Send!;
        Assert.Equal("Attack enemy base", send.Entry.Caption);
        Assert.Equal(9, send.Entry.Index);
        Assert.Equal(ChatTarget.Team, send.Target);
    }

    [Fact]
    public void EveryLeafCommand_MapsToTeamTarget()
    {
        // Per the documented collapse (QuickChatTarget), every leaf command —
        // command-targeted (ttEnemy|ttShip) or social (tt 0) — resolves to Team.
        var db = ParseSample();
        var all = db.MenusByName.Values.SelectMany(m => m.Choices).Where(c => c.Send is not null);
        Assert.All(all, c => Assert.Equal(ChatTarget.Team, c.Send!.Target));
    }

    [Fact]
    public void MenuSymbol_AssignedOnPrecedingLine_IsResolved()
    {
        // Menu names sit on the line ABOVE "QuickChatMenu([" (e.g. "mainMenu =").
        var db = ParseSample();
        Assert.Contains("mainMenu", db.MenusByName.Keys);
        Assert.Contains("attackMenu", db.MenusByName.Keys);
        Assert.Same(db.MainMenu, db.MenusByName["mainMenu"]);
    }

    // ── Real shipped dataset (regression guard against content/parser drift) ──

    [Fact]
    public void Parses_TheRealShippedDataset_ToKnownInvariants()
    {
        if (FindRepoContent() is not { } contentDir)
            return;   // not running from the repo tree — skip the repo-coupled check

        var qc = File.ReadAllText(Path.Combine(contentDir, "quickchat.mdl"));
        var sd = File.ReadAllText(Path.Combine(contentDir, "sounddef.mdl"));
        var db = QuickChatMdlParser.Parse(qc, sd);

        Assert.Equal(310, db.EntriesByIndex.Count);          // real sonicChats count
        Assert.Equal(13, db.MenusByName.Count);              // mainMenu + 12 submenus
        var mm = db.MainMenu!;
        Assert.Equal(22, mm.Choices.Count);                  // 10 quick items + 12 submenus
        Assert.Equal(15, mm.ChoiceFor('1')!.Send!.Entry.Index);
        Assert.Equal("Affirmative!", mm.ChoiceFor('1')!.Send!.Entry.Caption);
        Assert.Equal("vo_player_affirmative", mm.ChoiceFor('1')!.Send!.Entry.SoundStem);
        Assert.Equal("attackMenu", mm.ChoiceFor('A')!.Submenu!.Name);
        Assert.Equal("vo_player_attack", db.ByVoiceOver(7)!.SoundStem);
        Assert.Equal("Attack enemy fighter", db.ByVoiceOver(7)!.Caption);
        // The two real voices whose .ogg does not exist are still resolved to
        // their (unshippable) stems — captions remain, audio gracefully absent.
        Assert.Equal("vo_player_ISPdrop", db.ByVoiceOver(190)!.SoundStem);
        Assert.Equal("vo_ma_woopie", db.ByVoiceOver(369)!.SoundStem);
    }

    private static string? FindRepoContent()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = dir; d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "src", "Allegiance.ClientCore.MonoGame", "Content");
            if (File.Exists(Path.Combine(candidate, "quickchat.mdl"))
                && File.Exists(Path.Combine(candidate, "sounddef.mdl")))
                return candidate;
        }
        return null;
    }

    // ── P457 command resolution (cid / ttMask parsed from the mdl's constants) ──
    // A compact standalone sample declaring the cid*/tt*/habm* constants PLUS the
    // three commands the P457 brief pins (attackTarget / defendBase / followMe)
    // wired into a menu so each yields a send action. Exercises the parser
    // resolving the command's cid and target-type mask from the file's own scalar
    // constants (never hand-copied numbers).

    private const string CommandResolutionSample = """
ttFriendly  = 0x0001;
ttEnemy     = 0x0002;
ttShip      = 0x0008;
ttStation   = 0x0010;
ttNearest   = 0x0800;
ttMyTarget  = 0x80002;
ttWorstEnemy = 0x80003;
ttMe        = 0x80004;
cidNone = -1;
cidAttack = 1;
cidDefend = 3;
cidGoto   = 5;
cidMyAccepted = -2;
habmFighter = 0x80;

  attackTargetSonicChat = SonicChat([ SonicChatVoice(voAttackTargetSound, "Attack my target!") ]);
   defendBaseSonicChat = SonicChat([ SonicChatVoice(voDefendSound, "Defend the base!") ]);
     followMeSonicChat = SonicChat([ SonicChatVoice(voFollowSound, "Follow me!") ]);

sonicChats =
    [
        (10,  attackTargetSonicChat),
        (11,    defendBaseSonicChat),
        (12,      followMeSonicChat),
    ];

attackTargetCommand = QuickChatCommand(attackTargetSonicChat, cidAttack, ttMyTarget, 0);
  defendBaseCommand = QuickChatCommand(defendBaseSonicChat, cidDefend, ttFriendly | ttStation | ttNearest, 0);
    followMeCommand = QuickChatCommand(followMeSonicChat, cidGoto, ttMe, 0);

mainMenu =
    QuickChatMenu([
        QuickChatMenuItem('5', "Attack my target!", attackTargetCommand),
        QuickChatMenuItem('6', "Defend the base!", defendBaseCommand),
        QuickChatMenuItem('7', "Follow me!", followMeCommand)
    ]);
""";

    private const string CommandResolutionSounddef = """
voAttackTargetSound = SerializedSound(MutexVO, DefaultVOTimeout, 20, true, GainSound(VoiceOverGain, ImportWave("vo_player_attacktarget")));
voDefendSound = SerializedSound(MutexVO, DefaultVOTimeout, 20, true, GainSound(VoiceOverGain, ImportWave("vo_player_defend")));
voFollowSound = SerializedSound(MutexVO, DefaultVOTimeout, 20, true, GainSound(VoiceOverGain, ImportWave("vo_player_follow")));
""";

    [Fact]
    public void Parses_cid_and_ttMask_into_the_send_action()
    {
        var db = QuickChatMdlParser.Parse(CommandResolutionSample, CommandResolutionSounddef);
        var mm = db.MainMenu!;

        // attackTargetCommand: cidAttack (1), ttMyTarget (0x80002).
        var attack = mm.ChoiceFor('5')!.Send!;
        Assert.Equal(1, attack.CommandId);                                      // cidAttack
        Assert.Equal(0x80002L, attack.TargetTypeMask);                          // ttMyTarget

        // defendBaseCommand: cidDefend (3), ttFriendly|ttStation|ttNearest.
        var defend = mm.ChoiceFor('6')!.Send!;
        Assert.Equal(3, defend.CommandId);                                      // cidDefend
        Assert.Equal((0x0001 | 0x0010 | 0x0800), defend.TargetTypeMask);        // friendly|station|nearest

        // followMeCommand: cidGoto (5), ttMe (0x80004).
        var follow = mm.ChoiceFor('7')!.Send!;
        Assert.Equal(5, follow.CommandId);                                      // cidGoto
        Assert.Equal(0x80004L, follow.TargetTypeMask);                          // ttMe
    }
}
