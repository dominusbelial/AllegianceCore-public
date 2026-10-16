namespace Allegiance.Core.Sim;

/// <summary>Native Igc/sounds.h:46-47; sound IDs start at 1001
/// (Igc/igc.h:456-464). Used by WinTrek PostText feedback.</summary>
public static class NotificationCue
{
    public const short Critical = 1035;
    public const short NonCritical = 1036;
    public static bool IsNotification(short soundId) => soundId is Critical or NonCritical;
}
