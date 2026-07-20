namespace CalDesk.App.Models;

/// <summary>Broadcast when calendar colors/selection change locally (e.g. Settings save).
/// Week/Month VMs subscribe (R12) to refresh their color dots without a server sync.</summary>
public sealed record CalendarColorsChangedMessage;
