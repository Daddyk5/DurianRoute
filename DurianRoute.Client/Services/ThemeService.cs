using Microsoft.JSInterop;
using MudBlazor;

namespace DurianRoute.Client.Services;

/// <summary>Light/dark mode, remembered per browser.</summary>
public class ThemeService(IJSRuntime js)
{
    private const string StorageKey = "durianroute.dark";

    public bool IsDark { get; private set; }
    public event Action? Changed;

    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#15803D",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#D97706",
            Tertiary = "#4F46E5",
            Info = "#0284C7",
            Success = "#16A34A",
            Warning = "#EA580C",
            Error = "#DC2626",
            Background = "#F4F6F3",
            Surface = "#FFFFFF",
            AppbarBackground = "#FFFFFF",
            AppbarText = "#1C2A20",
            DrawerBackground = "#FFFFFF",
            DrawerText = "#3B4A3F",
            DrawerIcon = "#5B6B5F",
            TextPrimary = "#1C2A20",
            TextSecondary = "#5B6B5F",
            LinesDefault = "#E2E8E1",
            TableLines = "#E8EDE7",
            Divider = "#E2E8E1",
            ActionDefault = "#5B6B5F"
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#4ADE80",
            PrimaryContrastText = "#06210F",
            Secondary = "#FBBF24",
            Tertiary = "#818CF8",
            Info = "#38BDF8",
            Success = "#4ADE80",
            Warning = "#FB923C",
            Error = "#F87171",
            Background = "#0D1410",
            Surface = "#151E18",
            AppbarBackground = "#151E18",
            AppbarText = "#E5EDE7",
            DrawerBackground = "#121A15",
            DrawerText = "#C5D1C8",
            DrawerIcon = "#9AA99E",
            TextPrimary = "#E5EDE7",
            TextSecondary = "#9AA99E",
            LinesDefault = "#26322A",
            TableLines = "#222D26",
            Divider = "#26322A",
            ActionDefault = "#9AA99E"
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = ["Figtree", "Segoe UI", "system-ui", "sans-serif"] },
            H4 = new H4Typography { FontWeight = "700", LetterSpacing = "-0.01em" },
            H5 = new H5Typography { FontWeight = "700", LetterSpacing = "-0.01em" },
            H6 = new H6Typography { FontWeight = "650" },
            Button = new ButtonTypography { FontWeight = "600", TextTransform = "none" }
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "10px", DrawerWidthLeft = "232px" }
    };

    public async Task InitializeAsync()
    {
        try
        {
            IsDark = await js.InvokeAsync<string?>("durianStorage.get", StorageKey) == "1";
        }
        catch (JSException) { }
        Changed?.Invoke();
    }

    public async Task ToggleAsync()
    {
        IsDark = !IsDark;
        Changed?.Invoke();
        try
        {
            await js.InvokeVoidAsync("durianStorage.set", StorageKey, IsDark ? "1" : "0");
        }
        catch (JSException) { }
    }
}
