using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Auth;

namespace Orbit.Components.Shared;

/// <summary>
/// Serves an attachment's bytes (spec §6.18) to anyone who may see what it is attached to, at the address the Razor page had, so the
/// cards' links and MCP's <c>downloadPath</c> are unchanged (decision 110). Always as a download (Content-Disposition: attachment, no
/// content sniffing), so an uploaded HTML or SVG file is never rendered inside Orbit. A plain request, so the services come from the
/// request's own scope and the actor is the signed-in user.
/// </summary>
public static class AttachmentDownloadEndpoint
{
    public static IEndpointRouteBuilder MapAttachmentDownloadEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/Attachments/Download/{id:guid}", async (Guid id, HttpContext http, AttachmentService attachments) =>
        {
            try
            {
                var (attachment, content) = await attachments.DownloadAsync(id, http.RequestAborted);
                http.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.File(content, attachment.ContentType, attachment.FileName);
            }
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
            catch (ForbiddenException ex)
            {
                return AccessDeniedReason.Forbid(http, ex);
            }
        }).RequireAuthorization();
        return endpoints;
    }
}
