using Avalonia.Animation;

namespace Nitrox.Launcher.Models.Design;

internal interface IRoutingScreen
{
    object? ActiveViewModel { get; set; }
    IPageTransition? PageTransition { get; set; }
}
