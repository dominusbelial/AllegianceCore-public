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

    // TrekKey values the training missions react to (src/Igc/igc.h:273)
    public const int KeyFireBooster = 50; // TK_FireBooster — the SPACEBAR

    // Command ids (src/Igc/igc.h:898-912)
    public const int CmdDoNothing = -3; // c_cidDoNothing
    public const int CmdNone = -1;      // c_cidNone
    public const int CmdGoto = 5;       // c_cidGoto

    // Command enum (src/Igc/igc.h:883-887)
    public const int CommandQueued = 0;   // c_cmdQueued
    public const int CommandAccepted = 1; // c_cmdAccepted
    public const int CommandCurrent = 2;  // c_cmdCurrent

    // TrekWindow::CameraMode (src/WinTrek/Treki.h:108-122)
    public const int CameraCockpit = 0;         // cmCockpit
    public const int CameraExternalOrbit = 8;   // cmExternalOrbit
    public const int CameraExternalStation = 9; // cmExternalStation

    // Ship-control state bit for the booster (src/Igc/igc.h:817)
    public const int AfterburnerButton = 512; // afterburnerButtonIGC = 128 * coastButtonIGC

    // Training HUD overlays — symbolic ids; the concrete overlay assets are client-side (TR-2).
    public const int OverlayNone = 0;              // NoTrainingOverlay
    public const int OverlayRoll = 1;              // RollTrainingOverlay
    public const int OverlayThrottle = 2;          // ThrottleTrainingOverlay
    public const int OverlaySpeed = 3;             // SpeedTrainingOverlay
    public const int OverlayMotion = 4;            // MotionIndicatorTrainingOverlay
    public const int OverlayFuel = 5;              // FuelTrainingOverlay
    public const int OverlayBooster = 6;           // BoosterTrainingOverlay

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
