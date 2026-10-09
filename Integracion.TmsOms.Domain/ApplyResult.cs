namespace Integracion.TmsOms.Domain
{
    public sealed record ApplyResult(ApplyOutcome Outcome, HistoryEntry History, TrackingEvent? FollowUpEvent)
    {
        public bool StateChanged => Outcome == ApplyOutcome.Applied;
    }
}
