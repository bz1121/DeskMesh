using LanSwitch.Agent.Services;

namespace LanSwitch.Agent.Infrastructure;

public static class PhysicalFollowEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/peer/v1/physical-follow/subscriptions", (
            HttpContext context,
            PhysicalFollowSubscriptionCommand command,
            PeerDirectory peers,
            DistributedPhysicalFollowService follow) =>
        {
            var peer = RequireAuthorizedPeer(context, peers);
            if (!peers.TryGetTrustToken(peer.Id, peer.Fingerprint, out var trustToken))
                return Results.Unauthorized();
            var result = follow.AcceptSubscription(peer, command, trustToken);
            return result.Accepted ? Results.Ok(result) : Results.Conflict(result);
        });

        app.MapDelete("/peer/v1/physical-follow/subscriptions/{sessionId}", (
            string sessionId,
            HttpContext context,
            PeerDirectory peers,
            DistributedPhysicalFollowService follow) =>
        {
            var peer = RequireAuthorizedPeer(context, peers);
            follow.RemoveInboundSubscription(peer.Id, sessionId);
            return Results.Ok(new { removed = true });
        });

        app.MapPost("/peer/v1/physical-follow/observations", async (
            HttpContext context,
            PhysicalFollowObservation notification,
            PeerDirectory peers,
            DistributedPhysicalFollowService follow,
            CancellationToken cancellationToken) =>
        {
            var peer = RequireAuthorizedPeer(context, peers);
            if (!peers.TryGetTrustToken(peer.Id, peer.Fingerprint, out var trustToken))
                return Results.Unauthorized();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, trustToken);
            var result = await follow.AcceptObservationAsync(peer, notification, linked.Token);
            return result.Accepted ? Results.Ok(result) : Results.Conflict(result);
        });
    }

    private static RuntimePeer RequireAuthorizedPeer(HttpContext context, PeerDirectory peers)
    {
        if (!context.Items.TryGetValue("LanSwitch.Peer", out var value) || value is not RuntimePeer peer ||
            !peers.IsAuthorized(peer.Id, peer.Fingerprint))
            throw new UnauthorizedAccessException("设备未配对或信任已经撤销。");
        return peer;
    }
}
