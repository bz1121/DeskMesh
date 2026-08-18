using LanSwitch.Agent.Services;

namespace LanSwitch.Agent.Infrastructure;

public static class AiRepairEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/admin/ai", (AiRepairService service) =>
            Results.Ok(service.GetStatus()));

        app.MapPut("/api/v1/admin/ai", async (
            AiAssistantUpdate request,
            AiRepairService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ConfigureAsync(request, cancellationToken)));

        app.MapPost("/api/v1/admin/ai/analyze", async (
            AiRepairService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.AnalyzeAsync(cancellationToken)));

        app.MapPost("/api/v1/admin/ai/proposals/{proposalId}/apply", async (
            string proposalId,
            AiRepairApplyRequest request,
            AiRepairService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ApplyAsync(proposalId, request.ActionIds, cancellationToken)));
    }
}

public sealed record AiRepairApplyRequest(IReadOnlyList<string>? ActionIds);
