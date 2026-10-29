namespace Allegiance.ClientCore.Training;

/// <summary>
/// Well-known ids referenced by mission data — ports of the native enums and constants.
/// </summary>
public static class TrainingMissionIds
{
    // Mission ids (Training.h:21-32)
    public const int Tm1Introduction = 1;
    public const int Tm2BasicFlight = 2;
    public const int Tm3BasicWeaponry = 3;
    public const int Tm4EnemyEngagement = 4;
    public const int Tm5CommandView = 5;
    public const int Tm6PracticeArena = 6;
    public const int Tm7Live = 7;
    public const int Tm8Nanite = 8;
    public const int Tm10FreeFlight = 10;

    // TrekKey values the training missions react to (src/Igc/igc.h:261-436)
    public const int KeyFireBooster = 50; // TK_FireBooster — the SPACEBAR (igc.h:273)
    public const int KeyPauseTm = 157;              // TK_PauseTM (igc.h:375) — pause/resume training
    public const int KeySkipTmGoal = 200;           // TK_SkipTMGoal (igc.h:424) — skip a goal (debug)
    public const int KeyIncreaseTMClockSpeed = 201; // TK_IncreaseTMClockSpeed (igc.h:425) — debug
    public const int KeyDecreaseTMClockSpeed = 202; // TK_DecreaseTMClockSpeed (igc.h:426) — debug
    public const int KeyResetTMClockSpeed = 203;    // TK_ResetTMClockSpeed (igc.h:427) — debug
    public const int KeyCommandFinishTm = 207;      // TK_CommandFinishTM (igc.h:433) — delivered to key conditions
    public const int KeyViewCommand = 38;           // TK_ViewCommand (igc.h:261)
    public const int KeyRejectCommand = 42;         // TK_RejectCommand (igc.h:265)
    public const int KeyToggleAutoPilot = 43;       // TK_ToggleAutoPilot (igc.h:266)
    public const int KeyConModeGameState = 51;      // TK_ConModeGameState (igc.h:274)
    public const int KeyConModeCommand = 68;        // TK_ConModeCommand (igc.h:291)
    public const int KeyConModeInvest = 71;         // TK_ConModeInvest (igc.h:294)
    public const int KeySuicide = 92;               // TK_Suicide (igc.h:315)
    public const int KeyTargetSelf = 100;           // TK_TargetSelf (igc.h:323)
    public const int KeyTargetFriendly = 77;        // TK_TargetFriendly (igc.h:300)
    public const int KeyTargetEnemy = 80;           // TK_TargetEnemy (igc.h:303) — the F key
    public const int KeyToggleCloak = 94;           // TK_ToggleCloak (igc.h:317) — toggle the cloak
    public const int KeyTargetCenter = 90;          // TK_TargetCenter (igc.h:313) — target nearest reticle
    public const int KeyConModeTeleport = 210;      // TK_ConModeTeleport (igc.h:436)

    // Command ids (src/Igc/igc.h:898-912)
    public const int CmdDoNothing = -3; // c_cidDoNothing
    public const int CmdNone = -1;      // c_cidNone
    public const int CmdAttack = 1;     // c_cidAttack
    public const int CmdDefend = 3;     // c_cidDefend
    public const int CmdGoto = 5;       // c_cidGoto
    public const int CmdRepair = 6;     // c_cidRepair
    public const int CmdMine = 8;       // c_cidMine
    public const int CmdBuild = 9;      // c_cidBuild

    // Command enum (src/Igc/igc.h:883-887)
    public const int CommandQueued = 0;   // c_cmdQueued
    public const int CommandAccepted = 1; // c_cmdAccepted
    public const int CommandCurrent = 2;  // c_cmdCurrent

    // TrekWindow::CameraMode (src/WinTrek/Treki.h:108-122)
    public const int CameraCockpit = 0;         // cmCockpit
    public const int CameraExternalOrbit = 8;   // cmExternalOrbit
    public const int CameraExternalStation = 9; // cmExternalStation

    // Ship-control state bits (src/Igc/igc.h:805-831). Derived from selectedWeaponOneIGC=1:
    // coastButtonIGC = 4, so afterburner = 128*4 = 512, cloak = 512*4 = 2048, miningMask =
    // 2048*4 = 8192, oneWeapon = 2*8192 = 16384, weaponsMask = 3*16384 = 49152,
    // allWeapons = 2*16384 = 32768.
    public const int SelectedWeaponMask = 3;    // selectedWeaponMaskIGC (igc.h:807)
    public const int AfterburnerButton = 512;   // afterburnerButtonIGC (igc.h:817)
    public const int CloakActive = 2048;        // cloakActiveIGC (igc.h:820)
    public const int OneWeapon = 16384;         // oneWeaponIGC (igc.h:825)
    public const int AllWeapons = 32768;        // allWeaponsIGC (igc.h:826)
    public const int WeaponsMask = 49152;       // weaponsMaskIGC (igc.h:827)
    public const int MissileFire = 0x10000;     // missileFireIGC (igc.h:830)

    // Chat recipients (src/Igc/igc.h:2731)
    public const int ChatEveryone = 0;          // CHAT_EVERYONE

