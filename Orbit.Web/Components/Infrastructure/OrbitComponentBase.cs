using Microsoft.AspNetCore.Components;
using MudBlazor;
using Orbit.Application;

namespace Orbit.Components.Infrastructure;

/// <summary>
/// The Blazor side of the services' exceptions, as <c>OrbitTools.Run</c> is MCP's and the file endpoints' own catches are theirs
/// (<see cref="Orbit.Auth.AccessDeniedReason"/>). Pages and
/// controls call services only through <see cref="Load"/> and <see cref="Act"/>, each run in its own scope by
/// <see cref="OrbitRunner"/>, never by injecting a service directly. Load turns NotFoundException into the not-found page and
/// ForbiddenException into <see cref="Refusal"/>. Act shows any OrbitException as an error message and leaves the page as it was.
/// </summary>
public abstract class OrbitComponentBase : ComponentBase
{
    [Inject] protected OrbitRunner Runner { get; set; } = null!;
    [Inject] protected ISnackbar Snackbar { get; set; } = null!;
    [Inject] protected NavigationManager Nav { get; set; } = null!;
    [Inject] protected IDialogService Dialogs { get; set; } = null!;

    /// <summary>True once the page's data has loaded, or its load was refused.</summary>
    protected bool Loaded { get; private set; }

    /// <summary>Why the last load was refused (a ForbiddenException), to show instead of the page. Null when it loaded.</summary>
    protected string? Refusal { get; private set; }

    /// <summary>
    /// One load at a time on a component. Blazor starts a new <c>OnParametersSetAsync</c> without waiting for the last one, and a
    /// load sets the page's fields as it goes, so two that overlapped (a filter changed during a slow search) could finish out of
    /// order and leave the older results showing, or a mix of both. In turn, the last load asked for is the last one applied.
    /// </summary>
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    /// <summary>Loads the page's data. Returns false when the load was refused or found nothing.</summary>
    protected async Task<bool> Load(Func<IServiceProvider, Task> load)
    {
        await _loadGate.WaitAsync();
        try
        {
            await Runner.RunAsync(load);
            Refusal = null;
            return true;
        }
        catch (NotFoundException)
        {
            Nav.NotFound();
            return false;
        }
        catch (ForbiddenException ex)
        {
            Refusal = ex.Message;
            return false;
        }
        finally
        {
            Loaded = true;
            _loadGate.Release();
        }
    }

    /// <summary>Makes a change. Returns false, after showing why, when a service refused it.</summary>
    protected async Task<bool> Act(Func<IServiceProvider, Task> action)
    {
        try
        {
            await Runner.RunAsync(action);
            return true;
        }
        catch (OrbitException ex)
        {
            Error(ex.Message);
            return false;
        }
    }

    /// <summary>Asks before a change that can't be undone (the Razor pages' <c>data-confirm</c>). True when the user agreed.</summary>
    protected async Task<bool> Confirm(string title, string message, string yes = "Delete") =>
        await Dialogs.ShowMessageBoxAsync(title, message, yesText: yes, cancelText: "Cancel") == true;

    protected void Success(string message) => Snackbar.Add(message, Severity.Success);

    protected void Error(string message) => Snackbar.Add(message, Severity.Error);
}
