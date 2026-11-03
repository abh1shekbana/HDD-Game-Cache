using System;
using System.Reflection;

namespace SmartGameCache.UI.ViewModels;

public class AboutViewModel : ViewModelBase
{
    public string AppTitle => "Smart Game Cache";
    public string Tagline => "Intelligent HDD Gaming Accelerator";
    public string Version => "v1.0.0 (Release Build)";
    public string TargetFramework => ".NET 10 LTS (C# / WPF MVVM)";
    public string Architecture => "Windows 64-bit (x64)";
    public string Description => "Intelligently accelerates games installed on traditional mechanical HDDs by utilizing a configurable SSD cache and system RAM prefetch budget. Employs ETW kernel I/O monitoring, LRU/LFU adaptive scoring, and dynamic memory pressure safeguards to eliminate repetitive mechanical disk seek latency and asset streaming stutters.";
}

