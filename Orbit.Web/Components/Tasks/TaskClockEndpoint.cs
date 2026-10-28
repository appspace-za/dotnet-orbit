using Microsoft.AspNetCore.Antiforgery;
using Orbit.Application;
using Orbit.Application.Services;

namespace Orbit.Components.Tasks;

/// <summary>
/// Where the task page's clock goes when the user leaves the page with a full page load (spec §6.10): the browser's
/// <c>navigator.sendBeacon</c> posts here, with the antiforgery token, and the clock is stopped and its time logged, as the
/// Razor page's <c>StopClockBeacon</c> handler did. A request carries the user's cookie, so the actor is the signed-in user.
/// </summary>
public static class TaskClockEndpoint
{
    public static string Path(Guid taskId) => $"/Tasks/Details/{taskId}/StopClockBeacon";

    public static IEndpointRouteBuilder MapTaskClockEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/Tasks/Details/{id:guid}/StopClockBeacon", async (Guid id, HttpContext http, IAntiforgery antiforgery, TimeEntryService time) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();
            try
            {
                await time.StopClockAsync(id, http.RequestAborted);
            }
            // As ReportPdfEndpoint answers: a refusal is an answer, not a server error to log (the account was deactivated or
            // removed while its clock ran). Nobody reads a beacon's response, so a refusal is a plain 403, not a redirect.
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
            catch (ForbiddenException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (OrbitException)
            {
                return Results.BadRequest();
            }
            return Results.NoContent();
        }).RequireAuthorization();
        return endpoints;
    }
}