    // Wingman-behaviour bits (src/Igc/igc.h:785-792). NOTE: the TR-3 report read UseMissiles=0x10
    // (igc.h:786-791), but native is c_wbbmStrafe=0x01, c_wbbmUseMissiles=0x02, c_wbbmUseMines=0x04,
    // c_wbbmRunAt30Hull=0x10, c_wbbmRunAt60Hull=0x20, c_wbbmInRangeAggressive=0x40,
    // c_wbbmTempSectorAggressive=0x80. The values below are corrected to native truth. Native
    // CreateDroneAction default = c_wbbmUseMissiles|c_wbbmRunAt60Hull (createdroneaction.cpp:42).
    public const int WingmanStrafe = 0x01;
    public const int WingmanUseMissiles = 0x02;
    public const int WingmanUseMines = 0x04;
    public const int WingmanRunAt30Hull = 0x10;
    public const int WingmanRunAt60Hull = 0x20;
    public const int WingmanInRangeAggressive = 0x40;
    public const int WingmanTempSectorAggressive = 0x80;

    // Training HUD overlays — the native TrainingOverlay enum (WinTrek/indicator.h:17-42),
    // renumbered to the true enum order so M3/M4 overlays are native-accurate. The concrete
    // overlay visuals are client-side (TR-2); the ids carry the native ordering.
    public const int OverlayNone = 0;              // NoTrainingOverlay
    public const int OverlaySpeed = 1;             // SpeedTrainingOverlay
    public const int OverlayMotion = 2;            // MotionIndicatorTrainingOverlay
    public const int OverlayReticule = 3;          // ReticuleTrainingOverlay
    public const int OverlayThrottle = 4;          // ThrottleTrainingOverlay
    public const int OverlayFuel = 5;              // FuelTrainingOverlay
    public const int OverlayEnergy = 6;            // EnergyTrainingOverlay
    public const int OverlayAmmo = 7;              // AmmoTrainingOverlay
    public const int OverlayHull = 8;              // HullTrainingOverlay
    public const int OverlayShield = 9;            // ShieldTrainingOverlay
    public const int OverlayChat = 10;             // ChatTrainingOverlay
    public const int OverlaySector = 11;           // SectorTrainingOverlay
    public const int OverlayInventory = 12;        // InventoryTrainingOverlay
    public const int OverlayTarget = 13;           // TargetTrainingOverlay
    public const int OverlayCommand = 14;          // CommandTrainingOverlay
    public const int OverlayEyeball = 15;          // EyeballTrainingOverlay
    public const int OverlayKillBonus = 16;        // KillBonusTrainingOverlay
    public const int OverlayRoll = 17;             // RollTrainingOverlay
    public const int OverlayBooster = 18;          // BoosterTrainingOverlay
    public const int OverlayTargetCenter = 19;     // TargetCenterTrainingOverlay
    public const int OverlayFireGuns = 20;         // FireGunsTrainingOverlay
    public const int OverlayTargetEnemy = 21;      // TargetEnemyTrainingOverlay
    public const int OverlayMissile = 22;          // MissileTrainingOverlay
    public const int OverlayCloak = 23;            // CloakTrainingOverlay

    // Client overlay/pane masks shown by ShowPaneAction (native of*OverlayFlags, console.h:48-55).
    // ShowPaneAction::Execute turns these off and on the requested one (showpaneaction.cpp:30-38).
    public const int PaneExpandedTeam = 0x001000;   // ofExpandedTeam (console.h:52) — the Team display
    public const int PaneInventory = 0x002000;      // ofInventory (console.h:53)
    public const int PaneSector = 0x008000;         // ofSectorPane (console.h:55)

    // Mission 2 (Basic Flight) voice-over / commentary sound ids. Native uses symbolic tm_2_XXSound
    // ids bound in the trainingsounddef asset; exact values are catalog-specific, so the C# port uses
    // stable sequential ids here (TR-2 maps them to the asset catalog).
    public const int Tm2_01 = 2001;
    public const int Tm2_02 = 2002;
    public const int Tm2_03 = 2003;
    public const int Tm2_04 = 2004;
    public const int Tm2_05 = 2005;
    public const int Tm2_06 = 2006;
    public const int Tm2_07 = 2007;
    public const int Tm2_08 = 2008;
    public const int Tm2_11 = 2011;
    public const int Tm2_11r = 2012;
    public const int Tm2_12 = 2013;
    public const int Tm2_13 = 2014;
    public const int Tm2_15 = 2015;
    public const int Tm2_16 = 2016;
    public const int Tm2_16r = 2017;
    public const int Tm2_17 = 2018;
    public const int Tm2_18 = 2019;
    public const int Tm2_18r = 2020;
    public const int Tm2_19 = 2021;
    public const int Tm2_20 = 2022;
    public const int Tm2_21 = 2023;
    public const int Tm2_22 = 2024;
    public const int Tm2_23 = 2025;
    public const int Tm2_24 = 2026;
    public const int Tm2_24r = 2027;
    public const int Tm2_25 = 2028;
    public const int Tm2_26 = 2029;
    public const int Tm2_27 = 2030;
    public const int Tm2_28 = 2031;
    public const int Tm2_29 = 2032;
    public const int Tm2_30 = 2033;
    public const int Tm2_31 = 2034;
    public const int Tm2_32 = 2035;
    public const int Tm2_33 = 2036;
    public const int Tm2_34 = 2037;
    public const int Tm2_35 = 2038;
    public const int Tm2_36 = 2039;
    public const int Tm2_37 = 2040;
    public const int Tm2_38 = 2041;
}
