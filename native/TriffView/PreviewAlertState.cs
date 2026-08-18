using System;

namespace TriffView.Preview;

internal sealed record TriffViewPreviewAlert(
    int SeverityRank,
    string Color,
    int Thickness,
    int DurationMs,
    int PulseCount,
    bool Persistent
);

internal sealed class ActivePreviewAlert
{
    public ActivePreviewAlert(int severityRank, string color, int thickness, int durationMs, int pulseCount, DateTime startedUtc, DateTime expiresUtc, bool persistent)
    {
        SeverityRank = severityRank;
        Color = color;
        Thickness = thickness;
        DurationMs = durationMs;
        PulseCount = pulseCount;
        StartedUtc = startedUtc;
        ExpiresUtc = expiresUtc;
        Persistent = persistent;
    }

    public int SeverityRank { get; }
    public string Color { get; }
    public int Thickness { get; }
    public int DurationMs { get; }
    public int PulseCount { get; }
    public DateTime StartedUtc { get; }
    public DateTime ExpiresUtc { get; set; }
    public bool Persistent { get; }
}

internal sealed class PreviewAlertState
{
    private ActivePreviewAlert? _alert;

    private static bool IsLogicallyActive(ActivePreviewAlert? alert, DateTime now)
        => alert != null && (alert.Persistent || alert.ExpiresUtc > now);

    public ActivePreviewAlert? Active(DateTime now) => IsLogicallyActive(_alert, now) ? _alert : null;

    public bool Arm(TriffViewPreviewAlert alert, DateTime now, bool targetIsSelected)
    {
        var persistent = alert.Persistent && !targetIsSelected;
        if (IsLogicallyActive(_alert, now) && _alert!.SeverityRank > alert.SeverityRank)
        {
            _alert.ExpiresUtc = now.AddMilliseconds(Math.Max(1, alert.DurationMs));
            return true;
        }

        _alert = new ActivePreviewAlert(
            alert.SeverityRank,
            alert.Color,
            alert.Thickness,
            alert.DurationMs,
            alert.PulseCount,
            now,
            now.AddMilliseconds(Math.Max(1, alert.DurationMs)),
            persistent
        );
        return true;
    }

    public bool ClearExpired(DateTime now)
    {
        if (_alert == null || IsLogicallyActive(_alert, now)) return false;
        _alert = null;
        return true;
    }

    public bool Acknowledge()
    {
        if (_alert == null || !_alert.Persistent) return false;
        _alert = null;
        return true;
    }
}
