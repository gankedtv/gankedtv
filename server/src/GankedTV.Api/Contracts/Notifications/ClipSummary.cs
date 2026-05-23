namespace GankedTV.Api.Contracts.Notifications;

// Slim shape — the notification dropdown only needs enough to route to the clip and label the
// row ("liked your clip 'Ace clutch'"). Full feed item / detail responses carry presigned
// media URLs which are pointless here.
public sealed record ClipSummary(Guid Id, string ShareCode, string Title);
