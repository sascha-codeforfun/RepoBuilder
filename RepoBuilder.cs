using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Win32;

namespace RepoBuilder
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };

            // Application level, not window level: implicit styles for Window only apply
            // from here, and HueContrast swaps its overrides in against this dictionary.
            app.Resources.MergedDictionaries.Add(
                (ResourceDictionary)XamlReader.Parse(HueTheme.Xaml));
            HueContrast.Follow(app);

            app.Run(new MainWindow());
        }
    }

    // ---------------------------------------------------------------- model

    public sealed class PackageRef
    {
        public string Id { get; init; } = "";
        public string Version { get; init; } = "";
    }

    public sealed class ProjectInfo
    {
        public string Path { get; init; } = "";
        public string AssemblyName { get; init; } = "";
        public string OutputType { get; init; } = "";
        public bool UsesDesktop { get; init; }
        public bool WantsReadyToRun { get; init; }
        public bool WantsTrim { get; init; }
        public List<PackageRef> Packages { get; init; } = new();
        public bool IsExecutable => OutputType is "Exe" or "WinExe";

        public string Display
        {
            get
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(Path);
                var kind = OutputType.Length == 0 ? "Library" : OutputType;
                return $"{name}  ({kind})";
            }
        }

        public override string ToString() => Display;
    }

    // ------------------------------------------------------------- services

    public static class DotnetLocator
    {
        /// <summary>An SDK unpacked next to the exe. Environment.ProcessPath is used
        /// rather than AppContext.BaseDirectory, which points at the single-file
        /// extraction folder in temp rather than the exe's real location.</summary>
        public static string PortableRoot
        {
            get
            {
                var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
                return exeDir.Length == 0 ? "" : Path.Combine(exeDir, "sdk");
            }
        }

        public static string PortableDotnet =>
            PortableRoot.Length == 0 ? "" : Path.Combine(PortableRoot, "dotnet.exe");

        public static bool HasPortable => PortableDotnet.Length > 0 && File.Exists(PortableDotnet);

        /// <summary>A primed NuGet global-packages folder beside the exe. Holds the
        /// runtime packs that self-contained publish needs and that the SDK zip does
        /// not ship.</summary>
        public static string PortablePackages
        {
            get
            {
                var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
                return exeDir.Length == 0 ? "" : Path.Combine(exeDir, "packages");
            }
        }

        public static bool HasPortablePackages =>
            PortablePackages.Length > 0 && Directory.Exists(PortablePackages);

        /// <summary>The SDK versions installed under a dotnet root, newest first,
        /// read from the folder names in sdk\. No process needs to be run.</summary>
        public static List<(string Version, DateTime Stamp)> SdkVersions(string dotnetExePath)
        {
            var list = new List<(string, DateTime)>();
            try
            {
                var root = Path.GetDirectoryName(dotnetExePath);
                if (root is null) return list;
                var sdkDir = Path.Combine(root, "sdk");
                if (!Directory.Exists(sdkDir)) return list;

                foreach (var dir in Directory.EnumerateDirectories(sdkDir))
                {
                    var name = Path.GetFileName(dir);
                    if (name.Length == 0 || !char.IsDigit(name[0])) continue;
                    list.Add((name, Directory.GetLastWriteTime(dir)));
                }
            }
            catch { }

            return list.OrderByDescending(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string RootOf(string dotnetExePath) =>
            Path.GetDirectoryName(dotnetExePath) ?? "";

        /// <summary>Finds dotnet.exe: a portable SDK beside the exe, then DOTNET_ROOT,
        /// then the registry, then the default install path, then PATH. PATH is last so
        /// a stale entry left by an old install cannot win.</summary>
        public static string? Find()
        {
            if (HasPortable) return PortableDotnet;

            var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrWhiteSpace(root))
            {
                var candidate = Path.Combine(root, "dotnet.exe");
                if (File.Exists(candidate)) return candidate;
            }

            foreach (var arch in new[] { "x64", "arm64", "x86" })
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        $@"SOFTWARE\dotnet\Setup\InstalledVersions\{arch}");
                    if (key?.GetValue("InstallLocation") is string loc && loc.Length > 0)
                    {
                        var candidate = Path.Combine(loc, "dotnet.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
                catch { /* registry unavailable, fall through */ }
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var def = Path.Combine(programFiles, "dotnet", "dotnet.exe");
            if (File.Exists(def)) return def;

            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "dotnet.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* malformed PATH entry */ }
            }

            return null;
        }
    }

    public static class ProjectScanner
    {
        static readonly string[] Skip = { "bin", "obj", ".git", ".vs", "node_modules", "packages" };

        public static List<ProjectInfo> Scan(string repoRoot)
        {
            var results = new List<ProjectInfo>();
            foreach (var file in EnumerateProjects(repoRoot))
            {
                try { results.Add(Read(file)); }
                catch { results.Add(new ProjectInfo { Path = file }); }
            }
            return results
                .OrderByDescending(p => p.IsExecutable)
                .ThenBy(p => Path.GetFileNameWithoutExtension(p.Path))
                .ToList();
        }

        static IEnumerable<string> EnumerateProjects(string dir)
        {
            IEnumerable<string> files = Array.Empty<string>();
            IEnumerable<string> subdirs = Array.Empty<string>();

            try { files = Directory.EnumerateFiles(dir, "*.csproj"); } catch { }
            foreach (var f in files) yield return f;

            try { subdirs = Directory.EnumerateDirectories(dir); } catch { }
            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (Skip.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                foreach (var f in EnumerateProjects(sub)) yield return f;
            }
        }

        static ProjectInfo Read(string projectPath)
        {
            var doc = XDocument.Load(projectPath);
            string Value(string element) => doc.Descendants()
                .Where(e => e.Name.LocalName.Equals(element, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Value.Trim())
                .FirstOrDefault(v => v.Length > 0) ?? "";

            bool Flag(string element) =>
                Value(element).Equals("true", StringComparison.OrdinalIgnoreCase);

            var asm = Value("AssemblyName");
            if (asm.Length == 0) asm = Path.GetFileNameWithoutExtension(projectPath);

            var packages = doc.Descendants()
                .Where(e => e.Name.LocalName.Equals("PackageReference", StringComparison.OrdinalIgnoreCase))
                .Select(e => new PackageRef
                {
                    Id = (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update") ?? "",
                    Version = (string?)e.Attribute("Version")
                              ?? e.Elements().FirstOrDefault(c =>
                                     c.Name.LocalName.Equals("Version", StringComparison.OrdinalIgnoreCase))
                                 ?.Value.Trim()
                              ?? ""
                })
                .Where(p => p.Id.Length > 0)
                .ToList();

            return new ProjectInfo
            {
                Path = projectPath,
                AssemblyName = asm,
                OutputType = Value("OutputType"),
                UsesDesktop = Flag("UseWPF") || Flag("UseWindowsForms"),
                WantsReadyToRun = Flag("PublishReadyToRun"),
                WantsTrim = Flag("PublishTrimmed"),
                Packages = packages
            };
        }
    }

    public enum Readiness { Present, Maybe, Missing, Unknown }

    public sealed class ReadinessItem
    {
        public string Name { get; init; } = "";
        public string Detail { get; init; } = "";
        public string Version { get; init; } = "";
        public string Group { get; init; } = "tools";
        public Readiness State { get; init; }
    }

    /// <summary>Reads the primed packages folder straight off disk. No restore, no
    /// network, no NuGet involvement — so the answers are limited to what the layout
    /// can tell us.</summary>
    public static class PackageCache
    {
        public static bool Exists => DotnetLocator.HasPortablePackages;

        /// <summary>Package id (lowercased, as on disk) to the versions present.</summary>
        public static Dictionary<string, List<string>> Read()
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (!Exists) return result;

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(DotnetLocator.PortablePackages))
                {
                    var id = Path.GetFileName(dir);
                    var versions = new List<string>();
                    try
                    {
                        foreach (var v in Directory.EnumerateDirectories(dir))
                            versions.Add(Path.GetFileName(v));
                    }
                    catch { }
                    if (versions.Count > 0) result[id] = versions;
                }
            }
            catch { }

            return result;
        }

        public static int Count(Dictionary<string, List<string>> cache) => cache.Count;

        /// <summary>Works out what the project will ask restore for, and whether the
        /// cache already holds it. Transitive dependencies cannot be determined this
        /// way and are reported as unknown rather than guessed at.</summary>
        public static List<ReadinessItem> Check(ProjectInfo project, string rid,
                                                bool trimRequested, bool r2rRequested)
        {
            var items = new List<ReadinessItem>();
            var cache = Read();

            if (!Exists)
            {
                items.Add(new ReadinessItem
                {
                    Name = "Nothing stored locally",
                    Detail = "everything will be fetched over the network",
                    State = Readiness.Missing
                });
                return items;
            }

            void CheckPack(string id, string name)
            {
                if (cache.TryGetValue(id, out var versions))
                    items.Add(new ReadinessItem
                    {
                        Name = name,
                        Version = string.Join(", ", versions.OrderByDescending(v => v)),
                        State = Readiness.Present
                    });
                else
                    items.Add(new ReadinessItem
                    {
                        Name = name,
                        Detail = "not stored locally",
                        State = Readiness.Missing
                    });
            }

            CheckPack($"microsoft.netcore.app.runtime.{rid}", $"Runtime pack · {rid}");
            if (project.UsesDesktop)
                CheckPack($"microsoft.windowsdesktop.app.runtime.{rid}", "Desktop pack · WPF and WinForms");
            if (project.WantsReadyToRun || r2rRequested)
                CheckPack($"microsoft.netcore.app.crossgen2.{rid}", "ReadyToRun compiler");
            if (project.WantsTrim || trimRequested)
                CheckPack("microsoft.net.illink.tasks", "Trimming tools");

            foreach (var pkg in project.Packages)
            {
                if (!cache.TryGetValue(pkg.Id, out var versions))
                {
                    items.Add(new ReadinessItem
                    {
                        Name = pkg.Id,
                        Group = "repo",
                        Detail = pkg.Version.Length > 0
                            ? $"wants {pkg.Version}, not stored locally"
                            : "not stored locally",
                        State = Readiness.Missing
                    });
                    continue;
                }

                var have = string.Join(", ", versions.OrderBy(v => v));

                if (pkg.Version.Length == 0)
                {
                    items.Add(new ReadinessItem
                    {
                        Name = pkg.Id, Group = "repo",
                        Detail = "no version named in the project",
                        Version = have, State = Readiness.Maybe
                    });
                }
                else if (versions.Contains(pkg.Version, StringComparer.OrdinalIgnoreCase))
                {
                    items.Add(new ReadinessItem
                    {
                        Name = pkg.Id, Group = "repo",
                        Version = pkg.Version, State = Readiness.Present
                    });
                }
                else if (AnyHigher(versions, pkg.Version))
                {
                    items.Add(new ReadinessItem
                    {
                        Name = pkg.Id, Group = "repo",
                        Detail = $"wants {pkg.Version}, have {have} — newer, usually fine",
                        State = Readiness.Maybe
                    });
                }
                else
                {
                    items.Add(new ReadinessItem
                    {
                        Name = pkg.Id, Group = "repo",
                        Detail = $"wants {pkg.Version}, have {have} — older, will probably fail",
                        State = Readiness.Missing
                    });
                }
            }

            items.Add(new ReadinessItem
            {
                Name = "Anything those packages need in turn",
                Group = "repo",
                Detail = "only the build itself can find this out",
                State = Readiness.Unknown
            });

            return items;
        }

        static bool AnyHigher(List<string> versions, string wanted)
        {
            if (!TryParse(wanted, out var want)) return false;
            foreach (var v in versions)
                if (TryParse(v, out var have) && have >= want) return true;
            return false;
        }

        /// <summary>Numeric prefix only; prerelease suffixes are ignored for ordering.</summary>
        static bool TryParse(string text, out Version version)
        {
            version = new Version(0, 0);
            var core = text.Split('-', '+')[0];
            var parts = core.Split('.');
            if (parts.Length < 2) return false;
            var numeric = string.Join('.', parts.Take(4));
            return Version.TryParse(numeric, out version!);
        }
    }

    // ------------------------------------------------------------------ ui

    /// <summary>The suite's shared dark theme from the HueTheme kit — Cyan family, neutral
    /// surfaces, glow 0.35 — parsed into a resource dictionary at startup. Inlined rather
    /// than shipped as the kit's files so the app stays one readable .cs.
    ///
    /// Only the styles this app uses are carried, and each kept section is the kit's text
    /// verbatim: a difference against the kit is a kit change, never a local edit. A control
    /// used here for the first time gets its style copied from the kit, not written here.</summary>
    internal static class HueTheme
    {
        public const string Xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:sys="clr-namespace:System;assembly=mscorlib">

    <!-- ================================================================== PALETTE -->

    <!-- Text, identical in every family -->
    <Color x:Key="Text.Primary">#E7EAF0</Color>
    <Color x:Key="Text.Dim">#A3ACBA</Color>
    <Color x:Key="Text.Muted">#8590A2</Color>
    <Color x:Key="Text.Disabled">#5C6577</Color>

    <!-- Status, identical in every family. Fill = dot and solid fill; Text = pill text. -->
    <Color x:Key="Ok.Fill">#22C55E</Color>
    <Color x:Key="Ok.Text">#86EFAC</Color>
    <Color x:Key="Ok.Soft">#1F22C55E</Color>
    <Color x:Key="Ok.Edge">#5922C55E</Color>
    <Color x:Key="Warn.Fill">#FBBF24</Color>
    <Color x:Key="Warn.Text">#FCD34D</Color>
    <Color x:Key="Warn.Soft">#1FFBBF24</Color>
    <Color x:Key="Warn.Edge">#59FBBF24</Color>
    <Color x:Key="Limit.Fill">#FB923C</Color>
    <Color x:Key="Limit.Text">#FDBA74</Color>
    <Color x:Key="Limit.Soft">#1FFB923C</Color>
    <Color x:Key="Limit.Edge">#59FB923C</Color>
    <Color x:Key="Error.Fill">#F87171</Color>
    <Color x:Key="Error.Text">#FCA5A5</Color>
    <Color x:Key="Error.Soft">#1FF87171</Color>
    <Color x:Key="Error.Edge">#66F87171</Color>
    <!-- Solid fills for destructive buttons: a deeper red that carries white text. -->
    <Color x:Key="Error.Solid">#D12F2F</Color>
    <Color x:Key="Error.SolidHover">#DC2626</Color>
    <Color x:Key="OnError">#FFFFFF</Color>
    <Color x:Key="Stale.Fill">#8B95A5</Color>
    <Color x:Key="Stale.Text">#B4BDCA</Color>
    <Color x:Key="Stale.Soft">#1F8B95A5</Color>
    <Color x:Key="Stale.Edge">#598B95A5</Color>
    <Color x:Key="OnStatus">#0B0D11</Color>

    <!-- The outline that identifies a control (input, checkbox, radio, switch, scroll thumb).
         At least 3:1 against every surface, tinted ones included (WCAG 2.2, 1.4.11).
         Card borders and dividers stay softer: they decorate, they don't identify. -->
    <Color x:Key="Control.Edge">#6B7487</Color>

    <!-- Neutral surfaces: the default. Darkest first. -->
    <Color x:Key="Neutral.Base">#0B0D11</Color>
    <Color x:Key="Neutral.Chrome">#0F1217</Color>
    <Color x:Key="Neutral.Surface">#12151B</Color>
    <Color x:Key="Neutral.Raised">#191D25</Color>
    <Color x:Key="Neutral.Hover">#222733</Color>
    <Color x:Key="Neutral.LineSoft">#1C212A</Color>
    <Color x:Key="Neutral.Line">#2A3040</Color>
    <Color x:Key="Neutral.LineStrong">#3A4254</Color>
    <Color x:Key="Neutral.Scrim">#E60B0D11</Color>

    <!-- Two colours from other families, carried on purpose. The suite gives each app one
         accent, and Cyan is this one's; these are a deliberate exception for two buttons whose
         colour is older than the theme and is what the hand reaches for. Values are the kit's
         own, unaltered, including the text partner — so the fills stay WCAG-checked. Nothing
         else in the app may use them, and no third family may be added without the same
         justification. Indigo is the media family, Amber the data and registry family. -->
    <Color x:Key="Indigo.Fill">#5B5EEE</Color>
    <Color x:Key="Indigo.FillHover">#4C4FE0</Color>
    <Color x:Key="Indigo.OnAccent">#FFFFFF</Color>
    <Color x:Key="Amber.Fill">#F59E0B</Color>
    <Color x:Key="Amber.FillHover">#FBBF24</Color>
    <Color x:Key="Amber.OnAccent">#0B0D11</Color>

    <!-- Cyan · dev &amp; tools -->
    <Color x:Key="Cyan.Base">#22D3EE</Color>
    <Color x:Key="Cyan.Mid">#38BDF8</Color>
    <Color x:Key="Cyan.Light">#7DD3FC</Color>
    <Color x:Key="Cyan.Pale">#A4E0FD</Color>
    <Color x:Key="Cyan.Fill">#22D3EE</Color>
    <Color x:Key="Cyan.FillHover">#38BDF8</Color>
    <Color x:Key="Cyan.OnAccent">#0B0D11</Color>
    <Color x:Key="Cyan.Soft">#2422D3EE</Color>
    <Color x:Key="Cyan.SoftHover">#3D22D3EE</Color>
    <Color x:Key="Cyan.Edge">#6622D3EE</Color>
    <Color x:Key="Cyan.Tint.Base">#0C161B</Color>
    <Color x:Key="Cyan.Tint.Chrome">#101D23</Color>
    <Color x:Key="Cyan.Tint.Surface">#132129</Color>
    <Color x:Key="Cyan.Tint.Raised">#1A2B34</Color>
    <Color x:Key="Cyan.Tint.Hover">#223643</Color>
    <Color x:Key="Cyan.Tint.LineSoft">#1D313C</Color>
    <Color x:Key="Cyan.Tint.Line">#294253</Color>
    <Color x:Key="Cyan.Tint.LineStrong">#375568</Color>
    <Color x:Key="Cyan.Tint.Scrim">#E60C161B</Color>

    <!-- ================================================================== ACTIVE
         CONFIGURED FOR: Cyan (dev and tools), neutral surfaces, glow 0.35 -->

    <SolidColorBrush x:Key="Brush.Base"       Color="{StaticResource Neutral.Base}" />
    <SolidColorBrush x:Key="Brush.Chrome"     Color="{StaticResource Neutral.Chrome}" />
    <SolidColorBrush x:Key="Brush.Surface"    Color="{StaticResource Neutral.Surface}" />
    <SolidColorBrush x:Key="Brush.Raised"     Color="{StaticResource Neutral.Raised}" />
    <SolidColorBrush x:Key="Brush.Hover"      Color="{StaticResource Neutral.Hover}" />
    <SolidColorBrush x:Key="Brush.LineSoft"   Color="{StaticResource Neutral.LineSoft}" />
    <SolidColorBrush x:Key="Brush.Line"       Color="{StaticResource Neutral.Line}" />
    <SolidColorBrush x:Key="Brush.LineStrong" Color="{StaticResource Neutral.LineStrong}" />

    <SolidColorBrush x:Key="Brush.Accent"          Color="{StaticResource Cyan.Base}" />
    <SolidColorBrush x:Key="Brush.AccentMid"       Color="{StaticResource Cyan.Mid}" />
    <SolidColorBrush x:Key="Brush.AccentLight"     Color="{StaticResource Cyan.Light}" />
    <SolidColorBrush x:Key="Brush.AccentText"      Color="{StaticResource Cyan.Light}" />
    <SolidColorBrush x:Key="Brush.AccentFill"      Color="{StaticResource Cyan.Fill}" />
    <SolidColorBrush x:Key="Brush.AccentFillHover" Color="{StaticResource Cyan.FillHover}" />
    <SolidColorBrush x:Key="Brush.OnAccent"        Color="{StaticResource Cyan.OnAccent}" />
    <SolidColorBrush x:Key="Brush.AccentSoft"      Color="{StaticResource Cyan.Soft}" />
    <SolidColorBrush x:Key="Brush.AccentSoftHover" Color="{StaticResource Cyan.SoftHover}" />
    <SolidColorBrush x:Key="Brush.AccentEdge"      Color="{StaticResource Cyan.Edge}" />
    <SolidColorBrush x:Key="Brush.Glow"            Color="{StaticResource Cyan.Light}" />

    <SolidColorBrush x:Key="Brush.IndigoFill"      Color="{StaticResource Indigo.Fill}" />
    <SolidColorBrush x:Key="Brush.IndigoFillHover" Color="{StaticResource Indigo.FillHover}" />
    <SolidColorBrush x:Key="Brush.OnIndigo"        Color="{StaticResource Indigo.OnAccent}" />
    <SolidColorBrush x:Key="Brush.AmberFill"       Color="{StaticResource Amber.Fill}" />
    <SolidColorBrush x:Key="Brush.AmberFillHover"  Color="{StaticResource Amber.FillHover}" />
    <SolidColorBrush x:Key="Brush.OnAmber"         Color="{StaticResource Amber.OnAccent}" />


    <!-- How strongly glows show, 0 to 1. -->
    <sys:Double x:Key="Glow.Opacity">0.35</sys:Double>

    <!-- ================================================================== FIXED BRUSHES -->

    <SolidColorBrush x:Key="Brush.Text"         Color="{StaticResource Text.Primary}" />
    <SolidColorBrush x:Key="Brush.TextDim"      Color="{StaticResource Text.Dim}" />
    <SolidColorBrush x:Key="Brush.TextMuted"    Color="{StaticResource Text.Muted}" />
    <SolidColorBrush x:Key="Brush.TextDisabled" Color="{StaticResource Text.Disabled}" />
    <SolidColorBrush x:Key="Brush.OnStatus"     Color="{StaticResource OnStatus}" />
    <SolidColorBrush x:Key="Brush.ControlEdge"  Color="{StaticResource Control.Edge}" />

    <SolidColorBrush x:Key="Brush.Ok"         Color="{StaticResource Ok.Fill}" />
    <SolidColorBrush x:Key="Brush.OkText"     Color="{StaticResource Ok.Text}" />
    <SolidColorBrush x:Key="Brush.OkSoft"     Color="{StaticResource Ok.Soft}" />
    <SolidColorBrush x:Key="Brush.OkEdge"     Color="{StaticResource Ok.Edge}" />
    <SolidColorBrush x:Key="Brush.Warn"       Color="{StaticResource Warn.Fill}" />
    <SolidColorBrush x:Key="Brush.WarnText"   Color="{StaticResource Warn.Text}" />
    <SolidColorBrush x:Key="Brush.WarnSoft"   Color="{StaticResource Warn.Soft}" />
    <SolidColorBrush x:Key="Brush.WarnEdge"   Color="{StaticResource Warn.Edge}" />
    <SolidColorBrush x:Key="Brush.Limit"      Color="{StaticResource Limit.Fill}" />
    <SolidColorBrush x:Key="Brush.LimitText"  Color="{StaticResource Limit.Text}" />
    <SolidColorBrush x:Key="Brush.LimitSoft"  Color="{StaticResource Limit.Soft}" />
    <SolidColorBrush x:Key="Brush.LimitEdge"  Color="{StaticResource Limit.Edge}" />
    <SolidColorBrush x:Key="Brush.Error"      Color="{StaticResource Error.Fill}" />
    <SolidColorBrush x:Key="Brush.ErrorText"  Color="{StaticResource Error.Text}" />
    <SolidColorBrush x:Key="Brush.ErrorSoft"  Color="{StaticResource Error.Soft}" />
    <SolidColorBrush x:Key="Brush.ErrorEdge"  Color="{StaticResource Error.Edge}" />
    <SolidColorBrush x:Key="Brush.ErrorSolid"      Color="{StaticResource Error.Solid}" />
    <SolidColorBrush x:Key="Brush.ErrorSolidHover" Color="{StaticResource Error.SolidHover}" />
    <SolidColorBrush x:Key="Brush.OnError"         Color="{StaticResource OnError}" />
    <SolidColorBrush x:Key="Brush.Stale"      Color="{StaticResource Stale.Fill}" />
    <SolidColorBrush x:Key="Brush.StaleText"  Color="{StaticResource Stale.Text}" />
    <SolidColorBrush x:Key="Brush.StaleSoft"  Color="{StaticResource Stale.Soft}" />
    <SolidColorBrush x:Key="Brush.StaleEdge"  Color="{StaticResource Stale.Edge}" />

    <!-- ================================================================== METRICS AND TYPE -->

    <FontFamily x:Key="Font.Ui">Segoe UI Variable Text, Segoe UI</FontFamily>
    <FontFamily x:Key="Font.Display">Segoe UI Variable Display, Segoe UI</FontFamily>
    <FontFamily x:Key="Font.Mono">Cascadia Mono, Consolas</FontFamily>

    <CornerRadius x:Key="Radius.Control">8</CornerRadius>
    <CornerRadius x:Key="Radius.Card">12</CornerRadius>
    <CornerRadius x:Key="Radius.Item">6</CornerRadius>
    <sys:Double x:Key="Size.Control">34</sys:Double>

    <!-- ================================================================== ICONS
         16 x 16 stroke geometries. Use with Style="{StaticResource Hue.Icon}"; the stroke
         follows the surrounding Foreground. -->

    <Geometry x:Key="Icon.Refresh">M13.2,8 A5.2,5.2 0 1 1 11.6,4.3 M12,1.6 L12,4.7 L8.9,4.7</Geometry>
    <Geometry x:Key="Icon.Copy">M5.5,5.5 L13,5.5 L13,13 L5.5,13 Z M3,10.5 L3,3 L10.5,3</Geometry>
    <Geometry x:Key="Icon.Close">M4,4 L12,12 M12,4 L4,12</Geometry>
    <Geometry x:Key="Icon.Folder">M2,4.5 A1,1 0 0 1 3,3.5 L6.2,3.5 L7.6,5 L13,5 A1,1 0 0 1 14,6 L14,12 A1,1 0 0 1 13,13 L3,13 A1,1 0 0 1 2,12 Z</Geometry>
    <Geometry x:Key="Icon.Import">M8,2.5 L8,10.5 M4.5,7 L8,10.5 L11.5,7 M3,13.5 L13,13.5</Geometry>
    <Geometry x:Key="Icon.Play">M5,3 L13,8 L5,13 Z</Geometry>
    <Geometry x:Key="Icon.Stop">M4,4 L12,4 L12,12 L4,12 Z</Geometry>
    <Geometry x:Key="Icon.Open">M9,3 L13,3 L13,7 M13,3 L7.5,8.5 M11,9.5 L11,13 L3,13 L3,5 L6.5,5</Geometry>


    <Style x:Key="Hue.Icon" TargetType="Path">
        <Setter Property="Width" Value="16" />
        <Setter Property="Height" Value="16" />
        <Setter Property="Stretch" Value="None" />
        <Setter Property="StrokeThickness" Value="1.6" />
        <Setter Property="StrokeStartLineCap" Value="Round" />
        <Setter Property="StrokeEndLineCap" Value="Round" />
        <Setter Property="StrokeLineJoin" Value="Round" />
        <Setter Property="SnapsToDevicePixels" Value="False" />
        <Setter Property="VerticalAlignment" Value="Center" />
        <Setter Property="Stroke" Value="{Binding Path=(TextElement.Foreground), RelativeSource={RelativeSource Self}}" />
    </Style>

    <!-- ================================================================== FOCUS AND WINDOW -->

    <Style x:Key="Hue.FocusVisual">
        <Setter Property="Control.Template">
            <Setter.Value>
                <ControlTemplate>
                    <Border Margin="-3" CornerRadius="10" BorderThickness="2"
                            BorderBrush="{DynamicResource Brush.AccentLight}" SnapsToDevicePixels="True" />
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- For items inside lists, grids and drop-downs, where an outset ring would overlap
         the neighbours. Keyboard only, like every FocusVisualStyle. -->
    <Style x:Key="Hue.FocusVisual.Inset">
        <Setter Property="Control.Template">
            <Setter.Value>
                <ControlTemplate>
                    <Border Margin="1" CornerRadius="5" BorderThickness="2"
                            BorderBrush="{DynamicResource Brush.AccentLight}" SnapsToDevicePixels="True" />
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="Hue.Window" TargetType="Window">
        <Setter Property="Background" Value="{DynamicResource Brush.Base}" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
        <Setter Property="FontFamily" Value="{StaticResource Font.Ui}" />
        <Setter Property="FontSize" Value="13" />
        <Setter Property="UseLayoutRounding" Value="True" />
        <Setter Property="SnapsToDevicePixels" Value="True" />
    </Style>

    <!-- ================================================================== TEXT -->

    <Style x:Key="Text.Title" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource Font.Display}" />
        <Setter Property="FontSize" Value="22" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
    </Style>
    <Style x:Key="Text.Heading" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource Font.Display}" />
        <Setter Property="FontSize" Value="17" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
    </Style>
    <!-- The title of a tile: a card that stands for one thing (a profile, a server, a
         account), such as "Domain" / "Private" / "Public". Accent text, so the tiles of an app
         carry its family colour. Page and card sections use Text.Heading / Text.Section. -->
    <Style x:Key="Text.TileTitle" TargetType="TextBlock">
        <Setter Property="FontSize" Value="15" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.AccentText}" />
    </Style>
    <Style x:Key="Text.Body" TargetType="TextBlock">
        <Setter Property="FontSize" Value="13" />
        <Setter Property="TextWrapping" Value="Wrap" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.TextDim}" />
    </Style>
    <Style x:Key="Text.Caption" TargetType="TextBlock">
        <Setter Property="FontSize" Value="12" />
        <Setter Property="TextWrapping" Value="Wrap" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.TextMuted}" />
    </Style>
    <!-- Section labels are written in capitals in the markup; WPF has no text-transform. -->
    <Style x:Key="Text.Section" TargetType="TextBlock">
        <Setter Property="FontSize" Value="11" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.TextMuted}" />
    </Style>
    <Style x:Key="Text.Mono" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource Font.Mono}" />
        <Setter Property="FontSize" Value="12" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.TextDim}" />
    </Style>

    <Style TargetType="Label">
        <Setter Property="Foreground" Value="{DynamicResource Brush.TextDim}" />
        <Setter Property="Padding" Value="0,0,0,6" />
    </Style>

    <!-- ================================================================== BUTTONS -->

    <ControlTemplate x:Key="Hue.ButtonTemplate" TargetType="ButtonBase">
        <Border x:Name="Bd" Background="{TemplateBinding Background}"
                BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                CornerRadius="{StaticResource Radius.Control}" SnapsToDevicePixels="True">
            <ContentPresenter Margin="{TemplateBinding Padding}" RecognizesAccessKey="True"
                              HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                              VerticalAlignment="{TemplateBinding VerticalContentAlignment}" />
        </Border>
        <ControlTemplate.Triggers>
            <Trigger Property="IsPressed" Value="True">
                <Setter TargetName="Bd" Property="Opacity" Value="0.82" />
            </Trigger>
        </ControlTemplate.Triggers>
    </ControlTemplate>

    <!-- Same shape with a blurred copy of the fill underneath: the glow follows Background. -->
    <ControlTemplate x:Key="Hue.GlowButtonTemplate" TargetType="ButtonBase">
        <Grid>
            <Border x:Name="Glow" Background="{TemplateBinding Background}" IsHitTestVisible="False"
                    CornerRadius="{StaticResource Radius.Control}" Opacity="{DynamicResource Glow.Opacity}">
                <Border.Effect>
                    <BlurEffect Radius="16" />
                </Border.Effect>
            </Border>
            <Border x:Name="Bd" Background="{TemplateBinding Background}"
                    BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                    CornerRadius="{StaticResource Radius.Control}" SnapsToDevicePixels="True">
                <ContentPresenter Margin="{TemplateBinding Padding}" RecognizesAccessKey="True"
                                  HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                  VerticalAlignment="{TemplateBinding VerticalContentAlignment}" />
            </Border>
        </Grid>
        <ControlTemplate.Triggers>
            <Trigger Property="IsPressed" Value="True">
                <Setter TargetName="Bd" Property="Opacity" Value="0.85" />
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
                <Setter TargetName="Glow" Property="Visibility" Value="Collapsed" />
            </Trigger>
        </ControlTemplate.Triggers>
    </ControlTemplate>

    <Style x:Key="Hue.Button" TargetType="ButtonBase">
        <Setter Property="FocusVisualStyle" Value="{StaticResource Hue.FocusVisual}" />
        <Setter Property="Background" Value="{DynamicResource Brush.Raised}" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.Line}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="Padding" Value="14,0" />
        <Setter Property="MinHeight" Value="{StaticResource Size.Control}" />
        <Setter Property="MinWidth" Value="{StaticResource Size.Control}" />
        <Setter Property="FontSize" Value="13" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="HorizontalContentAlignment" Value="Center" />
        <Setter Property="VerticalContentAlignment" Value="Center" />
        <Setter Property="SnapsToDevicePixels" Value="True" />
        <Setter Property="Template" Value="{StaticResource Hue.ButtonTemplate}" />
        <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Background" Value="{DynamicResource Brush.Hover}" />
                <Setter Property="BorderBrush" Value="{DynamicResource Brush.LineStrong}" />
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Background" Value="{DynamicResource Brush.Surface}" />
                <Setter Property="Foreground" Value="{DynamicResource Brush.TextDisabled}" />
                <Setter Property="BorderBrush" Value="{DynamicResource Brush.LineSoft}" />
            </Trigger>
        </Style.Triggers>
    </Style>

    <!-- Filled accent with glow. One per view: the thing the view is for. -->
    <Style x:Key="Hue.Button.Primary" TargetType="ButtonBase" BasedOn="{StaticResource Hue.Button}">
        <Setter Property="Background" Value="{DynamicResource Brush.AccentFill}" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.OnAccent}" />
        <Setter Property="BorderThickness" Value="0" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Padding" Value="18,0" />
        <Setter Property="Template" Value="{StaticResource Hue.GlowButtonTemplate}" />
        <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Background" Value="{DynamicResource Brush.AccentFillHover}" />
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Background" Value="{DynamicResource Brush.Raised}" />
                <Setter Property="Foreground" Value="{DynamicResource Brush.TextDisabled}" />
            </Trigger>
        </Style.Triggers>
    </Style>

    <!-- Tinted accent, no glow. Secondary actions that still belong to the app's domain. -->
    <!-- The two deliberate exceptions. Same shape and behaviour as the primary button,
         including the halo, which the template takes from Background so it follows each
         fill on its own. Only the fill and its text partner differ. A third filled button
         in the view is the cost, and it is accepted: these two colours carry meaning from
         long before the theme, and losing them would cost more than the rule saves. -->
    <Style x:Key="Hue.Button.Indigo" TargetType="ButtonBase" BasedOn="{StaticResource Hue.Button.Primary}">
        <Setter Property="Background" Value="{DynamicResource Brush.IndigoFill}" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.OnIndigo}" />
        <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Background" Value="{DynamicResource Brush.IndigoFillHover}" />
            </Trigger>
        </Style.Triggers>
    </Style>

    <Style x:Key="Hue.Button.Amber" TargetType="ButtonBase" BasedOn="{StaticResource Hue.Button.Primary}">
        <Setter Property="Background" Value="{DynamicResource Brush.AmberFill}" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.OnAmber}" />
        <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Background" Value="{DynamicResource Brush.AmberFillHover}" />
            </Trigger>
        </Style.Triggers>
    </Style>

    <Style x:Key="Hue.Button.Tonal" TargetType="ButtonBase" BasedOn="{StaticResource Hue.Button}">
        <Setter Property="Background" Value="{DynamicResource Brush.AccentSoft}" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.AccentText}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.AccentEdge}" />
        <Setter Property="FontWeight" Value="Medium" />
        <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Background" Value="{DynamicResource Brush.AccentSoftHover}" />
                <Setter Property="BorderBrush" Value="{DynamicResource Brush.AccentEdge}" />
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Background" Value="{DynamicResource Brush.Surface}" />
                <Setter Property="Foreground" Value="{DynamicResource Brush.TextDisabled}" />
                <Setter Property="BorderBrush" Value="{DynamicResource Brush.LineSoft}" />
            </Trigger>
        </Style.Triggers>
    </Style>

    <Style x:Key="Hue.Button.Outline" TargetType="ButtonBase" BasedOn="{StaticResource Hue.Button}">
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.AccentText}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.AccentEdge}" />
        <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Background" Value="{DynamicResource Brush.AccentSoft}" />
                <Setter Property="BorderBrush" Value="{DynamicResource Brush.AccentEdge}" />
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Background" Value="Transparent" />
                <Setter Property="Foreground" Value="{DynamicResource Brush.TextDisabled}" />
                <Setter Property="BorderBrush" Value="{DynamicResource Brush.LineSoft}" />
            </Trigger>
        </Style.Triggers>
    </Style>

    <Style x:Key="Hue.Button.Ghost" TargetType="ButtonBase" BasedOn="{StaticResource Hue.Button}">
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.TextDim}" />
        <Setter Property="BorderBrush" Value="Transparent" />
        <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Background" Value="{DynamicResource Brush.Hover}" />
                <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
                <Setter Property="BorderBrush" Value="Transparent" />
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Background" Value="Transparent" />
                <Setter Property="Foreground" Value="{DynamicResource Brush.TextDisabled}" />
                <Setter Property="BorderBrush" Value="Transparent" />
            </Trigger>
        </Style.Triggers>
    </Style>

    <!-- Destroys something. Glows, because it matters. The same deep red with white text in
         every family; the lighter Brush.Error is for dots, bars and text, not for fills. -->
    <Style x:Key="Hue.Button.Danger" TargetType="ButtonBase" BasedOn="{StaticResource Hue.Button}">
        <Setter Property="Background" Value="{DynamicResource Brush.ErrorSolid}" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.OnError}" />
        <Setter Property="BorderThickness" Value="0" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Padding" Value="18,0" />
        <Setter Property="Template" Value="{StaticResource Hue.GlowButtonTemplate}" />
        <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Background" Value="{DynamicResource Brush.ErrorSolidHover}" />
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Background" Value="{DynamicResource Brush.Raised}" />
                <Setter Property="Foreground" Value="{DynamicResource Brush.TextDisabled}" />
            </Trigger>
        </Style.Triggers>
    </Style>

    <!-- Square, icon only. Give it an AutomationProperties.Name and a ToolTip. -->
    <Style x:Key="Hue.Button.Icon" TargetType="ButtonBase" BasedOn="{StaticResource Hue.Button.Ghost}">
        <Setter Property="Padding" Value="0" />
        <Setter Property="Width" Value="{StaticResource Size.Control}" />
        <Setter Property="Height" Value="{StaticResource Size.Control}" />
    </Style>

    <Style TargetType="Button" BasedOn="{StaticResource Hue.Button}" />
    <Style TargetType="RepeatButton" BasedOn="{StaticResource Hue.Button}" />
    <Style TargetType="ToggleButton" BasedOn="{StaticResource Hue.Button}">
        <Style.Triggers>
            <!-- The accent edge, not the soft one: the on state must stand out at 3:1. -->
            <Trigger Property="IsChecked" Value="True">
                <Setter Property="Background" Value="{DynamicResource Brush.AccentSoft}" />
                <Setter Property="Foreground" Value="{DynamicResource Brush.AccentText}" />
                <Setter Property="BorderBrush" Value="{DynamicResource Brush.Accent}" />
            </Trigger>
        </Style.Triggers>
    </Style>

    <!-- ================================================================== SCROLLING -->

    <Style x:Key="Hue.ScrollThumb" TargetType="Thumb">
        <Setter Property="OverridesDefaultStyle" Value="True" />
        <Setter Property="IsTabStop" Value="False" />
        <Setter Property="Focusable" Value="False" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Thumb">
                    <Border x:Name="T" Margin="3" CornerRadius="4" Background="{DynamicResource Brush.ControlEdge}" />
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="T" Property="Background" Value="{DynamicResource Brush.TextDim}" />
                        </Trigger>
                        <Trigger Property="IsDragging" Value="True">
                            <Setter TargetName="T" Property="Background" Value="{DynamicResource Brush.Text}" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="Hue.ScrollPage" TargetType="RepeatButton">
        <Setter Property="OverridesDefaultStyle" Value="True" />
        <Setter Property="IsTabStop" Value="False" />
        <Setter Property="Focusable" Value="False" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="RepeatButton">
                    <Border Background="Transparent" />
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="ScrollBar">
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="Width" Value="14" />
        <Setter Property="MinWidth" Value="14" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ScrollBar">
                    <Grid Background="{TemplateBinding Background}">
                        <Track x:Name="PART_Track" IsDirectionReversed="True">
                            <Track.DecreaseRepeatButton>
                                <RepeatButton Style="{StaticResource Hue.ScrollPage}" Command="ScrollBar.PageUpCommand" />
                            </Track.DecreaseRepeatButton>
                            <Track.Thumb>
                                <Thumb Style="{StaticResource Hue.ScrollThumb}" />
                            </Track.Thumb>
                            <Track.IncreaseRepeatButton>
                                <RepeatButton Style="{StaticResource Hue.ScrollPage}" Command="ScrollBar.PageDownCommand" />
                            </Track.IncreaseRepeatButton>
                        </Track>
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
        <Style.Triggers>
            <Trigger Property="Orientation" Value="Horizontal">
                <Setter Property="Width" Value="Auto" />
                <Setter Property="MinWidth" Value="0" />
                <Setter Property="Height" Value="14" />
                <Setter Property="MinHeight" Value="14" />
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="ScrollBar">
                            <Grid Background="{TemplateBinding Background}">
                                <Track x:Name="PART_Track" IsDirectionReversed="False">
                                    <Track.DecreaseRepeatButton>
                                        <RepeatButton Style="{StaticResource Hue.ScrollPage}" Command="ScrollBar.PageLeftCommand" />
                                    </Track.DecreaseRepeatButton>
                                    <Track.Thumb>
                                        <Thumb Style="{StaticResource Hue.ScrollThumb}" />
                                    </Track.Thumb>
                                    <Track.IncreaseRepeatButton>
                                        <RepeatButton Style="{StaticResource Hue.ScrollPage}" Command="ScrollBar.PageRightCommand" />
                                    </Track.IncreaseRepeatButton>
                                </Track>
                            </Grid>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Trigger>
        </Style.Triggers>
    </Style>

    <!-- Replaced so the corner between two scroll bars is not the system's light grey. -->
    <Style TargetType="ScrollViewer">
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ScrollViewer">
                    <Grid Background="{TemplateBinding Background}">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <Grid.RowDefinitions>
                            <RowDefinition Height="*" />
                            <RowDefinition Height="Auto" />
                        </Grid.RowDefinitions>
                        <ScrollContentPresenter x:Name="PART_ScrollContentPresenter"
                                                Margin="{TemplateBinding Padding}"
                                                CanContentScroll="{TemplateBinding CanContentScroll}"
                                                Content="{TemplateBinding Content}"
                                                ContentTemplate="{TemplateBinding ContentTemplate}" />
                        <ScrollBar x:Name="PART_VerticalScrollBar" Grid.Column="1"
                                   Value="{TemplateBinding VerticalOffset}"
                                   Maximum="{TemplateBinding ScrollableHeight}"
                                   ViewportSize="{TemplateBinding ViewportHeight}"
                                   Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}" />
                        <ScrollBar x:Name="PART_HorizontalScrollBar" Grid.Row="1" Orientation="Horizontal"
                                   Value="{TemplateBinding HorizontalOffset}"
                                   Maximum="{TemplateBinding ScrollableWidth}"
                                   ViewportSize="{TemplateBinding ViewportWidth}"
                                   Visibility="{TemplateBinding ComputedHorizontalScrollBarVisibility}" />
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ================================================================== TEXT INPUT
         Inputs sit on Base, one step darker than the card they are on, so they read as inset.
         Focus is the accent border; inputs do not glow. -->

    <ControlTemplate x:Key="Hue.InputTemplate" TargetType="Control">
        <Border x:Name="Bd" Background="{TemplateBinding Background}"
                BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                CornerRadius="{StaticResource Radius.Control}" SnapsToDevicePixels="True">
            <ScrollViewer x:Name="PART_ContentHost" Focusable="False"
                          HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden" />
        </Border>
        <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource Brush.TextMuted}" />
            </Trigger>
            <Trigger Property="IsKeyboardFocusWithin" Value="True">
                <Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource Brush.AccentLight}" />
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
                <Setter TargetName="Bd" Property="Opacity" Value="0.55" />
            </Trigger>
        </ControlTemplate.Triggers>
    </ControlTemplate>

    <Style TargetType="TextBox">
        <Setter Property="Background" Value="{DynamicResource Brush.Base}" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.ControlEdge}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="Padding" Value="9,6" />
        <Setter Property="MinHeight" Value="{StaticResource Size.Control}" />
        <Setter Property="VerticalContentAlignment" Value="Center" />
        <Setter Property="CaretBrush" Value="{DynamicResource Brush.Text}" />
        <Setter Property="SelectionBrush" Value="{DynamicResource Brush.Accent}" />
        <Setter Property="SelectionOpacity" Value="0.35" />
        <Setter Property="FocusVisualStyle" Value="{x:Null}" />
        <Setter Property="Template" Value="{StaticResource Hue.InputTemplate}" />
        <Style.Triggers>
            <Trigger Property="IsReadOnly" Value="True">
                <Setter Property="Background" Value="{DynamicResource Brush.Surface}" />
                <Setter Property="Foreground" Value="{DynamicResource Brush.TextDim}" />
            </Trigger>
        </Style.Triggers>
    </Style>

    <!-- ================================================================== CHECK, RADIO, SWITCH -->

    <Style TargetType="CheckBox">
        <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
        <Setter Property="Background" Value="{DynamicResource Brush.Base}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.ControlEdge}" />
        <Setter Property="Padding" Value="8,0,0,0" />
        <Setter Property="MinHeight" Value="24" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="VerticalContentAlignment" Value="Center" />
        <Setter Property="FocusVisualStyle" Value="{StaticResource Hue.FocusVisual}" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="CheckBox">
                    <Grid Background="Transparent" SnapsToDevicePixels="True">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="Auto" />
                            <ColumnDefinition Width="*" />
                        </Grid.ColumnDefinitions>
                        <Border x:Name="Box" Width="18" Height="18" CornerRadius="5" BorderThickness="1.5"
                                VerticalAlignment="Center"
                                Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}">
                            <Grid>
                                <Path x:Name="Mark" Data="M1,5 L4.5,8.5 L11,1.5" Visibility="Collapsed"
                                      Stroke="{DynamicResource Brush.OnAccent}" StrokeThickness="2"
                                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                                      HorizontalAlignment="Center" VerticalAlignment="Center" />
                                <Rectangle x:Name="Dash" Width="8" Height="2" RadiusX="1" RadiusY="1" Visibility="Collapsed"
                                           Fill="{DynamicResource Brush.OnAccent}" />
                            </Grid>
                        </Border>
                        <ContentPresenter Grid.Column="1" Margin="{TemplateBinding Padding}" RecognizesAccessKey="True"
                                          VerticalAlignment="{TemplateBinding VerticalContentAlignment}" />
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Brush.AccentLight}" />
                        </Trigger>
                        <Trigger Property="IsChecked" Value="True">
                            <Setter TargetName="Box" Property="Background" Value="{DynamicResource Brush.AccentFill}" />
                            <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Brush.AccentFill}" />
                            <Setter TargetName="Mark" Property="Visibility" Value="Visible" />
                        </Trigger>
                        <Trigger Property="IsChecked" Value="{x:Null}">
                            <Setter TargetName="Box" Property="Background" Value="{DynamicResource Brush.AccentFill}" />
                            <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Brush.AccentFill}" />
                            <Setter TargetName="Dash" Property="Visibility" Value="Visible" />
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.45" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ================================================================== COMBOBOX -->

    <Style x:Key="Hue.ComboToggle" TargetType="ToggleButton">
        <Setter Property="OverridesDefaultStyle" Value="True" />
        <Setter Property="Focusable" Value="False" />
        <Setter Property="IsTabStop" Value="False" />
        <Setter Property="ClickMode" Value="Press" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ToggleButton">
                    <Border Background="Transparent" />
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="Hue.ComboEditBox" TargetType="TextBox">
        <Setter Property="OverridesDefaultStyle" Value="True" />
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
        <Setter Property="CaretBrush" Value="{DynamicResource Brush.Text}" />
        <Setter Property="SelectionBrush" Value="{DynamicResource Brush.Accent}" />
        <Setter Property="SelectionOpacity" Value="0.35" />
        <Setter Property="FocusVisualStyle" Value="{x:Null}" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="TextBox">
                    <ScrollViewer x:Name="PART_ContentHost" Focusable="False" Background="Transparent"
                                  HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden" />
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- The panel behind every popup: menus, context menus, drop-downs. The margin leaves
         room for the shadow inside the popup's transparent window. -->
    <Style x:Key="Hue.PopupPanel" TargetType="Border">
        <Setter Property="Background" Value="{DynamicResource Brush.Raised}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.Line}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius" Value="10" />
        <Setter Property="Padding" Value="4" />
        <Setter Property="Margin" Value="0,0,10,10" />
        <Setter Property="SnapsToDevicePixels" Value="True" />
        <Setter Property="Effect">
            <Setter.Value>
                <DropShadowEffect BlurRadius="18" ShadowDepth="4" Direction="270" Opacity="0.55" Color="Black" />
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="ComboBoxItem">
        <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
        <Setter Property="Padding" Value="10,7" />
        <Setter Property="HorizontalContentAlignment" Value="Stretch" />
        <Setter Property="VerticalContentAlignment" Value="Center" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="FocusVisualStyle" Value="{StaticResource Hue.FocusVisual.Inset}" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ComboBoxItem">
                    <Grid>
                        <Border x:Name="Bd" Background="Transparent" CornerRadius="{StaticResource Radius.Item}"
                                Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                              VerticalAlignment="{TemplateBinding VerticalContentAlignment}" />
                        </Border>
                        <Border x:Name="SelectionBar" Width="3" Margin="0,6" HorizontalAlignment="Left" CornerRadius="1.5"
                                Background="{DynamicResource Brush.Accent}" Visibility="Collapsed" IsHitTestVisible="False" />
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsHighlighted" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource Brush.Hover}" />
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource Brush.AccentSoft}" />
                            <Setter TargetName="SelectionBar" Property="Visibility" Value="Visible" />
                            <Setter Property="Foreground" Value="{DynamicResource Brush.AccentText}" />
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Foreground" Value="{DynamicResource Brush.TextDisabled}" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="ComboBox">
        <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
        <Setter Property="Background" Value="{DynamicResource Brush.Base}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.ControlEdge}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="Padding" Value="10,0,34,0" />
        <Setter Property="MinHeight" Value="{StaticResource Size.Control}" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="FocusVisualStyle" Value="{x:Null}" />
        <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled" />
        <Setter Property="ScrollViewer.CanContentScroll" Value="True" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ComboBox">
                    <Grid SnapsToDevicePixels="True">
                        <Border x:Name="Bd" Background="{TemplateBinding Background}"
                                BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                                CornerRadius="{StaticResource Radius.Control}" />
                        <ToggleButton x:Name="Toggle" Style="{StaticResource Hue.ComboToggle}"
                                      IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}" />
                        <ContentPresenter x:Name="ContentSite" IsHitTestVisible="False"
                                          Margin="{TemplateBinding Padding}"
                                          HorizontalAlignment="Left" VerticalAlignment="Center"
                                          Content="{TemplateBinding SelectionBoxItem}"
                                          ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                                          ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}" />
                        <TextBox x:Name="PART_EditableTextBox" Style="{StaticResource Hue.ComboEditBox}"
                                 Visibility="Hidden" Margin="{TemplateBinding Padding}" VerticalAlignment="Center"
                                 IsReadOnly="{TemplateBinding IsReadOnly}" />
                        <Path x:Name="Arrow" Data="M0,0 L4,4 L8,0" IsHitTestVisible="False"
                              HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,13,0"
                              Stroke="{DynamicResource Brush.TextDim}" StrokeThickness="1.6"
                              StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round" />
                        <Popup x:Name="PART_Popup" Placement="Bottom" VerticalOffset="4" Focusable="False"
                               AllowsTransparency="True" PopupAnimation="Fade"
                               IsOpen="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}">
                            <Border Style="{StaticResource Hue.PopupPanel}"
                                    MinWidth="{Binding ActualWidth, ElementName=Bd}"
                                    MaxHeight="{TemplateBinding MaxDropDownHeight}">
                                <ScrollViewer SnapsToDevicePixels="True">
                                    <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained" />
                                </ScrollViewer>
                            </Border>
                        </Popup>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource Brush.TextMuted}" />
                        </Trigger>
                        <Trigger Property="IsKeyboardFocusWithin" Value="True">
                            <Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource Brush.AccentLight}" />
                        </Trigger>
                        <Trigger Property="IsDropDownOpen" Value="True">
                            <Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource Brush.AccentLight}" />
                            <Setter TargetName="Arrow" Property="Stroke" Value="{DynamicResource Brush.AccentLight}" />
                        </Trigger>
                        <Trigger Property="IsEditable" Value="True">
                            <Setter TargetName="PART_EditableTextBox" Property="Visibility" Value="Visible" />
                            <Setter TargetName="ContentSite" Property="Visibility" Value="Hidden" />
                            <Setter Property="Cursor" Value="IBeam" />
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.5" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ================================================================== PROGRESS
         Tag="Glow" makes the indicator glow in its own Foreground: for the one bar a view is
         about, not for bars repeated in list or grid rows. -->

    <Style TargetType="ProgressBar">
        <Setter Property="Background" Value="{DynamicResource Brush.Hover}" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Accent}" />
        <Setter Property="BorderThickness" Value="0" />
        <Setter Property="Height" Value="6" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ProgressBar">
                    <Grid x:Name="TemplateRoot" SnapsToDevicePixels="True">
                        <Border x:Name="PART_Track" CornerRadius="3" Background="{TemplateBinding Background}" />
                        <Grid x:Name="PART_Indicator" HorizontalAlignment="Left">
                            <Border x:Name="IndicatorGlow" CornerRadius="3" Background="{TemplateBinding Foreground}"
                                    Visibility="Collapsed" Opacity="{DynamicResource Glow.Opacity}" IsHitTestVisible="False">
                                <Border.Effect>
                                    <BlurEffect Radius="8" />
                                </Border.Effect>
                            </Border>
                            <Border CornerRadius="3" Background="{TemplateBinding Foreground}" />
                        </Grid>
                        <Border x:Name="Sweep" CornerRadius="3" Visibility="Collapsed"
                                Background="{TemplateBinding Foreground}">
                            <Border.OpacityMask>
                                <LinearGradientBrush StartPoint="0,0.5" EndPoint="1,0.5">
                                    <LinearGradientBrush.RelativeTransform>
                                        <TranslateTransform X="-1" />
                                    </LinearGradientBrush.RelativeTransform>
                                    <GradientStop Offset="0.15" Color="Transparent" />
                                    <GradientStop Offset="0.5" Color="Black" />
                                    <GradientStop Offset="0.85" Color="Transparent" />
                                </LinearGradientBrush>
                            </Border.OpacityMask>
                        </Border>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="Tag" Value="Glow">
                            <Setter TargetName="IndicatorGlow" Property="Visibility" Value="Visible" />
                        </Trigger>
                        <Trigger Property="IsIndeterminate" Value="True">
                            <Setter TargetName="PART_Indicator" Property="Visibility" Value="Collapsed" />
                            <Setter TargetName="Sweep" Property="Visibility" Value="Visible" />
                            <Trigger.EnterActions>
                                <BeginStoryboard x:Name="SweepRun">
                                    <Storyboard RepeatBehavior="Forever">
                                        <DoubleAnimation Storyboard.TargetName="Sweep"
                                                         Storyboard.TargetProperty="(UIElement.OpacityMask).(Brush.RelativeTransform).(TranslateTransform.X)"
                                                         From="-1" To="1" Duration="0:0:1.6" />
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                            <Trigger.ExitActions>
                                <StopStoryboard BeginStoryboardName="SweepRun" />
                            </Trigger.ExitActions>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ================================================================== TABS
         Underline tabs. The selected tab's line glows. -->

    <Style TargetType="TabControl">
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
        <Setter Property="BorderThickness" Value="0" />
        <Setter Property="Padding" Value="0,16,0,0" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="TabControl">
                    <Grid KeyboardNavigation.TabNavigation="Local" SnapsToDevicePixels="True">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="*" />
                        </Grid.RowDefinitions>
                        <Border BorderBrush="{DynamicResource Brush.LineSoft}" BorderThickness="0,0,0,1">
                            <TabPanel x:Name="HeaderPanel" IsItemsHost="True" Panel.ZIndex="1"
                                      KeyboardNavigation.TabIndex="1" Background="Transparent" />
                        </Border>
                        <Border Grid.Row="1" Background="{TemplateBinding Background}">
                            <ContentPresenter x:Name="PART_SelectedContentHost" ContentSource="SelectedContent"
                                              Margin="{TemplateBinding Padding}" />
                        </Border>
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="TabItem">
        <Setter Property="Foreground" Value="{DynamicResource Brush.TextDim}" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="FocusVisualStyle" Value="{StaticResource Hue.FocusVisual}" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="TabItem">
                    <Grid x:Name="Root" Background="Transparent" Margin="0,0,4,0">
                        <ContentPresenter x:Name="Content" ContentSource="Header" Margin="14,9,14,11"
                                          HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True" />
                        <Border x:Name="LineGlow" Height="3" Margin="10,0" VerticalAlignment="Bottom" CornerRadius="1.5"
                                Background="{DynamicResource Brush.Glow}" Opacity="0" IsHitTestVisible="False">
                            <Border.Effect>
                                <BlurEffect Radius="7" />
                            </Border.Effect>
                        </Border>
                        <Border x:Name="Line" Height="2" Margin="10,0" VerticalAlignment="Bottom" CornerRadius="1"
                                Background="{DynamicResource Brush.Accent}" Visibility="Hidden" />
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
                            <Setter TargetName="Line" Property="Visibility" Value="Visible" />
                            <Setter TargetName="LineGlow" Property="Opacity" Value="{DynamicResource Glow.Opacity}" />
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Foreground" Value="{DynamicResource Brush.TextDisabled}" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ================================================================== TOOLTIP -->

    <Style TargetType="ToolTip">
        <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
        <Setter Property="Background" Value="{DynamicResource Brush.Raised}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.Line}" />
        <Setter Property="FontFamily" Value="{StaticResource Font.Ui}" />
        <Setter Property="FontSize" Value="12" />
        <Setter Property="Padding" Value="10,6" />
        <Setter Property="HasDropShadow" Value="False" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ToolTip">
                    <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="1" CornerRadius="{StaticResource Radius.Control}"
                            Padding="{TemplateBinding Padding}" MaxWidth="360" SnapsToDevicePixels="True">
                        <ContentPresenter>
                            <ContentPresenter.Resources>
                                <Style TargetType="TextBlock">
                                    <Setter Property="TextWrapping" Value="Wrap" />
                                </Style>
                            </ContentPresenter.Resources>
                        </ContentPresenter>
                    </Border>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ================================================================== SUITE LAYOUT -->

    <Style x:Key="Hue.Card" TargetType="Border">
        <Setter Property="Background" Value="{DynamicResource Brush.Surface}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.LineSoft}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius" Value="{StaticResource Radius.Card}" />
        <Setter Property="Padding" Value="20,18" />
        <Setter Property="SnapsToDevicePixels" Value="True" />
    </Style>

    <!-- The header block: app_256.png in Hue.IconGlow, app name in Text.Title, one line in Text.Body. -->
    <Style x:Key="Hue.Header" TargetType="Border">
        <Setter Property="Background" Value="{DynamicResource Brush.Chrome}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.LineSoft}" />
        <Setter Property="BorderThickness" Value="0,0,0,1" />
        <Setter Property="Padding" Value="24,16" />
    </Style>

    <Style x:Key="Hue.StatusBar" TargetType="Border">
        <Setter Property="Background" Value="{DynamicResource Brush.Chrome}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.LineSoft}" />
        <Setter Property="BorderThickness" Value="0,1,0,0" />
        <Setter Property="Padding" Value="16,0" />
        <Setter Property="MinHeight" Value="28" />
        <Setter Property="TextElement.FontSize" Value="12" />
        <Setter Property="TextElement.Foreground" Value="{DynamicResource Brush.TextDim}" />
    </Style>

    <Style x:Key="Hue.Divider" TargetType="Border">
        <Setter Property="Background" Value="{DynamicResource Brush.LineSoft}" />
        <Setter Property="Width" Value="1" />
        <Setter Property="Margin" Value="20,4" />
    </Style>


    <!-- The app_256.png header image, with a halo in the family colour behind it. -->
    <Style x:Key="Hue.IconGlow" TargetType="ContentControl">
        <Setter Property="Focusable" Value="False" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ContentControl">
                    <Grid>
                        <Border Margin="6" CornerRadius="16" Background="{DynamicResource Brush.Glow}"
                                Opacity="{DynamicResource Glow.Opacity}" IsHitTestVisible="False">
                            <Border.Effect>
                                <BlurEffect Radius="22" />
                            </Border.Effect>
                        </Border>
                        <ContentPresenter />
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ================================================================== FEEDBACK -->

    <!-- Callouts: Foreground is the title and marker colour, Background the fill, BorderBrush the edge. -->
    <ControlTemplate x:Key="Hue.CalloutTemplate" TargetType="ContentControl">
        <Grid>
            <Border x:Name="Halo" CornerRadius="{StaticResource Radius.Card}" BorderThickness="3" Visibility="Collapsed"
                    BorderBrush="{TemplateBinding Foreground}" Opacity="{DynamicResource Glow.Opacity}" IsHitTestVisible="False">
                <Border.Effect>
                    <BlurEffect Radius="18" />
                </Border.Effect>
            </Border>
            <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                    BorderThickness="1" CornerRadius="{StaticResource Radius.Card}" Padding="{TemplateBinding Padding}"
                    SnapsToDevicePixels="True">
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <Grid Width="10" Height="10" Margin="0,5,14,0" VerticalAlignment="Top">
                        <Ellipse x:Name="DotGlow" Width="14" Height="14" Margin="-2" Visibility="Collapsed"
                                 Fill="{TemplateBinding Foreground}" Opacity="{DynamicResource Glow.Opacity}" IsHitTestVisible="False">
                            <Ellipse.Effect>
                                <BlurEffect Radius="8" />
                            </Ellipse.Effect>
                        </Ellipse>
                        <Ellipse Fill="{TemplateBinding Foreground}" />
                    </Grid>
                    <ContentPresenter Grid.Column="1" VerticalAlignment="Center" />
                </Grid>
            </Border>
        </Grid>
        <ControlTemplate.Triggers>
            <Trigger Property="Tag" Value="Glow">
                <Setter TargetName="Halo" Property="Visibility" Value="Visible" />
                <Setter TargetName="DotGlow" Property="Visibility" Value="Visible" />
            </Trigger>
        </ControlTemplate.Triggers>
    </ControlTemplate>

    <Style x:Key="Hue.Callout" TargetType="ContentControl">
        <Setter Property="Focusable" Value="False" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.AccentText}" />
        <Setter Property="Background" Value="{DynamicResource Brush.AccentSoft}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.AccentEdge}" />
        <Setter Property="Padding" Value="16,12" />
        <Setter Property="Template" Value="{StaticResource Hue.CalloutTemplate}" />
    </Style>

    <!-- Something needs attention. Status red with a halo, in every family. -->
    <Style x:Key="Hue.Callout.Critical" TargetType="ContentControl" BasedOn="{StaticResource Hue.Callout}">
        <Setter Property="Foreground" Value="{DynamicResource Brush.ErrorText}" />
        <Setter Property="Background" Value="{DynamicResource Brush.ErrorSoft}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.ErrorEdge}" />
        <Setter Property="Tag" Value="Glow" />
    </Style>

    <Style x:Key="Hue.Callout.Ok" TargetType="ContentControl" BasedOn="{StaticResource Hue.Callout}">
        <Setter Property="Foreground" Value="{DynamicResource Brush.OkText}" />
        <Setter Property="Background" Value="{DynamicResource Brush.OkSoft}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.OkEdge}" />
    </Style>

    <Style x:Key="Hue.Callout.Warn" TargetType="ContentControl" BasedOn="{StaticResource Hue.Callout}">
        <Setter Property="Foreground" Value="{DynamicResource Brush.WarnText}" />
        <Setter Property="Background" Value="{DynamicResource Brush.WarnSoft}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.WarnEdge}" />
    </Style>

    <!-- Pills: a dot and a word, for status ("how it went"). Foreground is text and dot,
         Background the fill, BorderBrush the edge. Tag="Glow" makes the dot glow: use it on a
         single standalone pill for live state or a failure, never on pills that repeat in list
         or grid rows (every glow is a blur, and over Remote Desktop the CPU draws it). -->
    <ControlTemplate x:Key="Hue.PillTemplate" TargetType="ContentControl">
        <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                BorderThickness="1" CornerRadius="11" Height="22" Padding="8,0,10,0"
                HorizontalAlignment="Left" VerticalAlignment="Center" SnapsToDevicePixels="True">
            <StackPanel Orientation="Horizontal">
                <Grid Width="6" Height="6" Margin="0,0,7,0" VerticalAlignment="Center">
                    <Ellipse x:Name="DotGlow" Width="10" Height="10" Margin="-2" Visibility="Collapsed"
                             Fill="{TemplateBinding Foreground}" Opacity="{DynamicResource Glow.Opacity}" IsHitTestVisible="False">
                        <Ellipse.Effect>
                            <BlurEffect Radius="6" />
                        </Ellipse.Effect>
                    </Ellipse>
                    <Ellipse Fill="{TemplateBinding Foreground}" />
                </Grid>
                <ContentPresenter VerticalAlignment="Center" TextElement.FontSize="12" TextElement.FontWeight="Medium" />
            </StackPanel>
        </Border>
        <ControlTemplate.Triggers>
            <Trigger Property="Tag" Value="Glow">
                <Setter TargetName="DotGlow" Property="Visibility" Value="Visible" />
            </Trigger>
        </ControlTemplate.Triggers>
    </ControlTemplate>

    <Style x:Key="Hue.Pill" TargetType="ContentControl">
        <Setter Property="Focusable" Value="False" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.TextDim}" />
        <Setter Property="Background" Value="{DynamicResource Brush.Raised}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.Line}" />
        <Setter Property="Template" Value="{StaticResource Hue.PillTemplate}" />
    </Style>
    <Style x:Key="Hue.Pill.Accent" TargetType="ContentControl" BasedOn="{StaticResource Hue.Pill}">
        <Setter Property="Foreground" Value="{DynamicResource Brush.AccentText}" />
        <Setter Property="Background" Value="{DynamicResource Brush.AccentSoft}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.AccentEdge}" />
    </Style>
    <Style x:Key="Hue.Pill.Ok" TargetType="ContentControl" BasedOn="{StaticResource Hue.Pill}">
        <Setter Property="Foreground" Value="{DynamicResource Brush.OkText}" />
        <Setter Property="Background" Value="{DynamicResource Brush.OkSoft}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.OkEdge}" />
    </Style>
    <Style x:Key="Hue.Pill.Warn" TargetType="ContentControl" BasedOn="{StaticResource Hue.Pill}">
        <Setter Property="Foreground" Value="{DynamicResource Brush.WarnText}" />
        <Setter Property="Background" Value="{DynamicResource Brush.WarnSoft}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.WarnEdge}" />
    </Style>
    <Style x:Key="Hue.Pill.Limit" TargetType="ContentControl" BasedOn="{StaticResource Hue.Pill}">
        <Setter Property="Foreground" Value="{DynamicResource Brush.LimitText}" />
        <Setter Property="Background" Value="{DynamicResource Brush.LimitSoft}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.LimitEdge}" />
    </Style>
    <Style x:Key="Hue.Pill.Error" TargetType="ContentControl" BasedOn="{StaticResource Hue.Pill}">
        <Setter Property="Foreground" Value="{DynamicResource Brush.ErrorText}" />
        <Setter Property="Background" Value="{DynamicResource Brush.ErrorSoft}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.ErrorEdge}" />
    </Style>
    <Style x:Key="Hue.Pill.Stale" TargetType="ContentControl" BasedOn="{StaticResource Hue.Pill}">
        <Setter Property="Foreground" Value="{DynamicResource Brush.StaleText}" />
        <Setter Property="Background" Value="{DynamicResource Brush.StaleSoft}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.StaleEdge}" />
    </Style>

    <!-- Marker dots. Foreground is the colour. -->
    <Style x:Key="Hue.Marker" TargetType="ContentControl">
        <Setter Property="Focusable" Value="False" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Accent}" />
        <Setter Property="VerticalAlignment" Value="Center" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ContentControl">
                    <Grid Width="8" Height="8">
                        <Ellipse x:Name="DotGlow" Width="12" Height="12" Margin="-2" Visibility="Collapsed"
                                 Fill="{TemplateBinding Foreground}" Opacity="{DynamicResource Glow.Opacity}" IsHitTestVisible="False">
                            <Ellipse.Effect>
                                <BlurEffect Radius="8" />
                            </Ellipse.Effect>
                        </Ellipse>
                        <Ellipse Fill="{TemplateBinding Foreground}" />
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="Tag" Value="Glow">
                            <Setter TargetName="DotGlow" Property="Visibility" Value="Visible" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style x:Key="Hue.Marker.Glow" TargetType="ContentControl" BasedOn="{StaticResource Hue.Marker}">
        <Setter Property="Tag" Value="Glow" />
    </Style>

    <!-- ================================================================== WINDOWS
         Applies to plain Window instances (dialogs created in code). Your own window classes
         need Style="{StaticResource Hue.Window}". -->
    <Style TargetType="Window" BasedOn="{StaticResource Hue.Window}" />


</ResourceDictionary>
""";
    }

    /// <summary>Hands the colours back to Windows while a contrast theme is on. Someone who
    /// has chosen a contrast theme has chosen their colours; a dark palette laid over it
    /// takes that away. From the HueTheme kit, unchanged apart from the namespace and the
    /// busy-overlay keys this app does not define.</summary>
    internal static class HueContrast
    {
        static ResourceDictionary? _overrides;
        static Application? _app;

        /// <summary>True while Windows has a contrast theme on.</summary>
        public static bool IsActive => SystemParameters.HighContrast;

        /// <summary>Raised on the UI thread after the brushes have been swapped either way.</summary>
        public static event EventHandler? Changed;

        public static void Follow(Application app)
        {
            if (_app is not null) return;
            _app = app;

            SystemParameters.StaticPropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SystemParameters.HighContrast)) Refresh();
            };
            // Switching from one contrast theme to another changes colours, not HighContrast.
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility) Refresh();
            };

            Update();
        }

        static void Refresh() => _app?.Dispatcher.BeginInvoke(new Action(Update));

        static void Update()
        {
            if (_app is null) return;
            var merged = _app.Resources.MergedDictionaries;

            if (_overrides is not null)
            {
                merged.Remove(_overrides);
                _overrides = null;
            }

            if (IsActive)
            {
                // Added last, so it wins over the theme and anything merged before it.
                _overrides = Build();
                merged.Add(_overrides);
            }

            foreach (Window window in _app.Windows)
                HueTitleBar.Apply(window);

            Changed?.Invoke(null, EventArgs.Empty);
        }

        static ResourceDictionary Build()
        {
            var window = SystemColors.WindowBrush;
            var text = SystemColors.WindowTextBrush;
            var gray = SystemColors.GrayTextBrush;
            var face = SystemColors.ControlBrush;
            var highlight = SystemColors.HighlightBrush;
            var onHighlight = SystemColors.HighlightTextBrush;

            var d = new ResourceDictionary();
            void Set(Brush brush, params string[] keys)
            {
                foreach (var key in keys) d[key] = brush;
            }

            Set(window, "Brush.Base", "Brush.Chrome", "Brush.Surface", "Brush.Hover",
                "Brush.AccentSoft", "Brush.AccentSoftHover", "Brush.OnStatus",
                "Brush.OkSoft", "Brush.WarnSoft", "Brush.LimitSoft", "Brush.ErrorSoft", "Brush.StaleSoft");
            Set(face, "Brush.Raised");
            Set(text, "Brush.LineSoft", "Brush.Line", "Brush.LineStrong", "Brush.ControlEdge",
                "Brush.Text", "Brush.TextDim", "Brush.TextMuted",
                "Brush.Ok", "Brush.OkText", "Brush.OkEdge", "Brush.Warn", "Brush.WarnText", "Brush.WarnEdge",
                "Brush.Limit", "Brush.LimitText", "Brush.LimitEdge", "Brush.Error", "Brush.ErrorText", "Brush.ErrorEdge",
                "Brush.Stale", "Brush.StaleText", "Brush.StaleEdge");
            Set(gray, "Brush.TextDisabled");
            Set(highlight, "Brush.Accent", "Brush.AccentMid", "Brush.AccentLight", "Brush.AccentText",
                "Brush.AccentFill", "Brush.AccentFillHover", "Brush.AccentEdge", "Brush.Glow",
                "Brush.ErrorSolid", "Brush.ErrorSolidHover",
                // The two off-family buttons collapse into the system highlight like every
                // other fill: someone running a contrast theme has chosen their colours, and
                // an exception of ours is not a reason to keep painting over them.
                "Brush.IndigoFill", "Brush.IndigoFillHover", "Brush.AmberFill", "Brush.AmberFillHover");
            Set(onHighlight, "Brush.OnAccent", "Brush.OnError", "Brush.OnIndigo", "Brush.OnAmber");

            d["Glow.Opacity"] = 0.0;
            return d;
        }
    }

    /// <summary>A dark title bar, coloured from the theme: caption = Brush.Chrome, caption
    /// text = Brush.Text, border = the accent blended into Chrome. Dark mode needs Windows 10
    /// 1809; the three colours need Windows 11 and are ignored below that, which leaves the
    /// plain dark caption. While a contrast theme is on the caption goes back to Windows.
    /// From the HueTheme kit, unchanged apart from the namespace and a try/catch so a DWM
    /// refusal can never stop the window opening.</summary>
    internal static class HueTitleBar
    {
        const int DwmwaUseImmersiveDarkModeOld = 19;
        const int DwmwaUseImmersiveDarkMode = 20;
        const int DwmwaBorderColor = 34;
        const int DwmwaCaptionColor = 35;
        const int DwmwaTextColor = 36;
        const int DwmwaColorDefault = unchecked((int)0xFFFFFFFF);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void Attach(Window window)
        {
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
                Apply(window);
            else
                window.SourceInitialized += (_, _) => Apply(window);
        }

        public static void Apply(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                if (SystemParameters.HighContrast)
                {
                    int off = 0;
                    if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref off, sizeof(int)) != 0)
                        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeOld, ref off, sizeof(int));
                    foreach (var attribute in new[] { DwmwaCaptionColor, DwmwaTextColor, DwmwaBorderColor })
                    {
                        int system = DwmwaColorDefault;
                        DwmSetWindowAttribute(hwnd, attribute, ref system, sizeof(int));
                    }
                    return;
                }

                int on = 1;
                if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeOld, ref on, sizeof(int));

                var chrome = Find(window, "Brush.Chrome");
                var text = Find(window, "Brush.Text");
                var accent = Find(window, "Brush.Accent");

                if (chrome is { } c) Set(hwnd, DwmwaCaptionColor, c);
                if (text is { } t) Set(hwnd, DwmwaTextColor, t);
                if (chrome is { } bg && accent is { } fg) Set(hwnd, DwmwaBorderColor, Blend(bg, fg, 0.55));
            }
            catch { /* cosmetic; never stop the window opening over it */ }
        }

        static Color? Find(Window window, string key) =>
            (window.TryFindResource(key) as SolidColorBrush)?.Color;

        static void Set(IntPtr hwnd, int attribute, Color color)
        {
            int colorRef = color.R | (color.G << 8) | (color.B << 16);   // COLORREF is 0x00BBGGRR
            DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(int));
        }

        static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }

    public sealed class MainWindow : Window
    {
        // Theme token keys, not brushes. Everything the app colours from code goes through
        // SetResourceReference with one of these, so a swap of the dictionary — which is
        // what HueContrast does when Windows turns a contrast theme on — reaches it. A
        // brush captured in a field would keep its colour and be left behind.
        const string Ink = "Brush.Text";
        const string InkDim = "Brush.TextDim";
        const string InkMuted = "Brush.TextMuted";
        const string InkAccent = "Brush.AccentText";
        const string InkOk = "Brush.OkText";
        const string InkWarn = "Brush.WarnText";
        const string InkError = "Brush.ErrorText";

        readonly TextBox _source = new();
        readonly ComboBox _project = new();
        readonly ComboBox _rid = new();
        readonly ComboBox _verbosity = new();
        readonly CheckBox _trim = new() { Content = "Trim" };
        readonly CheckBox _r2r = new() { Content = "ReadyToRun" };

        /// <summary>On by default; PublishArgs says what it changes and why.</summary>
        readonly CheckBox _repro = new()
        {
            Content = "Reproducible",
            IsChecked = true,
            ToolTip = "Adds -p:ContinuousIntegrationBuild=true. Source paths baked into the " +
                      "exe become /_/ instead of the random temp folder, so the same zip " +
                      "built on two machines with the same SDK should give identical bytes."
        };
        readonly TextBox _extraArgs = new();
        readonly TextBox _output = new();
        readonly Button _build = new() { Content = Labelled("Icon.Play", "Build exe") };
        readonly Button _rebuildAll = new()
        {
            Content = Labelled("Icon.Refresh", "Rebuild all"),
            ToolTip = "Rebuild the newest zip of every project in Save to\\HISTORY with the " +
                      "current settings, replace only the exes that changed, and write a " +
                      "dated .sha256 record of the result."
        };
        readonly Button _cancel = new() { Content = Labelled("Icon.Stop", "Cancel"), IsEnabled = false };

        /// <summary>Set by Cancel; checked between projects so a batch stops after the
        /// one it is on rather than starting the next.</summary>
        bool _batchCancel;
        readonly Button _openOutput = new() { Content = Labelled("Icon.Open", "Open folder"), IsEnabled = false };
        readonly RichTextBox _log = new();
        readonly TabControl _tabs = new();
        readonly StackPanel _statusPanel = new();

        /// <summary>The Packages tab. Filled on demand rather than at startup, so a large
        /// cache is only walked when someone asks to see it.</summary>
        readonly StackPanel _packagesPanel = new();
        readonly List<(string Text, string Ink)> _narrative = new();
        readonly List<(string Label, Func<Task> Run)> _actions = new();
        // The dot and the word are one unit in the suite, so the strip never shows the
        // marker on its own. "Ready" is the resting state; the dot stays neutral until
        // something has happened to report.
        readonly TextBlock _status = new() { Text = "Ready" };

        /// <summary>The status strip's dot. Colour says how the last thing went; the words
        /// beside it always say what it was, so the colour is never the only cue.</summary>
        readonly ContentControl _statusDot = new();

        /// <summary>The one progress bar the window is about, so it takes the glow the
        /// suite reserves for a single standalone bar. Hidden rather than collapsed, so the
        /// row keeps its height and nothing below it moves when a build starts.</summary>
        readonly ProgressBar _progress = new()
        {
            IsIndeterminate = false,
            Visibility = Visibility.Hidden,
            Tag = "Glow"
        };

        readonly List<string> _tempDirs = new();
        readonly DispatcherTimer _sweep = new();
        string _repoRoot = "";

        /// <summary>The .zip the current source came from, empty for a folder. Kept so
        /// the extraction can be thrown away after a successful build and rebuilt on
        /// demand from the same path — which is also how a replacement zip dropped at
        /// that path gets picked up without re-browsing.</summary>
        string _sourceZip = "";

        /// <summary>The guid folder the zip was unpacked into, which is the parent of
        /// _repoRoot when a wrapper folder was collapsed.</summary>
        string _extractRoot = "";

        /// <summary>Project the user had picked, relative to the repo root, kept across
        /// a discard so the same one can be reselected when the zip is unpacked again.</summary>
        string _wantedProjectRel = "";

        /// <summary>Where the source zip was filed after the last successful build,
        /// so the "it has gone" message can say where it went.</summary>
        string _archivedTo = "";

        bool _useArtifactsPath = true;
        Process? _current;
        bool _sawRestoreSourceError;
        double? _sdkSizeMb;
        double? _packagesSizeMb;

        public MainWindow()
        {
            Title = "RepoBuilder";
            Width = 980;
            Height = 720;
            MinWidth = 760;
            MinHeight = 520;
            // An implicit Window style does not reach a class derived from Window, so the
            // suite's window style is asked for by name.
            SetResourceReference(StyleProperty, "Hue.Window");
            HueTitleBar.Attach(this);
            AllowDrop = true;

            Content = BuildLayout();
            WireUp();
            SweepStaleTemp();
            SweepSelfExtract();
            RefreshSizes();

            _elapsed.SetResourceReference(StyleProperty, "Hue.Pill.Warn");
            _elapsed.SetResourceReference(Control.FontFamilyProperty, "Font.Mono");
            _clock.Tick += (_, _) => ShowElapsed();

            // The startup sweep alone never fires on a window left open for days.
            _sweep.Interval = TimeSpan.FromMinutes(30);
            _sweep.Tick += (_, _) => SweepStaleTemp();
            _sweep.Start();

            if (TempRootIsFallback)
            {
                Note($"Build files go to {TempRoot}.", InkMuted);
                Note("Want them elsewhere? Create a folder named TEMP at the root of a fixed " +
                     "drive — D:\\TEMP, say — and RepoBuilder will use it from the next start, " +
                     "the same way Save to picks up a \\REPO folder.", InkMuted);
            }

            if (!DotnetLocator.HasPortablePackages)
            {
                Note("Heading for a machine with no network later on? Load the repo you intend " +
                     "to build there, then use the button below while this machine still has " +
                     "network.", InkMuted);
                AddAction("Prime an offline packages folder", PrimePackages);
            }
            else RenderStatus();
        }

        // ------------------------------------------------------------ layout

        UIElement BuildLayout()
        {
            // The suite layout: the header strip and the status strip run edge to edge,
            // the content between them is padded. The progress bar is a hairline between
            // the body and the status strip, so neither of them changes height when a
            // build starts.
            var root = new Grid();
            foreach (var height in new[]
                     {
                         GridLength.Auto,                            // header strip
                         GridLength.Auto,                            // the form
                         new GridLength(1, GridUnitType.Star),       // tabs
                         GridLength.Auto,                            // progress
                         GridLength.Auto                             // status strip
                     })
                root.RowDefinitions.Add(new RowDefinition { Height = height });

            var form = new StackPanel { Margin = new Thickness(24, 18, 24, 0) };
            form.Children.Add(SourceRow());
            form.Children.Add(ProjectRow());
            form.Children.Add(OptionsRow());
            form.Children.Add(OutputRow());

            var tabs = LogPanel();
            ((FrameworkElement)tabs).Margin = new Thickness(24, 4, 24, 16);

            _progress.Margin = new Thickness(0);

            root.Children.Add(Row(0, Header()));
            root.Children.Add(Row(1, form));
            root.Children.Add(Row(2, tabs));
            root.Children.Add(Row(3, _progress));
            root.Children.Add(Row(4, StatusRow()));
            return root;
        }

        /// <summary>A value stamped in by one of the csproj's AssemblyMetadata items
        /// (BuiltWithSdk, BuiltWithRuntime). Read from the assembly's own attributes,
        /// which live inside the single-file bundle, so no file path is involved.
        /// Empty if the stamp is missing, and the header then shows nothing for it
        /// rather than guessing.</summary>
        static string BuildStamp(string key)
        {
            try
            {
                return typeof(MainWindow).Assembly
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? "";
            }
            catch { return ""; }
        }

        const string SdkDownloadUrl = "https://dotnet.microsoft.com/download";

        /// <summary>Recolours the panel's text lines, optionally leaving the first one alone.</summary>
        static void Recolour(Panel panel, string ink, bool skip = false)
        {
            foreach (var child in panel.Children.OfType<TextBlock>().Skip(skip ? 1 : 0))
                child.SetResourceReference(TextBlock.ForegroundProperty, ink);
        }

        /// <summary>The csproj's Version, as major.minor.build. Read from the informational
        /// version, which the csproj keeps free of a source-revision suffix; anything after
        /// a '+' is cut regardless, so a build from a git checkout still shows the bare
        /// number. Empty if absent, and the header then shows no version line.</summary>
        static string AppVersion()
        {
            try
            {
                var text = typeof(MainWindow).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion ?? "";
                var plus = text.IndexOf('+');
                return (plus >= 0 ? text[..plus] : text).Trim();
            }
            catch { return ""; }
        }

        static TextBlock StampLine(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, InkMuted);
            return block;
        }

        UIElement Header()
        {
            var grid = new Grid();
            // Equal star columns either side of the clock put it in the middle of the bar
            // rather than crowding the SDK stamp. The floor on the text column is set
            // below, once the title and tagline exist to be measured.
            var textColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 0 icon
            grid.ColumnDefinitions.Add(textColumn);                                         // 1 title + tagline
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 2 elapsed
            grid.ColumnDefinitions.Add(new ColumnDefinition                                 // 3 mirror of 1
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 4 SDK stamp

            // Which SDK built this exe, and which runtime it packed inside. Together
            // that is the shopping list for anyone who doubts the binary: fetch that
            // SDK and those runtime packs, rebuild from the same source, compare bytes.
            // The version on top says which source this is; the two lines below say which
            // toolchain turned it into this exe.
            var version = AppVersion();
            var sdk = BuildStamp("BuiltWithSdk");
            var runtime = BuildStamp("BuiltWithRuntime");
            if (version.Length > 0 || sdk.Length > 0 || runtime.Length > 0)
            {
                var stamp = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(16, 2, 0, 0),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent,   // hit-testable, not a colour
                    ToolTip = "Which source this is, and what built it. Rebuild the same source " +
                              "with this SDK and these runtime packs and the result should be " +
                              "byte-identical.\n" +
                              "Click to open the .NET download page and see whether a newer " +
                              "SDK is out."
                };
                if (version.Length > 0)
                {
                    var line = new TextBlock
                    {
                        Text = $"version {version}",
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 0, 0, 2)
                    };
                    line.SetResourceReference(FrameworkElement.StyleProperty, "Text.Heading");
                    stamp.Children.Add(line);
                }
                if (sdk.Length > 0)
                    stamp.Children.Add(StampLine($"built with SDK {sdk}"));
                if (runtime.Length > 0)
                    stamp.Children.Add(StampLine($"runtime {runtime}"));

                // Same page the no-SDK case offers, so both point at one place.
                stamp.MouseLeftButtonUp += (_, _) => OpenUrl(SdkDownloadUrl);
                // The muted lines take the accent on hover; the version keeps its style.
                stamp.MouseEnter += (_, _) => Recolour(stamp, InkAccent, skip: version.Length > 0);
                stamp.MouseLeave += (_, _) => Recolour(stamp, InkMuted, skip: version.Length > 0);

                Add(grid, 4, stamp);
            }

            var art = LoadEmbeddedImage("app_256.png");
            if (art is not null)
            {
                var image = new Image { Source = art };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

                var halo = new ContentControl { Content = image, Width = 60, Height = 60 };
                halo.SetResourceReference(FrameworkElement.StyleProperty, "Hue.IconGlow");
                Add(grid, 0, halo);
                Icon = art;   // title bar and taskbar
            }

            var title = new TextBlock { Text = "RepoBuilder" };
            title.SetResourceReference(FrameworkElement.StyleProperty, "Text.Title");

            var tagline = new TextBlock
            {
                Text = "Turns a repo folder or zip into one self-contained .exe — no IDE needed",
                Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            tagline.SetResourceReference(FrameworkElement.StyleProperty, "Text.Body");

            var text = new StackPanel
            {
                Margin = new Thickness(art is null ? 0 : 16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            text.Children.Add(title);
            text.Children.Add(tagline);

            // Two equal stars would otherwise hand the tagline half the bar and trim it
            // on a narrow window, because a star column ignores what its child wants.
            // Measuring the block unconstrained gives the column a floor at its natural
            // width: the tagline stays whole and the clock slides right of centre
            // instead, which is the better thing to give up when space runs out.
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            textColumn.MinWidth = text.DesiredSize.Width;

            Add(grid, 1, text);
            Add(grid, 2, _elapsed);   // its own column, so it cannot crowd the tagline

            var header = new Border { Child = grid };
            header.SetResourceReference(FrameworkElement.StyleProperty, "Hue.Header");
            return header;
        }

        /// <summary>Reads an image compiled in as an EmbeddedResource. Matches on the
        /// file name so the namespace prefix does not have to be hard-coded.</summary>
        static BitmapImage? LoadEmbeddedImage(string fileName)
        {
            try
            {
                var assembly = typeof(MainWindow).Assembly;
                var name = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
                if (name is null) return null;

                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) return null;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = stream;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;   // stream can close after EndInit
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;   // header simply renders without the mark
            }
        }

        static UIElement Row(int index, UIElement child)
        {
            Grid.SetRow(child, index);
            return child;
        }

        UIElement SourceRow()
        {
            var browseFolder = new Button
            {
                Content = Labelled("Icon.Folder", "Folder…"),
                Margin = new Thickness(8, 0, 0, 0)
            };
            var browseZip = new Button
            {
                Content = Labelled("Icon.Import", "Zip…"),
                Margin = new Thickness(8, 0, 0, 0)
            };
            browseZip.SetResourceReference(StyleProperty, "Hue.Button.Amber");
            browseFolder.Click += (_, _) => PickFolder();
            browseZip.Click += (_, _) => PickZip();
            _lockedWhileBusy.Add(browseFolder);
            _lockedWhileBusy.Add(browseZip);

            _source.IsReadOnly = true;

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Add(grid, 0, Caption("Repo"));
            Add(grid, 1, _source);
            Add(grid, 2, browseFolder);
            Add(grid, 3, browseZip);
            return grid;
        }

        UIElement ProjectRow()
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _project.IsEnabled = false;
            Add(grid, 0, Caption("Project"));
            Add(grid, 1, _project);
            return grid;
        }

        UIElement OptionsRow()
        {
            foreach (var rid in new[] { "win-x64", "win-arm64", "win-x86" }) _rid.Items.Add(rid);
            _rid.SelectedIndex = 0;
            _rid.Width = 120;

            foreach (var v in new[] { "minimal", "normal", "detailed", "diagnostic" }) _verbosity.Items.Add(v);
            _verbosity.SelectedIndex = 0;
            _verbosity.Width = 110;

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(74, 0, 0, 10)
            };
            panel.Children.Add(_rid);
            panel.Children.Add(Spacer(16));
            panel.Children.Add(_trim);
            panel.Children.Add(Spacer(16));
            panel.Children.Add(_r2r);
            panel.Children.Add(Spacer(16));
            panel.Children.Add(_repro);
            panel.Children.Add(Spacer(16));
            panel.Children.Add(_verbosity);

            var args = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            args.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            args.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Add(args, 0, Caption("Args"));
            Add(args, 1, _extraArgs);

            var wrapper = new StackPanel();
            wrapper.Children.Add(panel);
            wrapper.Children.Add(args);
            return wrapper;
        }

        /// <summary>Prefers an existing \REPO on any fixed drive, taking the lowest
        /// drive letter. Falls back to the system drive if none exists yet.</summary>
        static string DefaultOutputFolder()
        {
            try
            {
                foreach (var drive in DriveInfo.GetDrives()
                             .Where(d => d.DriveType == DriveType.Fixed)
                             .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (!drive.IsReady) continue;
                    var candidate = Path.Combine(drive.RootDirectory.FullName, "REPO");
                    if (Directory.Exists(candidate)) return candidate;
                }
            }
            catch { /* fall through */ }

            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            return Path.Combine(systemRoot, "REPO");
        }

        UIElement OutputRow()
        {
            _output.IsReadOnly = false;
            _output.Text = DefaultOutputFolder();

            var browse = new Button
            {
                Content = Labelled("Icon.Folder", "Change…"),
                Margin = new Thickness(8, 0, 0, 0)
            };
            browse.Click += (_, _) => PickOutput();
            _lockedWhileBusy.Add(browse);
            // Typed into as well as browsed for, so the box locks with the button.
            _lockedWhileBusy.Add(_output);

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // One primary button in the view: the thing the window is for. Rebuild all is
            // indigo, a stated exception (see the README's notes on the look); the rest
            // stay neutral.
            _build.Margin = new Thickness(16, 0, 0, 0);
            _build.SetResourceReference(StyleProperty, "Hue.Button.Primary");
            _rebuildAll.Margin = new Thickness(8, 0, 0, 0);
            _rebuildAll.SetResourceReference(StyleProperty, "Hue.Button.Indigo");
            _cancel.Margin = new Thickness(8, 0, 0, 0);
            _openOutput.Margin = new Thickness(8, 0, 0, 0);

            Add(grid, 0, Caption("Save to"));
            Add(grid, 1, _output);
            Add(grid, 2, browse);
            Add(grid, 3, _build);
            Add(grid, 4, _rebuildAll);
            Add(grid, 5, _cancel);
            Add(grid, 6, _openOutput);
            return grid;
        }

        UIElement LogPanel()
        {
            _log.IsReadOnly = true;
            _log.IsDocumentEnabled = true;
            _log.Background = Brushes.Transparent;
            _log.BorderThickness = new Thickness(0);
            _log.FontSize = 12;
            _log.SetResourceReference(Control.ForegroundProperty, Ink);
            _log.SetResourceReference(Control.FontFamilyProperty, "Font.Mono");
            _log.Padding = new Thickness(14, 12, 14, 12);
            _log.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            // No fixed page width and no horizontal bar: the document wraps at whatever the
            // window is, so a long publish command line breaks where it can be read instead
            // of running off the side. Wrapping is visual only — Copy log still yields the
            // original line breaks.
            _log.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _log.Document.Blocks.Clear();
            _log.Document.Blocks.Add(new Paragraph { Margin = new Thickness(0) });

            _statusPanel.Margin = new Thickness(20, 16, 20, 18);

            // Every readiness row sizes its pill column from the widest pill on the tab, so
            // the words beside them line up and the longest one cannot be clipped.
            Grid.SetIsSharedSizeScope(_statusPanel, true);

            var statusScroll = new ScrollViewer
            {
                Content = _statusPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            _packagesPanel.Margin = new Thickness(20, 16, 20, 18);
            var packagesScroll = new ScrollViewer
            {
                Content = _packagesPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            _tabs.Items.Add(new TabItem { Header = "Status", Content = statusScroll });
            _tabs.Items.Add(new TabItem { Header = "Output", Content = _log });
            _tabs.Items.Add(new TabItem { Header = "Packages", Content = packagesScroll });
            _tabs.SelectedIndex = 0;
            return _tabs;
        }

        // ------------------------------------------------------------- status pane

        // Everything on the Status tab is written by the app from what it knows, never
        // derived from dotnet's output — which is localised and would break the moment the
        // machine language changes.

        /// <summary>Guards against re-entering RenderStatus. UpdateTrimAvailability can
        /// clear the Trim box, whose handler calls RenderStatus again; without this the
        /// inner pass would rebuild the panel and the outer one would then append a
        /// second copy of everything.</summary>
        bool _rendering;

        void RenderStatus()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RenderStatus));
                return;
            }
            if (_rendering) return;

            _rendering = true;
            try { RenderStatusCore(); }
            finally { _rendering = false; }
        }

        void RenderStatusCore()
        {
            _statusPanel.Children.Clear();
            UpdateTrimAvailability();

            var dotnet = DotnetLocator.Find();
            List<ReadinessItem> items = new();
            if (_project.SelectedItem is ProjectInfo project && PackageCache.Exists)
                items = PackageCache.Check(project, (string)_rid.SelectedItem,
                                           _trim.IsChecked == true, _r2r.IsChecked == true);

            _statusPanel.Children.Add(Verdict(dotnet, items));
            _statusPanel.Children.Add(ToolchainCards(dotnet));

            if (items.Count > 0)
            {
                var tools = items.Where(i => i.Group == "tools").ToList();
                var repo = items.Where(i => i.Group == "repo").ToList();
                if (tools.Count > 0)
                {
                    _statusPanel.Children.Add(Heading("Build tools"));
                    _statusPanel.Children.Add(RowGroup(tools));
                }
                if (repo.Count > 0)
                {
                    _statusPanel.Children.Add(Heading("This repo's own packages"));
                    _statusPanel.Children.Add(RowGroup(repo));
                }
            }

            if (_narrative.Count > 0)
            {
                _statusPanel.Children.Add(Heading("Progress"));
                foreach (var (text, ink) in _narrative)
                    _statusPanel.Children.Add(Line(text, ink));
            }

            // The buttons from the last render are gone with the panel's children.
            _statusActions.Clear();

            if (_actions.Count > 0)
            {
                _statusPanel.Children.Add(Heading("What to do"));
                var panel = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                for (int i = 0; i < _actions.Count; i++)
                {
                    var (label, run) = _actions[i];
                    var button = new Button
                    {
                        Content = $"{i + 1}.  {label}",
                        Margin = new Thickness(0, 4, 8, 0),
                        MinWidth = 0,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        // These move packages about and create folders. A render can happen
                        // mid-run, so they start disabled if one is already going and SetBusy
                        // turns them back on at the end.
                        IsEnabled = !_busy
                    };
                    button.Click += async (_, _) => await run();
                    _statusActions.Add(button);
                    panel.Children.Add(button);
                }
                _statusPanel.Children.Add(panel);
            }
        }

        /// <summary>The one message the Status tab leads with, as the suite's callout.
        /// Critical only when nothing can be built; a missing package is a warning, since
        /// pressing Build exe is still safe and says what it wanted.</summary>
        UIElement Verdict(string? dotnet, List<ReadinessItem> items)
        {
            string headline, detail, style;

            if (dotnet is null)
            {
                headline = "No .NET SDK on this machine";
                detail = "Nothing can be built until one is unpacked or installed.";
                style = "Hue.Callout.Critical";
            }
            else if (!PackageCache.Exists)
            {
                headline = "Ready to build with a network connection";
                detail = "Nothing is stored locally yet, so the build will fetch what it needs.";
                style = "Hue.Callout.Warn";
            }
            else if (items.Any(i => i.State == Readiness.Missing))
            {
                headline = "Something is missing";
                detail = "You can still press Build exe. Without a network it will stop after a " +
                         "short wait and name what it wanted. Nothing is harmed by trying.";
                style = "Hue.Callout.Warn";
            }
            else if (items.Count == 0)
            {
                headline = "Ready — no repo loaded yet";
                detail = "Drop a repo folder or a .zip anywhere on this window.";
                style = "Hue.Callout";
            }
            else
            {
                headline = "Ready to build offline";
                detail = "Everything this repo names is already stored locally.";
                style = "Hue.Callout.Ok";
            }

            var stack = new StackPanel();

            // No Foreground on the title: it inherits the callout's, which is what carries
            // the severity. The detail line is dim, as the suite's callouts are.
            stack.Children.Add(new TextBlock
            {
                Text = headline,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });

            var body = new TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            };
            body.SetResourceReference(FrameworkElement.StyleProperty, "Text.Body");
            stack.Children.Add(body);

            var callout = new ContentControl { Content = stack, Margin = new Thickness(0, 0, 0, 16) };
            callout.SetResourceReference(FrameworkElement.StyleProperty, style);
            return callout;
        }

        /// <summary>Trim is offered only where it can work. A desktop project gets the box
        /// cleared, disabled and explained, rather than a build that fails or, worse, an
        /// exe that dies the first time it opens a window.</summary>
        void UpdateTrimAvailability()
        {
            var desktop = _project.SelectedItem is ProjectInfo project && project.UsesDesktop;
            if (desktop && _trim.IsChecked == true) _trim.IsChecked = false;
            // This runs on every re-render, including the ones each note causes mid-build,
            // so it must respect _busy or it would unlock the box during a run.
            _trim.IsEnabled = !_busy && !desktop;
            _trim.ToolTip = desktop
                ? "Not available: this is a WPF or WinForms project. Trimming drops code it " +
                  "cannot see being used, and XAML resolves types by name at runtime, so the " +
                  "result would be broken. Microsoft does not support it for desktop apps."
                : "Removes IL that nothing references. Not usable by WPF or WinForms projects.";
        }

        UIElement ToolchainCards(string? dotnet)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // --- SDK ---------------------------------------------------------
            string size = _sdkSizeMb is null ? "measuring…" : $"{_sdkSizeMb:F0} MB";
            string version = "not found";
            string where = "";

            if (dotnet is not null)
            {
                var sdks = DotnetLocator.SdkVersions(dotnet);
                version = sdks.Count switch
                {
                    0 => "runtime only, no SDK",
                    1 => $"{sdks[0].Version} · unpacked {sdks[0].Stamp:d MMM yyyy}",
                    _ => $"{sdks[0].Version} and {sdks.Count - 1} more · newest unpacked {sdks[0].Stamp:d MMM yyyy}"
                };
                where = (DotnetLocator.HasPortable ? "portable · " : "") + DotnetLocator.RootOf(dotnet);
            }

            var sdkCard = Card("SDK", dotnet is null ? "—" : size, version, where);
            if (DotnetLocator.HasPortable)
            {
                // The check sits in the card's free right half. The panel is kept across
                // renders so a pasted hash survives them; it only moves to the new card.
                var panel = SdkCheckPanel();
                if (panel.Parent is Panel old) old.Children.Remove(panel);

                var split = new Grid();
                split.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var left = sdkCard.Child;
                sdkCard.Child = null;
                Add(split, 0, left);
                Add(split, 1, panel);
                sdkCard.Child = split;
            }
            Grid.SetColumn(sdkCard, 0);
            sdkCard.Margin = new Thickness(0, 0, 6, 0);
            grid.Children.Add(sdkCard);

            // --- packages ----------------------------------------------------
            var pkgCard = PackageCache.Exists
                ? Card("Packages",
                       _packagesSizeMb is null ? "measuring…" : $"{_packagesSizeMb:F0} MB",
                       $"{PackageCache.Count(PackageCache.Read())} stored",
                       DotnetLocator.PortablePackages)
                : Card("Packages", "none", "nothing stored next to this exe", "");

            // Clickable either way: with no cache the tab is where it says so, and where it
            // would have looked.
            pkgCard.Cursor = Cursors.Hand;
            pkgCard.ToolTip = "Click for what is stored, on the Packages tab.";
            pkgCard.MouseLeftButtonUp += (_, _) => ListPackages();
            Grid.SetColumn(pkgCard, 1);
            pkgCard.Margin = new Thickness(6, 0, 0, 0);
            grid.Children.Add(pkgCard);

            grid.Margin = new Thickness(0, 0, 0, 4);
            return grid;
        }

        // ------------------------------------------------------------- SDK check

        readonly Button _checkSdk = new()
        {
            Content = "Check SDK",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "Checks the SDK zip in TOOLCHAIN against its .sha512 and the hash pasted " +
                      "above, and the sdk folder against the zip, byte for byte."
        };
        readonly TextBox _sdkHash = new();
        StackPanel? _sdkCheckPanel;

        StackPanel SdkCheckPanel()
        {
            if (_sdkCheckPanel is not null) return _sdkCheckPanel;

            var caption = new TextBlock { Text = "SHA-512 from the download page (optional)" };
            caption.SetResourceReference(FrameworkElement.StyleProperty, "Text.Caption");
            _sdkHash.Margin = new Thickness(0, 4, 0, 0);
            _sdkHash.SetResourceReference(Control.FontFamilyProperty, "Font.Mono");
            _checkSdk.SetResourceReference(StyleProperty, "Hue.Button.Tonal");
            _checkSdk.Click += async (_, _) => await CheckSdkAsync();
            _lockedWhileBusy.Add(_checkSdk);
            _lockedWhileBusy.Add(_sdkHash);

            _sdkCheckPanel = new StackPanel
            {
                Margin = new Thickness(24, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _sdkCheckPanel.Children.Add(caption);
            _sdkCheckPanel.Children.Add(_sdkHash);
            _sdkCheckPanel.Children.Add(_checkSdk);
            return _sdkCheckPanel;
        }

        /// <summary>Where the SDK zip and its .sha512 are kept: beside the exe, like sdk and
        /// packages.</summary>
        static string ToolchainFolder =>
            Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", "TOOLCHAIN");

        /// <summary>A SHA-512 as 128 lowercase hex digits from whatever was pasted or stored —
        /// spaces, line breaks and a trailing " *name" are dropped — or "" if it is not one.</summary>
        static string NormalizeSha512(string text)
        {
            var first = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault() ?? "";
            // The first word is the hash in a .sha512 file; a paste wrapped over several
            // lines only becomes whole once the whitespace is taken out.
            var hex = first.Length == 128 ? first
                : new string(text.Where(c => !char.IsWhiteSpace(c)).TakeWhile(Uri.IsHexDigit).ToArray());
            hex = hex.ToLowerInvariant();
            return hex.Length == 128 && hex.All(Uri.IsHexDigit) ? hex : "";
        }

        /// <summary>The chain from Microsoft's published hash to the folder builds run from:
        /// the zip must match every reference given (.sha512 file, pasted hash), and the sdk
        /// folder must be byte-identical to the zip — nothing missing, different or extra.</summary>
        async Task CheckSdkAsync()
        {
            var sdkRoot = DotnetLocator.PortableRoot;
            var versions = DotnetLocator.SdkVersions(DotnetLocator.PortableDotnet);

            SetBusy(true, "Checking SDK");
            ClearLog();
            ClearNotes();
            _tabs.SelectedIndex = 0;
            SetStatus("Checking SDK…", "Brush.Accent");

            var ok = true;
            try
            {
                if (versions.Count == 0)
                {
                    Note($"No SDK version found in {Path.Combine(sdkRoot, "sdk")}.", InkError);
                    SetStatus("SDK check failed.", "Brush.Error");
                    return;
                }
                var version = versions[0].Version;

                var toolchain = ToolchainFolder;
                var zips = Directory.Exists(toolchain)
                    ? Directory.GetFiles(toolchain, $"dotnet-sdk-{version}-win-*.zip")
                    : Array.Empty<string>();
                if (zips.Length == 0)
                {
                    Note($"No dotnet-sdk-{version}-win-*.zip in {toolchain}.", InkError);
                    Note("Put the SDK zip the sdk folder was unpacked from there, with its .sha512.",
                         InkMuted);
                    SetStatus("SDK check failed — zip not found.", "Brush.Error");
                    return;
                }
                var arch = "-win-" + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() + ".zip";
                var zip = zips.FirstOrDefault(z => z.EndsWith(arch, StringComparison.OrdinalIgnoreCase))
                          ?? zips[0];
                var zipName = Path.GetFileName(zip);
                Note($"SDK {version} · {zip}", InkMuted);

                // --- 1. the zip against its references ---
                var shaFile = Path.ChangeExtension(zip, ".sha512");
                if (!File.Exists(shaFile) && File.Exists(zip + ".sha512")) shaFile = zip + ".sha512";
                var stored = File.Exists(shaFile) ? NormalizeSha512(File.ReadAllText(shaFile)) : "";
                var pasted = NormalizeSha512(_sdkHash.Text);

                if (_sdkHash.Text.Trim().Length > 0 && pasted.Length == 0)
                {
                    Note("The pasted value is not a SHA-512 (128 hex digits); it was not used.", InkWarn);
                    ok = false;
                }

                SetStatus($"Checking SDK — hashing {zipName}…", "Brush.Accent");
                var actual = await Task.Run(() =>
                {
                    using var s = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return Convert.ToHexString(SHA512.HashData(s)).ToLowerInvariant();
                });
                Write($"SHA-512 {actual}  {zipName}\n", InkMuted);

                if (File.Exists(shaFile))
                {
                    if (stored.Length == 0)
                    { Note($"{Path.GetFileName(shaFile)} holds no SHA-512.", InkError); ok = false; }
                    else if (stored == actual)
                        Note($"Zip matches {Path.GetFileName(shaFile)}.", InkOk);
                    else
                    { Note($"Zip does NOT match {Path.GetFileName(shaFile)}.", InkError); ok = false; }
                }
                else Note($"No {Path.GetFileName(Path.ChangeExtension(zip, ".sha512"))} in {toolchain}.", InkWarn);

                if (pasted.Length > 0)
                {
                    if (pasted == actual) Note("Zip matches the pasted SHA-512.", InkOk);
                    else { Note("Zip does NOT match the pasted SHA-512.", InkError); ok = false; }
                }

                if (stored.Length == 0 && pasted.Length == 0)
                {
                    Note("Nothing to check the zip against: no .sha512 and no pasted hash.", InkWarn);
                    ok = false;
                }

                // --- 2. the sdk folder against the zip, byte for byte ---
                var missing = new List<string>();
                var differ = new List<string>();
                var extra = new List<string>();
                var count = 0;

                await Task.Run(() =>
                {
                    var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using var archive = ZipFile.OpenRead(zip);
                    var files = archive.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
                    foreach (var entry in files)
                    {
                        var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        expected.Add(rel);
                        var path = Path.Combine(sdkRoot, rel);
                        if (!File.Exists(path)) missing.Add(rel);
                        else if (new FileInfo(path).Length != entry.Length) differ.Add(rel);
                        else
                        {
                            using var a = entry.Open();
                            using var b = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                            if (!SameStream(a, b)) differ.Add(rel);
                        }
                        if (++count % 250 == 0)
                            SetStatus($"Checking SDK — {count:N0} of {files.Count:N0} files…", "Brush.Accent");
                    }

                    foreach (var file in Directory.EnumerateFiles(sdkRoot, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(sdkRoot, file);
                        if (!expected.Contains(rel)) extra.Add(rel);
                    }
                });

                if (missing.Count + differ.Count + extra.Count == 0)
                    Note($"sdk folder is byte-identical to the zip — {count:N0} files.", InkOk);
                else
                {
                    ok = false;
                    Note($"sdk folder differs from the zip: {missing.Count} missing, {differ.Count} " +
                         $"different, {extra.Count} extra. The Output tab lists every file.", InkError);
                    void List(string label, List<string> paths)
                    {
                        if (paths.Count == 0) return;
                        Write($"\n{label} ({paths.Count}):\n", InkError);
                        var all = new StringBuilder();
                        foreach (var p in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                            all.Append("  ").Append(p).Append('\n');
                        Write(all.ToString(), Ink);
                        foreach (var p in paths.Take(5)) Note($"{label}: {p}", InkMuted);
                        if (paths.Count > 5) Note($"{label}: … and {paths.Count - 5} more", InkMuted);
                    }
                    List("missing", missing);
                    List("different", differ);
                    List("extra", extra);
                }

                // A verified paste becomes the stored reference the isolated machine checks against.
                if (pasted.Length > 0 && pasted == actual && !File.Exists(shaFile))
                {
                    var target = Path.ChangeExtension(zip, ".sha512");
                    AddAction($"Save as {Path.GetFileName(target)}", () =>
                    {
                        try
                        {
                            File.WriteAllText(target, $"{actual} *{zipName}\r\n", new UTF8Encoding(false));
                            Note($"Saved {target}", InkOk);
                        }
                        catch (Exception ex) { Note($"Could not save: {ex.Message}", InkError); }
                        return Task.CompletedTask;
                    });
                }

                SetStatus(ok ? $"SDK {version} checked — all matches." : $"SDK {version} check failed — see Status.",
                          ok ? "Brush.Ok" : "Brush.Error");
            }
            catch (Exception ex)
            {
                Note($"The check could not finish: {ex.Message}", InkError);
                SetStatus("SDK check failed.", "Brush.Error");
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>Every package id in the cache with the versions on disk, on its own
        /// tab rather than in the build log, so looking never interleaves with or clears a
        /// running build's output.</summary>
        void ListPackages()
        {
            _packagesPanel.Children.Clear();
            _tabs.SelectedIndex = 2;

            var cache = PackageCache.Read();
            var rows = cache
                .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .Select(e => (Id: e.Key,
                              Versions: string.Join(", ",
                                  e.Value.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))))
                .ToList();

            // --- header: count, location, Copy list ---
            var head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var where = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var count = new TextBlock
            {
                Text = rows.Count == 1 ? "1 package stored" : $"{rows.Count} packages stored"
            };
            count.SetResourceReference(FrameworkElement.StyleProperty, "Text.Heading");
            where.Children.Add(count);

            var path = new TextBlock
            {
                Text = PackageCache.Exists ? DotnetLocator.PortablePackages
                                           : "No packages folder beside the exe.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };
            path.SetResourceReference(FrameworkElement.StyleProperty, "Text.Caption");
            where.Children.Add(path);
            Add(head, 0, where);

            if (rows.Count > 0)
            {
                var copy = new Button
                {
                    Content = Labelled("Icon.Copy", "Copy list"),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Put the list on the clipboard as plain text, ready to paste " +
                              "into whatever needs to know what this machine can build offline."
                };
                copy.SetResourceReference(StyleProperty, "Hue.Button.Tonal");
                copy.Click += (_, _) => CopyPackageList(rows);
                Add(head, 1, copy);
            }
            _packagesPanel.Children.Add(head);

            if (rows.Count == 0)
            {
                _packagesPanel.Children.Add(Line(
                    PackageCache.Exists
                        ? "The folder is there but holds no packages yet."
                        : "Restore will go to the machine's own cache, and to the network for "
                          + "anything missing from it.",
                    InkWarn));
                return;
            }

            // --- one row per package, shaped like the readiness rows ---
            foreach (var (id, versions) in rows)
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var name = new TextBlock
                {
                    Text = id,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0)
                };
                name.SetResourceReference(TextBlock.ForegroundProperty, Ink);
                Add(grid, 0, name);

                var stored = new TextBlock { Text = versions, VerticalAlignment = VerticalAlignment.Center };
                stored.SetResourceReference(FrameworkElement.StyleProperty, "Text.Mono");
                Add(grid, 1, stored);

                var row = new Border
                {
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 7, 0, 7),
                    Child = grid
                };
                row.SetResourceReference(Border.BorderBrushProperty, "Brush.LineSoft");
                _packagesPanel.Children.Add(row);
            }
        }

        /// <summary>The list as plain text: the folder, then one package per line with its
        /// versions, aligned. Plain rather than pretty because it is written to be pasted
        /// somewhere else and read there.</summary>
        void CopyPackageList(List<(string Id, string Versions)> rows)
        {
            var width = rows.Max(r => r.Id.Length);
            var text = new StringBuilder();
            text.Append(DotnetLocator.PortablePackages).Append('\n');
            text.Append(rows.Count).Append(rows.Count == 1 ? " package" : " packages").Append("\n\n");
            foreach (var (id, versions) in rows)
                text.Append(id.PadRight(width)).Append("  ").Append(versions).Append('\n');

            try
            {
                Clipboard.SetText(text.ToString());
                SetStatus($"{rows.Count} packages copied to the clipboard.", "Brush.Ok");
            }
            catch
            {
                SetStatus("The clipboard was busy — try again.", "Brush.Warn");
            }
        }

        /// <summary>A tile: a card that stands for one thing. Its heading takes the family
        /// colour, which is what marks a tile out from an ordinary card in the suite.</summary>
        static Border Card(string label, string value, string detail, string path)
        {
            var stack = new StackPanel();

            var heading = new TextBlock { Text = label };
            heading.SetResourceReference(FrameworkElement.StyleProperty, "Text.TileTitle");
            stack.Children.Add(heading);

            var figure = new TextBlock
            {
                Text = value,
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 0)
            };
            figure.SetResourceReference(TextBlock.ForegroundProperty, Ink);
            figure.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
            stack.Children.Add(figure);

            if (detail.Length > 0)
            {
                var line = new TextBlock
                {
                    Text = detail,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                line.SetResourceReference(FrameworkElement.StyleProperty, "Text.Body");
                stack.Children.Add(line);
            }

            if (path.Length > 0)
            {
                var where = new TextBlock
                {
                    Text = path,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                where.SetResourceReference(FrameworkElement.StyleProperty, "Text.Caption");
                stack.Children.Add(where);
            }

            var card = new Border { Child = stack, Padding = new Thickness(16, 14, 16, 15) };
            card.SetResourceReference(FrameworkElement.StyleProperty, "Hue.Card");
            return card;
        }

        static UIElement RowGroup(List<ReadinessItem> items)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
            foreach (var item in items) stack.Children.Add(ReadinessRow(item));
            return stack;
        }

        static UIElement ReadinessRow(ReadinessItem item)
        {
            // Status, so pills. No glow on any of them: these repeat down the list, and a
            // blur per row is what makes a long list crawl over Remote Desktop.
            var (label, pill) = item.State switch
            {
                Readiness.Present => ("ready", "Hue.Pill.Ok"),
                Readiness.Maybe => ("likely", "Hue.Pill.Warn"),
                Readiness.Missing => ("missing", "Hue.Pill.Error"),
                _ => ("unknown", "Hue.Pill.Stale")
            };

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 1) };

            // Auto rather than a fixed width: a pill is its word plus about 33px of dot,
            // padding and border, so a fixed column clips the longest word the moment the
            // wording or the font changes. Shared across the tab, so the rows still align.
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto,
                SharedSizeGroup = "ReadinessPill"
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = new ContentControl
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };
            badge.SetResourceReference(FrameworkElement.StyleProperty, pill);
            Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            var text = new TextBlock
            {
                Text = item.Detail.Length > 0 ? $"{item.Name} · {item.Detail}" : item.Name,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            text.SetResourceReference(TextBlock.ForegroundProperty,
                                      item.State == Readiness.Unknown ? InkMuted : Ink);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            if (item.Version.Length > 0)
            {
                var version = new TextBlock
                {
                    Text = item.Version,
                    VerticalAlignment = VerticalAlignment.Center
                };
                version.SetResourceReference(FrameworkElement.StyleProperty, "Text.Mono");
                Grid.SetColumn(version, 2);
                grid.Children.Add(version);
            }

            var row = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 7, 0, 7),
                Child = grid
            };
            row.SetResourceReference(Border.BorderBrushProperty, "Brush.LineSoft");
            return row;
        }

        /// <summary>Folder sizes are slow on an 800 MB SDK, so they are measured off
        /// the UI thread and the cards fill in when the numbers arrive.</summary>
        void RefreshSizes()
        {
            _sdkSizeMb = null;
            _packagesSizeMb = null;

            var dotnet = DotnetLocator.Find();
            var sdkRoot = dotnet is null ? "" : DotnetLocator.RootOf(dotnet);
            var pkgRoot = PackageCache.Exists ? DotnetLocator.PortablePackages : "";

            _ = Task.Run(() =>
            {
                if (sdkRoot.Length > 0)
                {
                    var mb = FolderSizeMb(sdkRoot);
                    Dispatcher.BeginInvoke(new Action(() => { _sdkSizeMb = mb; RenderStatus(); }));
                }
                if (pkgRoot.Length > 0)
                {
                    var mb = FolderSizeMb(pkgRoot);
                    Dispatcher.BeginInvoke(new Action(() => { _packagesSizeMb = mb; RenderStatus(); }));
                }
            });
        }

        /// <summary>A group label inside the Status tab. Text.Section is written in
        /// capitals in the suite, since WPF has no text-transform.</summary>
        static TextBlock Heading(string text)
        {
            var block = new TextBlock
            {
                Text = text.ToUpperInvariant(),
                Margin = new Thickness(0, 18, 0, 7)
            };
            block.SetResourceReference(FrameworkElement.StyleProperty, "Text.Section");
            return block;
        }

        static TextBlock Line(string text, string ink)
        {
            var block = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 3)
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, ink);
            return block;
        }

        /// <summary>Adds a plain-language line to the Status tab. Marshalled like the
        /// other status writers: the list it appends to is the one RenderStatusCore walks,
        /// so appending from a worker thread while it renders would throw.</summary>
        void Note(string text, string ink)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Note(text, ink)));
                return;
            }
            _narrative.Add((text, ink));
            RenderStatus();
        }

        void ClearNotes()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ClearNotes));
                return;
            }
            _narrative.Clear();
            _actions.Clear();
            RenderStatus();
        }

        void AddAction(string label, Func<Task> run)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => AddAction(label, run)));
                return;
            }
            _actions.Add((label, run));
            RenderStatus();
        }

        /// <summary>The suite's status strip: a marker dot in the colour of the current
        /// state, the status line, and the two log actions as quiet ghost buttons. The
        /// progress bar sits in its own thin row above it, so the strip keeps one height.</summary>
        UIElement StatusRow()
        {
            var copy = new Button { Content = Labelled("Icon.Copy", "Copy log") };
            var clear = new Button { Content = Labelled("Icon.Close", "Clear") };
            copy.Click += (_, _) => CopyLog();
            clear.Click += (_, _) => ClearLog();
            copy.SetResourceReference(StyleProperty, "Hue.Button.Ghost");
            clear.SetResourceReference(StyleProperty, "Hue.Button.Ghost");
            clear.Margin = new Thickness(4, 0, 0, 0);

            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            _status.VerticalAlignment = VerticalAlignment.Center;

            // Size and alignment come from Hue.Marker; only the gap to the words is ours.
            _statusDot.Margin = new Thickness(0, 0, 9, 0);
            _statusDot.SetResourceReference(FrameworkElement.StyleProperty, "Hue.Marker");
            _statusDot.SetResourceReference(Control.ForegroundProperty, "Brush.Stale");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Add(grid, 0, _statusDot);
            Add(grid, 1, _status);
            Add(grid, 2, copy);
            Add(grid, 3, clear);

            var bar = new Border { Child = grid };
            bar.SetResourceReference(FrameworkElement.StyleProperty, "Hue.StatusBar");
            return bar;
        }

        static TextBlock Caption(string text)
        {
            var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            block.SetResourceReference(TextBlock.ForegroundProperty, InkDim);
            return block;
        }

        /// <summary>Puts one of the kit's 16x16 icons in front of a button's label. The
        /// word carries the meaning on its own — the icon only speeds recognition — so no
        /// button here is icon-only and none needs an AutomationProperties.Name beyond its
        /// content.</summary>
        static object Labelled(string icon, string text)
        {
            // Fully qualified: System.IO.Path is in scope everywhere else in this file.
            var path = new System.Windows.Shapes.Path();
            path.SetResourceReference(FrameworkElement.StyleProperty, "Hue.Icon");
            path.SetResourceReference(System.Windows.Shapes.Path.DataProperty, icon);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(path);
            row.Children.Add(new TextBlock
            {
                Text = text,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            return row;
        }

        static UIElement Spacer(double width) => new Border { Width = width };

        static void Add(Grid grid, int column, UIElement child)
        {
            Grid.SetColumn(child, column);
            grid.Children.Add(child);
        }

        // ------------------------------------------------------------- wiring

        void WireUp()
        {
            Drop += OnDrop;
            // A drop swaps the source, so it is locked down with the browse buttons. The
            // cursor says no rather than the drop being silently ignored.
            DragOver += (_, e) =>
            {
                e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop)
                    ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            _project.SelectionChanged += (_, _) => RenderStatus();
            _rid.SelectionChanged += (_, _) => RenderStatus();
            _trim.Checked += (_, _) => RenderStatus();
            _trim.Unchecked += (_, _) => RenderStatus();
            _r2r.Checked += (_, _) => RenderStatus();
            _r2r.Unchecked += (_, _) => RenderStatus();
            _build.Click += async (_, _) => await BuildAsync();
            _rebuildAll.Click += async (_, _) => await RebuildAllAsync();
            _cancel.Click += (_, _) => { _batchCancel = true; CancelBuild(); };
            _openOutput.Click += (_, _) => OpenOutputFolder();
            Closed += (_, _) => Cleanup();
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            if (_busy) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
            LoadSource(paths[0]);
        }

        void PickFolder()
        {
            var dlg = new OpenFolderDialog { Title = "Select the repo folder" };
            if (dlg.ShowDialog(this) == true) LoadSource(dlg.FolderName);
        }

        /// <summary>Downloads has no SpecialFolder entry, so read the known-folder
        /// registry value and fall back to the conventional path.</summary>
        static string DownloadsFolder()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
                if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string path
                    && path.Length > 0)
                {
                    path = Environment.ExpandEnvironmentVariables(path);
                    if (Directory.Exists(path)) return path;
                }
            }
            catch { /* fall through to the conventional path */ }

            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Directory.Exists(fallback) ? fallback : "";
        }

        void PickZip()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select a repo zip",
                Filter = "Zip archives (*.zip)|*.zip"
            };
            var downloads = DownloadsFolder();
            if (downloads.Length > 0) dlg.InitialDirectory = downloads;
            if (dlg.ShowDialog(this) == true) LoadSource(dlg.FileName);
        }

        void PickOutput()
        {
            var dlg = new OpenFolderDialog { Title = "Where should the exe go?" };
            if (dlg.ShowDialog(this) == true) _output.Text = dlg.FolderName;
        }

        // -------------------------------------------------------- source load

        void LoadSource(string path)
        {
            ClearNotes();
            try
            {
                if (File.Exists(path) && Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    DiscardExtraction();
                    _wantedProjectRel = "";
                    _archivedTo = "";
                    _sourceZip = path;
                    Note($"Unpacking {Path.GetFileName(path)}…", InkMuted);
                    ExtractSourceZip();
                    _source.Text = path;
                }
                else if (Directory.Exists(path))
                {
                    DiscardExtraction();
                    _wantedProjectRel = "";
                    _archivedTo = "";
                    _sourceZip = "";
                    _repoRoot = path;
                    _source.Text = path;
                }
                else
                {
                    SetStatus("That path is neither a folder nor a .zip.", "Brush.Warn");
                    return;
                }
            }
            catch (Exception ex)
            {
                SetStatus("Could not open the source.", "Brush.Error");
                Note(ex.Message, InkError);
                return;
            }

            ScanProjects();
            WarnIfAlreadyBuilt();
        }

        /// <summary>The newest zip of any project in Save to\HISTORY that is byte-identical
        /// to the loaded zip, or null. Only the newest per project: matching an older one is
        /// not a repeat of the last build. A warning only — a rebuild of the same source can
        /// be wanted for other settings, another SDK or other packages, so nothing is ever
        /// refused on it. Rebuild all never asks.</summary>
        string? AlreadyBuiltAs()
        {
            if (_sourceZip.Length == 0 || !File.Exists(_sourceZip)) return null;
            var history = Path.Combine(_output.Text.Trim(), "HISTORY");
            try
            {
                if (_output.Text.Trim().Length == 0 || !Directory.Exists(history)) return null;
                foreach (var dir in Directory.EnumerateDirectories(history))
                {
                    var newest = NewestArchive(dir, Path.GetFileName(dir));
                    // Loading a HISTORY zip directly is a deliberate rebuild, not a repeat download.
                    if (newest is not null && !SamePath(newest, _sourceZip) && SameBytes(newest, _sourceZip))
                        return newest;
                }
            }
            catch { /* a courtesy check; never stand in the way of a build */ }
            return null;
        }

        void WarnIfAlreadyBuilt()
        {
            if (AlreadyBuiltAs() is not { } match) return;
            Note($"Already built: this zip is identical to {match}, the latest in HISTORY.", InkWarn);
            Note("Build exe still builds it — for other settings, another SDK or other packages. " +
                 "It will not be filed a second time.", InkMuted);
            SetStatus($"Already built — identical to {Path.GetFileName(match)}.", "Brush.Warn");
        }

        /// <summary>Length first, so a different file almost never costs a read.</summary>
        static bool SameBytes(string a, string b)
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;

            using var sa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return SameStream(sa, sb);
        }

        /// <summary>Reads both to the end; true only if every byte matches.</summary>
        static bool SameStream(Stream sa, Stream sb)
        {
            var ba = new byte[81920];
            var bb = new byte[81920];
            while (true)
            {
                var na = sa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
                var nb = sb.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
                if (na != nb) return false;
                if (na == 0) return true;
                if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
            }
        }

        /// <summary>Unpacks _sourceZip into a fresh folder under TempRoot and points
        /// _repoRoot at it. Throws on a missing or unreadable zip; callers report it.</summary>
        void ExtractSourceZip()
        {
            var dest = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dest);
            _extractRoot = dest;
            _tempDirs.Add(dest);
            ZipFile.ExtractToDirectory(_sourceZip, dest);
            _repoRoot = CollapseSingleRoot(dest);
        }

        /// <summary>Moves the source zip into Save to\HISTORY\&lt;AssemblyName&gt;\ under
        /// the next free number (see NextArchivePath). Only the zip moves; the extraction
        /// is deleted separately.</summary>
        void ArchiveSourceZip(ProjectInfo project, string outputDir)
        {
            if (_sourceZip.Length == 0 || !File.Exists(_sourceZip)) return;

            // AssemblyName, so the archive is named after the exe rather than after the
            // .csproj, which is not always the same thing. The scanner already falls
            // back to the project file name when the element is absent.
            var name = SafeName(project.AssemblyName);
            if (name.Length == 0) name = SafeName(Path.GetFileNameWithoutExtension(project.Path));
            if (name.Length == 0) name = "project";

            var historyDir = Path.Combine(outputDir, "HISTORY", name);

            try
            {
                // Rebuilding something already filed must not file it a second time.
                var from = Path.GetDirectoryName(Path.GetFullPath(_sourceZip)) ?? "";
                if (string.Equals(from, Path.GetFullPath(historyDir),
                                  StringComparison.OrdinalIgnoreCase))
                    return;

                // The same bytes as the latest filed zip: filing again would only duplicate
                // it, so the download is dropped and the existing entry stands for it.
                if (NewestArchive(historyDir, name) is { } latest && SameBytes(latest, _sourceZip))
                {
                    File.Delete(_sourceZip);
                    _archivedTo = latest;
                    Note($"Identical to {latest}, so not filed again; the download was removed.",
                         InkMuted);
                    return;
                }

                Directory.CreateDirectory(historyDir);
                var target = NextArchivePath(historyDir, name);

                // Across volumes this is a copy followed by a delete, done by the OS.
                // It either completes or throws; there is no half-moved state to undo.
                File.Move(_sourceZip, target);

                _archivedTo = target;
                Note($"Source filed as {target}", InkMuted);
            }
            catch (Exception ex)
            {
                Note($"The exe is fine, but the source zip could not be filed: {ex.Message}",
                     InkWarn);
                Note($"It is still at {_sourceZip}", InkMuted);
            }
        }

        /// <summary>AssemblyName is free text and can hold characters a path cannot.</summary>
        static string SafeName(string raw)
        {
            var cleaned = new string(raw.Trim()
                .Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
            return cleaned.TrimEnd('.', ' ');
        }

        /// <summary>&lt;name&gt;-0001.zip upwards. The number comes from the folder, not the
        /// incoming file name, so two builds can never collide whatever the download was
        /// called. Any trailing run of digits counts, so a folder seeded with an older
        /// scheme carries on: RepoBuilder-src-006.zip continues at 0007.</summary>
        static string NextArchivePath(string historyDir, string name)
        {
            var highest = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(historyDir, name + "-*.zip"))
                {
                    var stem = Path.GetFileNameWithoutExtension(file);
                    var cut = stem.LastIndexOf('-');
                    if (cut < 0 || cut == stem.Length - 1) continue;
                    if (int.TryParse(stem[(cut + 1)..], out var n) && n > highest) highest = n;
                }
            }
            catch { /* an unreadable folder just starts at 1 */ }

            string candidate;
            do { candidate = Path.Combine(historyDir, $"{name}-{++highest:D4}.zip"); }
            while (File.Exists(candidate));

            return candidate;
        }

        /// <summary>Throws away whatever a zip was last unpacked into. The zip path
        /// itself is left alone, so a later build can unpack it again.</summary>
        void DiscardExtraction()
        {
            if (_extractRoot.Length == 0) return;

            if (_project.SelectedItem is ProjectInfo selected && _repoRoot.Length > 0)
            {
                try { _wantedProjectRel = Path.GetRelativePath(_repoRoot, selected.Path); }
                catch { _wantedProjectRel = ""; }
            }

            TryDelete(_extractRoot);
            _tempDirs.Remove(_extractRoot);
            _extractRoot = "";
            _repoRoot = "";
        }

        /// <summary>Zips from GitHub contain one wrapper folder; step into it.</summary>
        static string CollapseSingleRoot(string dir)
        {
            var entries = Directory.GetFileSystemEntries(dir);
            if (entries.Length == 1 && Directory.Exists(entries[0])) return entries[0];
            return dir;
        }

        void ScanProjects()
        {
            _project.Items.Clear();
            var found = ProjectScanner.Scan(_repoRoot);
            var exes = found.Where(p => p.IsExecutable).ToList();

            if (found.Count == 0)
            {
                _project.IsEnabled = false;
                Note("No .NET project was found in there. A repo needs a .csproj file " +
                     "somewhere inside it.", InkError);
                SetStatus("No project found.", "Brush.Warn");
                return;
            }

            foreach (var p in found) _project.Items.Add(p);
            _project.IsEnabled = true;

            if (exes.Count == 1)
            {
                _project.SelectedItem = exes[0];
                Note($"Found {Path.GetFileNameWithoutExtension(exes[0].Path)}. Ready to build.",
                     InkOk);
                SetStatus("Ready to build.", "Brush.Ok");
            }
            else if (exes.Count == 0)
            {
                _project.SelectedIndex = 0;
                Note($"{found.Count} project(s) found, but none of them produces a program. " +
                     "Pick one from the Project list to try anyway.", InkWarn);
                SetStatus("No runnable project found.", "Brush.Warn");
            }
            else
            {
                _project.SelectedItem = exes[0];
                Note($"{exes.Count} runnable projects here. The first is selected — check the " +
                     "Project list if that is not the one you want.", InkWarn);
                SetStatus("More than one project — confirm the choice.", "Brush.Warn");
            }

            RenderStatus();
        }

        // -------------------------------------------------------------- build

        /// <summary>The full dotnet publish command line for one project, built from the
        /// current settings. Single builds and Rebuild all both call this, so a batch
        /// produces exactly what a one-off build of the same zip would.</summary>
        string PublishArgs(ProjectInfo project, string workDir)
        {
            var args = new StringBuilder();
            args.Append("publish \"").Append(project.Path).Append('"');
            args.Append(" -c Release");
            args.Append(" -r ").Append(_rid.SelectedItem);
            args.Append(" --self-contained true");
            args.Append(" -p:PublishSingleFile=true");
            args.Append(" -p:IncludeNativeLibrariesForSelfExtract=true");
            args.Append(" -p:EnableCompressionInSingleFile=true");
            // The vulnerability audit contacts nuget.org even when every package
            // resolves from the local cache. Package resolution itself is already
            // local-first, so a genuinely missing package still errors normally.
            args.Append(" -p:NuGetAudit=false");
            // Without this, obj\ and bin\ are written beside the .csproj — into the
            // user's own tree for a folder build — and outlive the build. Inside workDir
            // they are deleted with it.
            //
            // ArtifactsPath rather than BaseIntermediateOutputPath/BaseOutputPath: a
            // command-line property reaches every project in the graph, and one shared
            // obj\ makes referenced projects overwrite each other's project.assets.json.
            // ArtifactsPath gives each project its own subtree, and takes no trailing
            // separator, so quoting it has no \" escaping trap.
            if (_useArtifactsPath)
                args.Append(" -p:ArtifactsPath=\"")
                    .Append(Path.Combine(workDir, "artifacts")).Append('"');
            // Trimming removes IL it cannot prove is reachable, and WPF resolves types by
            // name at runtime out of XAML, so the trimmer strips things that are in use.
            // Microsoft does not support it for WPF or WinForms; skipping it per project
            // also keeps one tick from breaking every desktop project in a batch.
            if (_trim.IsChecked == true && !project.UsesDesktop)
                args.Append(" -p:PublishTrimmed=true");
            if (_r2r.IsChecked == true) args.Append(" -p:PublishReadyToRun=true");
            // Every build runs in a random folder, and without this that path is baked
            // into the output (PDB references, [CallerFilePath]), so two builds of the
            // same zip would differ by the folder name alone. ContinuousIntegrationBuild
            // turns on DeterministicSourcePaths, which maps the source root to /_/.
            // Projects that embed no paths are unaffected, so it costs nothing to leave
            // on, and it does not need a .git folder.
            if (_repro.IsChecked == true) args.Append(" -p:ContinuousIntegrationBuild=true");
            args.Append(" -v ").Append(_verbosity.SelectedItem);
            args.Append(" -o \"").Append(workDir).Append('"');
            var extra = _extraArgs.Text.Trim();
            if (extra.Length > 0) args.Append(' ').Append(extra);
            return args.ToString();
        }

        async Task BuildAsync()
        {
            // The extraction is thrown away after a successful build, so the source
            // row can still point at a zip whose contents are gone. Unpack it again —
            // which also picks up a replacement zip dropped at the same path.
            if (_sourceZip.Length > 0 && !Directory.Exists(_repoRoot))
            {
                ClearNotes();
                if (!File.Exists(_sourceZip))
                {
                    Note($"{Path.GetFileName(_sourceZip)} is no longer in " +
                         $"{Path.GetDirectoryName(_sourceZip)}.", InkError);
                    if (_archivedTo.Length > 0)
                        Note($"It was filed as {_archivedTo} after the last build. Drop the " +
                             "next zip when you have it.", InkMuted);
                    else
                        Note("Drop the zip again.", InkMuted);
                    SetStatus("The source zip has gone.", "Brush.Error");
                    return;
                }

                Note($"Unpacking {Path.GetFileName(_sourceZip)}…", InkMuted);
                try { ExtractSourceZip(); }
                catch (Exception ex)
                {
                    Note($"Could not unpack the zip: {ex.Message}", InkError);
                    SetStatus("Could not unpack the source.", "Brush.Error");
                    return;
                }

                ScanProjects();

                // Keep the choice made last time, if that project is still in there.
                if (_wantedProjectRel.Length > 0)
                {
                    var match = _project.Items.OfType<ProjectInfo>().FirstOrDefault(
                        p => string.Equals(Path.GetRelativePath(_repoRoot, p.Path),
                                           _wantedProjectRel, StringComparison.OrdinalIgnoreCase));
                    if (match is not null) _project.SelectedItem = match;
                }
            }

            if (_project.SelectedItem is not ProjectInfo project)
            {
                ClearNotes();
                Note("Pick a repo first — drop a folder or a .zip anywhere on this window.",
                     InkWarn);
                SetStatus("No repo loaded.", "Brush.Warn");
                return;
            }

            var dotnet = DotnetLocator.Find();
            if (dotnet is null)
            {
                SetStatus("No .NET SDK found.", "Brush.Error");
                ClearNotes();
                Note("No .NET SDK on this machine, so there is nothing to build with.", InkError);
                Note("Download the SDK as a .zip rather than the installer — it needs no admin " +
                     "rights and can sit next to this exe.", InkMuted);
                AddAction("Open the download page", () => OpenUrl(SdkDownloadUrl));
                AddAction("Search, if that page moved",
                          () => OpenUrl("https://duckduckgo.com/?q=dotnet+sdk+download+zip"));
                AddAction("I already have the SDK zip", InstallSdkFromZip);
                return;
            }

            var outputDir = _output.Text.Trim();
            if (outputDir.Length == 0)
            {
                ClearNotes();
                Note("Set a folder for the finished exe in \u201cSave to\u201d.", InkWarn);
                SetStatus("No output folder set.", "Brush.Warn");
                return;
            }

            // A running exe cannot be replaced, so building it would be wasted work.
            if (IsRunning(project.AssemblyName, outputDir))
            {
                var exeName = project.AssemblyName + ".exe";
                if (_sourceZip.Length > 0) DiscardExtraction();
                ClearNotes();
                Note($"{exeName} is running, so it cannot be replaced. Nothing was built.", InkWarn);
                Note($"Close {exeName} and press Build exe again.", InkMuted);
                SetStatus($"{exeName} is running — close it and try again.", "Brush.Warn");
                return;
            }

            var workDir = Path.Combine(TempRoot, "publish_" + Guid.NewGuid().ToString("N"));
            _tempDirs.Add(workDir);

            var args = PublishArgs(project, workDir);

            SetBusy(true, "Building");
            ClearLog();
            ClearNotes();
            _sawRestoreSourceError = false;
            Write($"{dotnet}\n{args}\n", InkMuted);
            if (DotnetLocator.HasPortablePackages)
                Write($"packages: {DotnetLocator.PortablePackages}\n", InkMuted);
            Write("\n", InkMuted);
            Note($"Building {Path.GetFileNameWithoutExtension(project.Path)} for " +
                 $"{_rid.SelectedItem}. This usually takes under a minute.", Ink);
            if (_useArtifactsPath && PinsItsOwnOutputPath(project, out var pinnedBy))
                Note($"{pinnedBy} sets an output path of its own, which wins over the " +
                     "redirect. This repo's obj and bin will land in the repo folder as " +
                     "before — the build is unaffected.", InkMuted);
            if (AlreadyBuiltAs() is { } builtAs)
                Note($"Building again: this zip is identical to {Path.GetFileName(builtAs)}, " +
                     "already built. It will not be filed a second time.", InkWarn);
            SetStatus("Building…", "Brush.Accent");

            var sw = Stopwatch.StartNew();
            int exit;
            try
            {
                exit = await RunAsync(dotnet, args, Path.GetDirectoryName(project.Path)!);
            }
            catch (Exception ex)
            {
                Note($"The build could not be started: {ex.Message}", InkError);
                SetStatus("The build could not be started.", "Brush.Error");
                SetBusy(false);
                return;
            }
            sw.Stop();

            if (exit != 0)
            {
                Write($"\ndotnet publish exited with code {exit}.\n", InkError);
                Note($"The build failed after {sw.Elapsed.TotalSeconds:F0} seconds.", InkError);
                Note("The Output tab has the exact error. Copy log puts all of it on the " +
                     "clipboard.", InkMuted);
                SetStatus("Build failed.", "Brush.Error");

                if (_sawRestoreSourceError)
                {
                    Note("Something this repo needs is not stored locally, and the package " +
                         "source could not be reached.", InkWarn);
                    AddAction("This machine has network — fetch what is missing", PrimePackages);
                    AddAction("It does not — use a folder primed elsewhere", InstallPackagesFromZip);
                }
                else if (_useArtifactsPath)
                {
                    // A repo with a custom target that copies into a hardcoded
                    // bin\Release path, or an SDK that dislikes ArtifactsPath next to
                    // -o, fails here. One click puts the build back the old way.
                    Note("Build files were redirected out of the repo folder. A repo that " +
                         "hardcodes its own build paths can trip over that.", InkMuted);
                    AddAction("Retry with build files in the repo folder", async () =>
                    {
                        _useArtifactsPath = false;
                        try { await BuildAsync(); }
                        finally { _useArtifactsPath = true; }
                    });
                }

                TryDelete(workDir);
                SetBusy(false);
                return;
            }

            try
            {
                var exe = FindExe(workDir, project.AssemblyName);
                if (exe is null)
                {
                    Note("The build reported success but produced no .exe.", InkError);
                    SetStatus("No exe found in the publish output.", "Brush.Error");
                    SetBusy(false);
                    return;
                }

                Directory.CreateDirectory(outputDir);
                var final = Path.Combine(outputDir, Path.GetFileName(exe));

                // Building RepoBuilder with itself: Windows will not overwrite the running
                // exe, so it steps aside as -OLD, the same swap Rebuild all uses. Identical
                // output needs no swap at all.
                var selfBuild = SamePath(final, _runningAs);
                var selfIdentical = selfBuild && File.Exists(final) && Sha256Of(final) == Sha256Of(exe);
                if (selfBuild)
                {
                    if (!selfIdentical) ReplaceRunningExe(exe, final);
                }
                else
                {
                    File.Copy(exe, final, overwrite: true);
                }

                var size = new FileInfo(final).Length / 1024d / 1024d;
                Write($"\n{final}\n", InkAccent);
                Note($"Done. {Path.GetFileName(final)} is ready — {size:F1} MB, built in " +
                     $"{sw.Elapsed.TotalSeconds:F0} seconds.", InkOk);
                Note(final, InkMuted);
                Note("It needs nothing installed on the machine you run it on.", InkMuted);
                if (selfIdentical)
                    Note("Byte-identical to the RepoBuilder that is running, so nothing was replaced.",
                         InkMuted);
                else if (selfBuild)
                    Note($"This window is now running {Path.GetFileName(_runningAs)}, the fallback; " +
                         $"start {Path.GetFileName(final)} to use the new build.", InkWarn);
                AddAction("Open the folder", () => { OpenOutputFolder(); return Task.CompletedTask; });
                SetStatus($"Done — {Path.GetFileName(final)} ({size:F1} MB)", "Brush.Ok");
                _openOutput.IsEnabled = true;

                // The zip goes to HISTORY and the unpacked copy is thrown away, so a
                // finished build leaves nothing in Downloads and nothing in temp.
                if (_sourceZip.Length > 0)
                {
                    ArchiveSourceZip(project, outputDir);
                    DiscardExtraction();
                }
            }
            catch (Exception ex)
            {
                Note($"Built, but the exe could not be copied: {ex.Message}", InkError);
                SetStatus("Built, but the exe could not be copied.", "Brush.Error");
            }

            TryDelete(workDir);
            SetBusy(false);
        }

        // ---------------------------------------------------------------- rebuild all

        /// <summary>One project in a Rebuild all run: its HISTORY folder, the zip that
        /// was built, and what happened.</summary>
        sealed class BatchItem
        {
            public string Name = "";      // HISTORY folder name, which is the AssemblyName
            public string Zip = "";       // newest zip in that folder
            public string Outcome = "";   // new | updated | unchanged | failed | skipped
            public string Detail = "";
            public bool Built;            // Save to now holds an exe built from Zip
        }

        /// <summary>The exe this process was started from. Tracked rather than read from
        /// Environment.ProcessPath, which is cached and would go stale after the batch
        /// renames the running exe to its -OLD name.</summary>
        string _runningAs = Environment.ProcessPath ?? "";

        bool _selfReplaced;
        bool _busy;

        /// <summary>What is running and for how long, as a pill centred in the header. Keeps
        /// its last value afterwards, so a finished run's time stays readable.
        ///
        /// Amber, a stated exception like Zip… and Rebuild all, so a live run does not read
        /// as more cyan chrome. It uses the kit's warn pill for that amber's tone, not its
        /// meaning, so nothing has to be added to the palette.</summary>
        readonly ContentControl _elapsed = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(16, 0, 16, 0),
            Visibility = Visibility.Collapsed
        };

        /// <summary>What the pill calls the work in progress — "Building", "Batch rebuild"
        /// and so on. Set by SetBusy, read by ShowElapsed.</summary>
        string _busyLabel = "";

        /// <summary>Controls built inside a layout method rather than held as a field, which
        /// SetBusy still has to reach: the two source browsers and the output browser.
        /// Filled once, while the layout is built.</summary>
        readonly List<Control> _lockedWhileBusy = new();

        /// <summary>The Status tab's "What to do" buttons. Unlike the list above these are
        /// thrown away and rebuilt every time the panel renders, so the list is refilled
        /// there and the buttons are born disabled if a run is already under way.</summary>
        readonly List<Control> _statusActions = new();

        readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
        readonly Stopwatch _runFor = new();

        /// <summary>"Id/Version" of everything the batch's builds resolved, mapped to the
        /// projects that resolved it. Read from each project.assets.json before its folder
        /// is deleted; restore writes the full graph there, transitive packages included,
        /// so this is measured rather than guessed from the csproj.
        ///
        /// The project names are kept because they make the report actionable: a package
        /// resolved by every project is the toolchain, one resolved by a single project is
        /// a choice in that csproj that can be reconsidered.</summary>
        readonly Dictionary<string, SortedSet<string>> _usedPackages =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Rebuilds the newest zip of every project under Save to\HISTORY with the
        /// current settings. Built to temp as usual, then compared with the exe already
        /// in Save to: identical output is left untouched, different output replaces it.
        /// A failure never stops the batch. RepoBuilder itself goes last. At the end a
        /// dated .sha256 records every exe in the folder and the zip each rebuilt exe
        /// came from.</summary>
        async Task RebuildAllAsync()
        {
            var dotnet = DotnetLocator.Find();
            if (dotnet is null)
            {
                ClearNotes();
                Note("No .NET SDK on this machine, so there is nothing to build with.", InkError);
                SetStatus("No .NET SDK found.", "Brush.Error");
                return;
            }

            var outputDir = _output.Text.Trim();
            var history = outputDir.Length > 0 ? Path.Combine(outputDir, "HISTORY") : "";
            if (history.Length == 0 || !Directory.Exists(history))
            {
                ClearNotes();
                Note($"There is no HISTORY folder in {(outputDir.Length > 0 ? outputDir : "Save to")} " +
                     "yet. It fills up as zips are built, one folder per project.", InkWarn);
                SetStatus("Nothing to rebuild.", "Brush.Warn");
                return;
            }

            var self = typeof(MainWindow).Assembly.GetName().Name ?? "RepoBuilder";
            var items = new List<BatchItem>();
            foreach (var dir in Directory.EnumerateDirectories(history)
                         .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                var zip = NewestArchive(dir, name);
                items.Add(zip is null
                    ? new BatchItem { Name = name, Outcome = "skipped", Detail = "no zip in its HISTORY folder" }
                    : new BatchItem { Name = name, Zip = zip });
            }

            // RepoBuilder last: if it replaces itself, everything else is already done.
            // OrderBy is stable, so the rest stay alphabetical.
            items = items.OrderBy(i => string.Equals(i.Name, self, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                         .ToList();

            var todo = items.Where(i => i.Zip.Length > 0).ToList();
            if (todo.Count == 0)
            {
                ClearNotes();
                Note($"{history} has no zips to rebuild yet.", InkWarn);
                SetStatus("Nothing to rebuild.", "Brush.Warn");
                return;
            }

            SetBusy(true, "Batch rebuild");
            ClearLog();
            ClearNotes();
            _batchCancel = false;
            _usedPackages.Clear();

            var settings = new List<string> { $"{_rid.SelectedItem}" };
            if (_trim.IsChecked == true) settings.Add("Trim where supported");
            if (_r2r.IsChecked == true) settings.Add("ReadyToRun");
            if (_repro.IsChecked == true) settings.Add("Reproducible");
            if (_extraArgs.Text.Trim().Length > 0) settings.Add($"Args: {_extraArgs.Text.Trim()}");

            Write($"{dotnet}\n", InkMuted);
            if (DotnetLocator.HasPortablePackages)
                Write($"packages: {DotnetLocator.PortablePackages}\n", InkMuted);
            Write($"Rebuilding {todo.Count} project(s) from {history}\n", InkMuted);
            Write($"settings: {string.Join(" · ", settings)}\n\n", InkMuted);
            Note($"Rebuilding {todo.Count} projects from HISTORY with the current settings " +
                 $"({string.Join(" · ", settings)}). The Output tab shows each build as it runs.", Ink);

            var sw = Stopwatch.StartNew();
            var number = 0;
            foreach (var item in todo)
            {
                if (_batchCancel)
                {
                    item.Outcome = "skipped";
                    item.Detail = "cancelled before it started";
                    continue;
                }

                number++;
                SetStatus($"Rebuilding {number} of {todo.Count}: {item.Name}…", "Brush.Accent");
                Write($"── {item.Name} · {Path.GetFileName(item.Zip)}\n", InkAccent);

                await RebuildOneAsync(dotnet, item, outputDir);

                Write($"   {item.Outcome}{(item.Detail.Length > 0 ? " — " + item.Detail : "")}\n\n",
                      OutcomeInk(item));

                // The status bar only ever shows the project in progress, so each
                // finished one leaves its line here and the list grows as the batch runs.
                NoteOutcome(item);
            }
            sw.Stop();

            string? record = null;
            try { record = WriteChecksums(outputDir, items); }
            catch (Exception ex) { Write($"The checksum file could not be written: {ex.Message}\n", InkError); }

            ReportBatch(items, outputDir, history, record, sw.Elapsed);
            ReportPackageUse(items);
            SetBusy(false);
            _openOutput.IsEnabled = true;
        }

        async Task RebuildOneAsync(string dotnet, BatchItem item, string outputDir)
        {
            // Failed rather than skipped: the package report must not see this project's
            // packages as unused just because its exe happened to be running.
            if (IsRunning(item.Name, outputDir))
            {
                item.Outcome = "failed";
                item.Detail = $"{item.Name}.exe is running — close it and run again; not built";
                return;
            }

            var unpacked = Path.Combine(TempRoot, "batch_" + Guid.NewGuid().ToString("N"));
            var workDir = Path.Combine(TempRoot, "publish_" + Guid.NewGuid().ToString("N"));
            _tempDirs.Add(unpacked);
            _tempDirs.Add(workDir);
            try
            {
                Directory.CreateDirectory(unpacked);
                ZipFile.ExtractToDirectory(item.Zip, unpacked);
                var root = CollapseSingleRoot(unpacked);

                // Zips are filed under their AssemblyName, so the folder name says which
                // project was built — no need to ask, even in a multi-project repo.
                var project = ProjectScanner.Scan(root).FirstOrDefault(p =>
                    p.IsExecutable &&
                    string.Equals(SafeName(p.AssemblyName), item.Name, StringComparison.OrdinalIgnoreCase));
                if (project is null)
                {
                    item.Outcome = "failed";
                    item.Detail = $"the zip has no executable project named {item.Name}";
                    return;
                }

                var args = PublishArgs(project, workDir);
                Write($"{args}\n", InkMuted);
                _sawRestoreSourceError = false;

                var exit = await RunAsync(dotnet, args, Path.GetDirectoryName(project.Path)!);
                if (exit != 0)
                {
                    item.Outcome = "failed";
                    item.Detail = _batchCancel ? "cancelled"
                        : _sawRestoreSourceError ? "a package it needs is not in the local cache"
                        : $"dotnet publish exited with code {exit}";
                    return;
                }

                CollectUsedPackages(workDir, item.Name);

                var exe = FindExe(workDir, project.AssemblyName);
                if (exe is null)
                {
                    item.Outcome = "failed";
                    item.Detail = "the build produced no .exe";
                    return;
                }

                Directory.CreateDirectory(outputDir);
                var target = Path.Combine(outputDir, Path.GetFileName(exe));

                // Identical output means nothing changed: leave the file and its
                // timestamp alone, so what did change stands out.
                if (File.Exists(target) && Sha256Of(target) == Sha256Of(exe))
                {
                    item.Outcome = "unchanged";
                    item.Built = true;
                    return;
                }

                var existed = File.Exists(target);
                try
                {
                    if (SamePath(target, _runningAs))
                    {
                        ReplaceRunningExe(exe, target);
                        item.Detail = $"now running as {Path.GetFileName(_runningAs)}; start " +
                                      $"{Path.GetFileName(target)} to use the new build";
                    }
                    else
                    {
                        File.Copy(exe, target, overwrite: true);
                    }
                }
                catch (IOException ex)
                {
                    // Almost always the app is open somewhere. Its old exe stays as it was.
                    item.Outcome = "failed";
                    item.Detail = $"could not replace {Path.GetFileName(target)} — is it running? {ex.Message}";
                    return;
                }

                item.Outcome = existed ? "updated" : "new";
                item.Built = true;
            }
            catch (Exception ex)
            {
                item.Outcome = "failed";
                item.Detail = ex.Message;
            }
            finally
            {
                TryDelete(workDir);
                TryDelete(unpacked);
                _tempDirs.Remove(workDir);
                _tempDirs.Remove(unpacked);
            }
        }

        /// <summary>Windows refuses to overwrite or delete a running exe but does allow
        /// renaming it. So the running copy steps aside as &lt;name&gt;-OLD.exe — the
        /// fallback, replacing any earlier one — and the new build takes the real name.
        /// Should the copy fail, the running exe is moved back so the real name is
        /// never left empty.</summary>
        void ReplaceRunningExe(string newExe, string target)
        {
            var folder = Path.GetDirectoryName(target)!;
            var fallback = Path.Combine(folder, Path.GetFileNameWithoutExtension(target) + "-OLD.exe");

            if (File.Exists(fallback)) File.Delete(fallback);
            File.Move(target, fallback);
            try
            {
                File.Copy(newExe, target);
            }
            catch
            {
                try { File.Move(fallback, target); } catch { /* leave it as -OLD; still runnable */ }
                throw;
            }

            _runningAs = fallback;
            _selfReplaced = true;
        }

        void ReportBatch(List<BatchItem> items, string outputDir, string history, string? record,
                         TimeSpan elapsed)
        {
            int Tally(string outcome) => items.Count(i => i.Outcome == outcome);

            var changed = items.Count(i => i.Outcome is "updated" or "new");
            var failed = Tally("failed");
            var took = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
            var attempted = items.Count(i => i.Outcome is "updated" or "new" or "unchanged" or "failed");

            ClearNotes();
            Note($"Rebuilt {attempted - failed} of {attempted} in {took} — {changed} changed, " +
                 $"{Tally("unchanged")} unchanged, {failed} failed.",
                 failed > 0 ? InkWarn : InkOk);

            foreach (var item in items) NoteOutcome(item);

            // Exes that cannot be regenerated, because no source was ever filed for them.
            // A -OLD fallback belongs to a project that does have history, so it is not one.
            var noHistory = Directory.EnumerateFiles(outputDir)
                .Where(f => Path.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Where(stem =>
                {
                    var owner = stem.EndsWith("-OLD", StringComparison.OrdinalIgnoreCase) ? stem[..^4] : stem;
                    return !Directory.Exists(Path.Combine(history, owner));
                })
                .OrderBy(stem => stem, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (noHistory.Count > 0)
                Note($"No source in HISTORY, so these can't be rebuilt: {string.Join(", ", noHistory)}",
                     InkMuted);

            if (record is not null)
                Note($"Checksums written to {record}", InkMuted);

            if (_selfReplaced)
                Note($"RepoBuilder was rebuilt. This window is now running " +
                     $"{Path.GetFileName(_runningAs)}; start RepoBuilder.exe to use the new build.",
                     InkWarn);

            AddAction("Open the folder", () => { OpenOutputFolder(); return Task.CompletedTask; });
            SetStatus($"Rebuild all done — {changed} changed, {Tally("unchanged")} unchanged, {failed} failed.",
                      failed > 0 ? "Brush.Error" : "Brush.Ok");

            Write($"{new string('─', 60)}\n", InkMuted);
            foreach (var item in items)
                Write($"{item.Outcome,-10} {item.Name}{(item.Detail.Length > 0 ? " — " + item.Detail : "")}\n",
                      OutcomeInk(item));
            if (record is not null) Write($"\n{record}\n", InkAccent);
        }

        /// <summary>Reads what a build actually resolved out of its project.assets.json,
        /// written by restore into the artifacts obj folder and deleted moments later.
        /// "libraries" holds every package including transitive ones; downloadDependencies
        /// holds the runtime and targeting packs, which is what most of an exe is made of.</summary>
        void CollectUsedPackages(string workDir, string projectName)
        {
            void Attribute(string key, string by)
            {
                if (!_usedPackages.TryGetValue(key, out var who))
                    _usedPackages[key] = who = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                who.Add(by);
            }

            try
            {
                var objRoot = Path.Combine(workDir, "artifacts", "obj");
                if (!Directory.Exists(objRoot)) return;

                foreach (var file in Directory.EnumerateFiles(objRoot, "project.assets.json",
                                                              SearchOption.AllDirectories))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;

                    if (root.TryGetProperty("libraries", out var libraries) &&
                        libraries.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var library in libraries.EnumerateObject())
                        {
                            // Keys are "Id/Version". Skip project references, which are
                            // source in the same repo rather than anything in the cache.
                            if (library.Value.TryGetProperty("type", out var kind) &&
                                !string.Equals(kind.GetString(), "package",
                                               StringComparison.OrdinalIgnoreCase))
                                continue;
                            Attribute(library.Name, projectName);
                        }
                    }

                    if (root.TryGetProperty("project", out var spec) &&
                        spec.TryGetProperty("frameworks", out var frameworks) &&
                        frameworks.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var framework in frameworks.EnumerateObject())
                        {
                            if (!framework.Value.TryGetProperty("downloadDependencies", out var downloads)
                                || downloads.ValueKind != JsonValueKind.Array)
                                continue;

                            foreach (var dependency in downloads.EnumerateArray())
                            {
                                var name = dependency.TryGetProperty("name", out var n) ? n.GetString() : null;
                                var version = dependency.TryGetProperty("version", out var v) ? v.GetString() : null;
                                if (name is null || version is null) continue;

                                // Written as a range, "[10.0.12, 10.0.12]".
                                version = version.Trim('[', ']', ' ');
                                var comma = version.IndexOf(',');
                                if (comma >= 0) version = version[..comma].Trim();
                                if (version.Length > 0) Attribute($"{name}/{version}", projectName);
                            }
                        }
                    }
                }
            }
            catch { /* the report simply has less to go on */ }
        }

        /// <summary>After a clean sweep of every project, says which package versions in
        /// the cache were actually used and which were not, and offers to move the unused
        /// ones aside. Only ever after a batch where everything built: a project that
        /// failed would make its packages look unused.</summary>
        void ReportPackageUse(List<BatchItem> items)
        {
            if (!PackageCache.Exists || _usedPackages.Count == 0) return;

            if (items.Any(i => i.Outcome == "failed") || _batchCancel)
            {
                Note("Not reporting unused packages: this batch did not finish cleanly, so " +
                     "packages a failed project needs would look unused.", InkMuted);
                return;
            }

            var rid = $"{_rid.SelectedItem}";
            var cache = PackageCache.Read();

            var runtime = BuildStamp("BuiltWithRuntime");

            var used = new List<(string Key, double Mb, string By)>();
            var other = new List<(string Key, double Mb, string By)>();
            var unused = new List<(string Key, double Mb, string Path)>();

            foreach (var entry in cache)
            {
                foreach (var version in entry.Value)
                {
                    var folder = Path.Combine(DotnetLocator.PortablePackages, entry.Key, version);
                    var mb = FolderSizeMb(folder);
                    var key = $"{entry.Key}/{version}";

                    if (_usedPackages.TryGetValue(key, out var who))
                    {
                        // Names only when few enough to act on (see _usedPackages).
                        var by = who.Count == items.Count ? $"all {who.Count}"
                               : who.Count > 4 ? $"{who.Count} projects"
                               : string.Join(", ", who);
                        used.Add((key, mb, by));
                    }
                    else if (NeededForOtherSettings(entry.Key, version, rid, runtime))
                    {
                        other.Add((key, mb, ""));
                    }
                    else unused.Add((key, mb, folder));
                }
            }

            _tabs.SelectedIndex = 1;
            Write($"\n{new string('─', 60)}\n", InkMuted);
            Write($"Package cache · {DotnetLocator.PortablePackages}\n", InkAccent);

            void Section(string title, IEnumerable<(string Key, double Mb, string By)> rows, string ink)
            {
                var list = rows.OrderByDescending(r => r.Mb).ToList();
                Write($"\n{title}: {list.Count} versions, {list.Sum(r => r.Mb):F0} MB\n", ink);
                if (list.Count == 0) return;

                var width = list.Max(r => r.Key.Length);
                foreach (var row in list)
                    Write($"  {row.Mb,8:F1} MB  {row.Key.PadRight(width)}" +
                          (row.By.Length > 0 ? $"  {row.By}" : "") + "\n", InkMuted);
            }

            Section($"used by these {items.Count} projects", used, InkOk);
            Section($"kept for other settings than {rid}, Trim or ReadyToRun", other, InkWarn);
            Section("used by nothing here",
                    unused.Select(u => (u.Key, u.Mb, "")), InkError);

            var wasted = unused.Sum(u => u.Mb);
            if (unused.Count == 0)
            {
                Note("Every package in the cache was used by this batch.", InkOk);
                return;
            }

            Note($"{unused.Count} package versions ({wasted:F0} MB) were not used by any of the " +
                 $"{items.Count} projects, and are not the packs for another runtime ID or for " +
                 "Trim and ReadyToRun. The Output tab lists them.", InkWarn);
            AddAction($"Move those {unused.Count} aside into packages-unused",
                      () => { QuarantinePackages(unused); return Task.CompletedTask; });
        }

        /// <summary>Runtime and targeting packs for a different runtime ID, and the
        /// toolchains behind Trim and ReadyToRun. Unused by this batch, but needed the
        /// moment a box is ticked or the RID changes — and on an air-gapped machine they
        /// cannot simply be fetched again.</summary>
        static bool NeededForOtherSettings(string id, string version, string currentRid, string runtime)
        {
            // Trim and ReadyToRun toolchains could be wanted on the next build, but only at
            // the version this SDK asks for; any other version is never requested again,
            // and keeping it regardless would keep it forever. With no runtime stamp to
            // compare against, the keep is dropped rather than guessed — the package falls
            // through to unused, which offers a move aside, not a delete.
            if (id.Contains("illink", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("crossgen", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("ilcompiler", StringComparison.OrdinalIgnoreCase))
                return runtime.Length > 0 && version.Equals(runtime, StringComparison.OrdinalIgnoreCase);

            // Packs for a runtime ID other than the one selected: a switch of the dropdown
            // would want them, and on an air-gapped machine they cannot be fetched again.
            foreach (var rid in new[] { "win-x64", "win-arm64", "win-x86",
                                        "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" })
            {
                if (!rid.Equals(currentRid, StringComparison.OrdinalIgnoreCase) &&
                    id.EndsWith("." + rid, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Moves rather than deletes, into a packages-unused folder beside the
        /// cache. If a later build turns out to need one, it can be moved back instead of
        /// downloaded, which matters on a machine with no network.</summary>
        void QuarantinePackages(List<(string Key, double Mb, string Path)> unused)
        {
            var root = Path.GetDirectoryName(DotnetLocator.PortablePackages)!;
            var aside = Path.Combine(root, "packages-unused");
            var moved = 0;
            var freed = 0d;

            foreach (var item in unused)
            {
                try
                {
                    var parts = item.Key.Split('/');
                    var target = Path.Combine(aside, parts[0], parts[1]);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                    Directory.Move(item.Path, target);

                    // Leave no empty id folder behind in the cache.
                    var parent = Path.GetDirectoryName(item.Path)!;
                    if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                        Directory.Delete(parent);

                    moved++;
                    freed += item.Mb;
                }
                catch (Exception ex)
                {
                    Write($"could not move {item.Key}: {ex.Message}\n", InkError);
                }
            }

            ClearNotes();
            Note($"Moved {moved} package versions ({freed:F0} MB) to {aside}.", InkOk);
            Note("Nothing is deleted. If a build later asks for one of them, move that folder " +
                 "back into packages rather than fetching it over the network. Once a few " +
                 "batches have passed cleanly, the folder can go.", InkMuted);
            RefreshSizes();
        }

        void ShowElapsed()
        {
            var t = _runFor.Elapsed;
            var clock = t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}";
            _elapsed.Content = _busy && _busyLabel.Length > 0
                ? $"{_busyLabel} · {clock}"
                : $"Took {clock}";
            // The kit reserves the glowing dot for a single standalone pill showing live
            // state, which is exactly this one. Off again when the run ends, so a finished
            // time reads as a record rather than as something still happening.
            _elapsed.Tag = _busy ? "Glow" : "";
        }

        static string OutcomeInk(BatchItem item) => item.Outcome switch
        {
            "failed" => InkError,
            "new" or "updated" => InkOk,
            _ => InkMuted
        };

        /// <summary>One line per project on the Status tab: green when its exe
        /// changed, muted when unchanged or skipped, red with the reason when it
        /// failed.</summary>
        void NoteOutcome(BatchItem item) =>
            Note($"{item.Name} — {item.Outcome}{(item.Detail.Length > 0 ? ": " + item.Detail : "")}",
                 OutcomeInk(item));

        /// <summary>Writes &lt;timestamp&gt;-&lt;folder&gt;.sha256 into Save to in the format
        /// sha256sum reads: lowercase hex, space, '*', name, CRLF. First every exe in the
        /// folder, rebuilt or not — a snapshot of what is on disk. Then, for each exe this
        /// batch actually built, the zip it was built from, by path relative to the file
        /// with forward slashes. A failed project keeps its old exe but gets no zip line,
        /// so every zip listed is one its exe demonstrably came from. Run
        /// sha256sum -c on it inside the folder to verify the lot.</summary>
        static string WriteChecksums(string outputDir, List<BatchItem> items)
        {
            var label = SafeName(new DirectoryInfo(outputDir).Name);
            if (label.Length == 0) label = "REPO";
            var file = Path.Combine(outputDir, $"{DateTime.Now:yyyy-MM-dd-HH-mm-ss}-{label}.sha256");

            var text = new StringBuilder();
            foreach (var exe in Directory.EnumerateFiles(outputDir)
                         .Where(f => Path.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            {
                text.Append(Sha256Of(exe)).Append(" *").Append(Path.GetFileName(exe)).Append("\r\n");
            }

            foreach (var item in items.Where(i => i.Built)
                         .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(outputDir, item.Zip).Replace('\\', '/');
                text.Append(Sha256Of(item.Zip)).Append(" *").Append(relative).Append("\r\n");
            }

            File.WriteAllText(file, text.ToString(), new UTF8Encoding(false));
            return file;
        }

        /// <summary>The zip with the highest trailing number in a HISTORY folder.</summary>
        static string? NewestArchive(string dir, string name)
        {
            string? newest = null;
            var highest = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, name + "-*.zip"))
                {
                    var stem = Path.GetFileNameWithoutExtension(file);
                    var cut = stem.LastIndexOf('-');
                    if (cut < 0 || cut == stem.Length - 1) continue;
                    if (int.TryParse(stem[(cut + 1)..], out var n) && n > highest)
                    {
                        highest = n;
                        newest = file;
                    }
                }
            }
            catch { /* unreadable folder: treated as having no zip */ }
            return newest;
        }

        static string Sha256Of(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        /// <summary>True when Save to holds &lt;assemblyName&gt;.exe and something has it open so
        /// it cannot be overwritten — in practice, the app is running. Opening for write with
        /// every share flag allowed fails only on a real lock, and nothing is written, so the
        /// file and its timestamp are untouched. RepoBuilder's own exe is always false: the
        /// -OLD swap replaces it while it runs.</summary>
        static bool IsRunning(string assemblyName, string outputDir)
        {
            var self = typeof(MainWindow).Assembly.GetName().Name ?? "RepoBuilder";
            if (string.Equals(assemblyName, self, StringComparison.OrdinalIgnoreCase)) return false;

            var exe = Path.Combine(outputDir, assemblyName + ".exe");
            if (!File.Exists(exe)) return false;
            try
            {
                using var _ = new FileStream(exe, FileMode.Open, FileAccess.Write,
                                             FileShare.ReadWrite | FileShare.Delete);
                return false;
            }
            catch (IOException) { return true; }
            catch { return false; }   // e.g. read-only: not "running"; the copy reports it
        }

        static bool SamePath(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>An explicit OutputPath beats anything derived from ArtifactsPath,
        /// so the redirect quietly does nothing on such a repo. Worth saying out loud
        /// rather than leaving someone to wonder why obj and bin are still there.
        /// Looks at the project itself and at any Directory.Build.props/targets from
        /// the project folder up to the repo root, which is where MSBuild looks.</summary>
        bool PinsItsOwnOutputPath(ProjectInfo project, out string pinnedBy)
        {
            pinnedBy = "";
            try
            {
                var files = new List<string> { project.Path };

                var dir = Path.GetDirectoryName(project.Path);
                var stop = Path.GetFullPath(_repoRoot.Length > 0 ? _repoRoot : dir ?? "");
                while (!string.IsNullOrEmpty(dir))
                {
                    foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
                    {
                        var candidate = Path.Combine(dir, name);
                        if (File.Exists(candidate)) files.Add(candidate);
                    }
                    if (string.Equals(Path.GetFullPath(dir), stop, StringComparison.OrdinalIgnoreCase))
                        break;
                    dir = Path.GetDirectoryName(dir);
                }

                foreach (var file in files)
                {
                    XDocument doc;
                    try { doc = XDocument.Load(file); }
                    catch { continue; }

                    var hit = doc.Descendants().Any(
                        e => e.Name.LocalName is "OutputPath" or "BaseOutputPath"
                                              or "BaseIntermediateOutputPath");
                    if (hit)
                    {
                        pinnedBy = Path.GetFileName(file);
                        return true;
                    }
                }
            }
            catch { /* the note is a courtesy; never fail a build over it */ }

            return false;
        }

        async Task<int> RunAsync(string exe, string arguments, string workingDirectory,
                                 string? packagesOverride = null)
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment["DOTNET_NOLOGO"] = "1";
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            // Verifying each package signature asks the issuing CA about revocation — an
            // outbound call even for packages from the local cache, which offline means a
            // timeout per package and then NU3028. Offline mode uses the revocation list
            // already on the machine. The cost: a revocation newer than that list goes
            // unnoticed, but with no route out the online check could not run anyway.
            psi.Environment["NUGET_CERT_REVOCATION_MODE"] = "offline";
            // The CLI fetches workload advertising manifests in the background on every
            // build-type command. The once-a-day throttle only counts completed downloads,
            // so offline it retries on every project of a batch.
            psi.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
            // A blocked connection does not fail fast: a dropped SYN costs Windows' retry
            // sequence, about 21 seconds, and NuGet tries six times per request by default.
            // This is the attempt count — not NUGET_ENABLE_ENHANCED_HTTP_RETRY, which the
            // .NET 10 SDK no longer honours. Nothing depends on it if a later SDK drops it.
            psi.Environment["NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT"] = "1";

            // NuGet and MSBuild make their own scratch folders straight in %TEMP%
            // (NuGetScratch and friends) and never remove them. Giving the child its
            // own TMP means all of that lands somewhere we can delete wholesale.
            var scratch = Path.Combine(TempRoot, "scratch_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(scratch);
                psi.Environment["TMP"] = scratch;
                psi.Environment["TEMP"] = scratch;
            }
            catch { scratch = ""; }

            if (DotnetLocator.HasPortable)
                psi.Environment["DOTNET_ROOT"] = DotnetLocator.PortableRoot;
            if (packagesOverride is not null)
                psi.Environment["NUGET_PACKAGES"] = packagesOverride;
            else if (DotnetLocator.HasPortablePackages)
                psi.Environment["NUGET_PACKAGES"] = DotnetLocator.PortablePackages;

            var proc = new Process { StartInfo = psi };

            // MSBuild writes its errors to stdout, so both streams are merged.
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) WriteLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) WriteLine(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _current = proc;

            // WaitForExitAsync also waits for the redirected streams to drain,
            // which the Exited event does not.
            await proc.WaitForExitAsync();
            var code = proc.ExitCode;

            _current = null;
            proc.Dispose();
            if (scratch.Length > 0) TryDelete(scratch);
            return code;
        }

        /// <summary>Runs dotnet and returns stdout without writing to the log.
        /// Used for short queries like --version.</summary>
        static async Task<string> RunCaptureAsync(string exe, string arguments)
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.Environment["DOTNET_NOLOGO"] = "1";
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return output.Trim();
        }

        static string _tempRoot = "";
        static bool _tempRootIsFallback;

        /// <summary>Everything scratch lives under here. Resolved once, on first use.</summary>
        static string TempRoot
        {
            get
            {
                if (_tempRoot.Length == 0) _tempRoot = ResolveTempRoot(out _tempRootIsFallback);
                return _tempRoot;
            }
        }

        static bool TempRootIsFallback
        {
            get { _ = TempRoot; return _tempRootIsFallback; }
        }

        /// <summary>Prefers an existing \TEMP on any fixed drive, taking the lowest
        /// drive letter, the same rule Save to uses for \REPO. Falls back to the
        /// user's %TEMP% when no such folder exists or none of them is writable.
        /// A short root also keeps publish paths clear of the 260-character limit.</summary>
        static string ResolveTempRoot(out bool fallback)
        {
            try
            {
                foreach (var drive in DriveInfo.GetDrives()
                             .Where(d => d.DriveType == DriveType.Fixed)
                             .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (!drive.IsReady) continue;
                    var candidate = Path.Combine(drive.RootDirectory.FullName, "TEMP");
                    if (!Directory.Exists(candidate)) continue;

                    // An existing \TEMP is no use if it cannot be written to.
                    var mine = Path.Combine(candidate, "RepoBuilder");
                    try
                    {
                        Directory.CreateDirectory(mine);
                        var probe = Path.Combine(mine, ".write-test");
                        File.WriteAllText(probe, "");
                        File.Delete(probe);
                    }
                    catch { continue; }

                    fallback = false;
                    return mine;
                }
            }
            catch { /* fall through to the user profile */ }

            fallback = true;
            return Path.Combine(Path.GetTempPath(), "RepoBuilder");
        }

        static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* locked or gone; the sweep will get it next time */ }
        }

        /// <summary>Removes leftovers from runs that crashed or were killed. Anything
        /// touched in the last few hours is left alone in case a second instance is
        /// using it.</summary>
        static void SweepStaleTemp()
        {
            try
            {
                if (!Directory.Exists(TempRoot)) return;
                var cutoff = DateTime.UtcNow.AddHours(-6);

                foreach (var dir in Directory.EnumerateDirectories(TempRoot))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                            Directory.Delete(dir, recursive: true);
                    }
                    catch { }
                }

                // Loose files too, or a stray file in the root would never go.
                foreach (var file in Directory.EnumerateFiles(TempRoot))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>Removes the single-file extraction folders left by earlier builds
        /// of this exe. The runtime keys them on a hash of the bundle, so every new
        /// version gets a fresh folder and none of the old ones is ever cleaned up.
        /// Always the real %TEMP%: the host picks that before any of our code runs.
        /// Each folder is renamed before deletion — a rename fails outright when a
        /// DLL inside is loaded, so the live folder is skipped whole rather than
        /// being half-deleted underneath us.</summary>
        static void SweepSelfExtract()
        {
            try
            {
                var baseDir = Path.Combine(Path.GetTempPath(), ".net", "RepoBuilder");
                if (!Directory.Exists(baseDir)) return;

                foreach (var dir in Directory.EnumerateDirectories(baseDir))
                {
                    var parked = dir + ".stale";
                    try
                    {
                        if (Directory.Exists(parked)) Directory.Delete(parked, recursive: true);
                        Directory.Move(dir, parked);
                        Directory.Delete(parked, recursive: true);
                    }
                    catch { /* in use, or someone else got there first */ }
                }
            }
            catch { }
        }

        void CancelBuild()
        {
            var proc = _current;
            if (proc is null) return;
            try
            {
                proc.Kill(entireProcessTree: true);
                Note("Cancelled.", InkWarn);
                SetStatus("Cancelled.", "Brush.Warn");
            }
            catch (Exception ex)
            {
                Note($"Could not stop the build: {ex.Message}", InkError);
            }
        }

        static string? FindExe(string dir, string assemblyName)
        {
            var preferred = Path.Combine(dir, assemblyName + ".exe");
            if (File.Exists(preferred)) return preferred;

            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.exe")
                    .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
        }

        void OpenOutputFolder()
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_output.Text.Trim()}\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Note(ex.Message, InkError);
            }
        }

        // ---------------------------------------------------------------- log

        void WriteLine(string line)
        {
            // NU1301: source unreachable. NU1101: package not found in any source.
            // Both mean restore could not get what it needed, which on an offline box
            // is the primed-packages case rather than a mistake in the project.
            if (line.Contains("NU1301", StringComparison.Ordinal)
                || line.Contains("NU1101", StringComparison.Ordinal))
                _sawRestoreSourceError = true;

            Write(line + "\n", InkFor(line));
        }

        /// <summary>Which ink a line of build output is written in. Matched on the
        /// MSBuild and NuGet markers, not on wording, so it survives a localised SDK.</summary>
        static string InkFor(string line)
        {
            if (line.Contains(": error", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("error MSB", StringComparison.OrdinalIgnoreCase)
                || line.Contains("error NU", StringComparison.OrdinalIgnoreCase))
                return InkError;

            if (line.Contains(": warning", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("warning", StringComparison.OrdinalIgnoreCase))
                return InkWarn;

            if (line.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase))
                return InkOk;

            return Ink;
        }

        void Write(string text, string ink)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Write(text, ink)));
                return;
            }

            var run = new Run(text);
            run.SetResourceReference(TextElement.ForegroundProperty, ink);
            ((Paragraph)_log.Document.Blocks.LastBlock).Inlines.Add(run);
            _log.ScrollToEnd();
        }

        static Task OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { /* no browser available; the Status tab already shows the address */ }
            return Task.CompletedTask;
        }

        /// <summary>Unpacks a dotnet-sdk-*-win-x64.zip into an "sdk" folder beside the
        /// exe. The zip is a plain xcopy layout: no registry, no PATH, no installer.</summary>
        Task InstallSdkFromZip() => UnpackBeside(
            target: DotnetLocator.PortableRoot,
            dialogTitle: "Select the .NET SDK zip",
            filter: "SDK archives (dotnet-sdk-*.zip)|dotnet-sdk-*.zip|Zip archives (*.zip)|*.zip",
            verify: () => DotnetLocator.HasPortable,
            okMessage: "SDK ready. It will be found automatically from now on — " +
                       "keep the sdk folder next to the exe.",
            failMessage: "Unpacked, but no dotnet.exe turned up at the root of that folder. " +
                         "Check the zip is a Windows SDK archive rather than a runtime or a " +
                         "different platform.");

        /// <summary>Unpacks a primed NuGet global-packages folder into "packages"
        /// beside the exe, so restore resolves locally with no network.</summary>
        Task InstallPackagesFromZip() => UnpackBeside(
            target: DotnetLocator.PortablePackages,
            dialogTitle: "Select the primed packages zip",
            filter: "Zip archives (*.zip)|*.zip",
            verify: () => DotnetLocator.HasPortablePackages,
            okMessage: "Packages ready. Restore will resolve from here from now on — " +
                       "keep the packages folder next to the exe.",
            failMessage: "Unpacking produced nothing usable.");

        async Task UnpackBeside(string target, string dialogTitle, string filter,
                                Func<bool> verify, string okMessage, string failMessage)
        {
            if (target.Length == 0)
            {
                Note("Could not work out where this exe lives.", InkError);
                return;
            }

            var dlg = new OpenFileDialog { Title = dialogTitle, Filter = filter };
            var downloads = DownloadsFolder();
            if (downloads.Length > 0) dlg.InitialDirectory = downloads;
            if (dlg.ShowDialog(this) != true) return;

            var zipPath = dlg.FileName;

            SetBusy(true, "Unpacking");
            SetStatus("Unpacking…", "Brush.Accent");
            ClearNotes();
            Note($"Unpacking {Path.GetFileName(zipPath)}", Ink);
            Note("This can be a few hundred MB, so give it a moment.", InkMuted);

            try
            {
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(target);
                    ZipFile.ExtractToDirectory(zipPath, target, overwriteFiles: true);
                });
            }
            catch (Exception ex)
            {
                Note(ex.Message, InkError);
                SetStatus("Unpacking failed.", "Brush.Error");
                SetBusy(false);
                return;
            }

            SetBusy(false);

            if (verify())
            {
                RefreshSizes();
                Note(okMessage, InkOk);
                Note(target, InkMuted);
                SetStatus("Unpacked. Press Build exe.", "Brush.Ok");
            }
            else
            {
                Note(failMessage, InkError);
                SetStatus("That zip was not what was expected.", "Brush.Error");
            }
        }

        /// <summary>Fills the portable packages folder by publishing with NUGET_PACKAGES
        /// aimed at it. Needs network. Uses the loaded repo's own project, so exactly what
        /// it asks for is fetched; with none loaded, a throwaway WPF project, whose
        /// self-contained publish pulls the NETCore and WindowsDesktop runtime packs only.</summary>
        async Task PrimePackages()
        {
            var dotnet = DotnetLocator.Find();
            if (dotnet is null)
            {
                Note("Set up the SDK first — there is nothing to fetch packages with.", InkError);
                return;
            }

            var target = DotnetLocator.PortablePackages;
            if (target.Length == 0)
            {
                Note("Could not work out where this exe lives.", InkError);
                return;
            }

            SetBusy(true, "Fetching packages");
            ClearLog();
            ClearNotes();
            _sawRestoreSourceError = false;
            SetStatus("Fetching packages…", "Brush.Accent");

            var rid = (string)_rid.SelectedItem;
            Note("Fetching the packages needed to build without a network later.", Ink);
            Note("This machine needs internet for this step.", InkMuted);
            Note($"Storing them in {target}", InkMuted);

            var scratch = Path.Combine(TempRoot, "prime_" + Guid.NewGuid().ToString("N"));
            _tempDirs.Add(scratch);

            string projectPath;
            string workDir;
            bool usingRealProject = _project.SelectedItem is ProjectInfo;

            if (_project.SelectedItem is ProjectInfo selected)
            {
                // The repo's own csproj decides what gets pulled: ReadyToRun set inside
                // it needs crossgen2, AOT the ILCompiler packs — a synthetic project
                // would fetch neither.
                projectPath = selected.Path;
                workDir = Path.GetDirectoryName(selected.Path)!;
                Note($"Working from {Path.GetFileName(projectPath)}, so exactly what this repo " +
                     "asks for is fetched.", InkMuted);
            }
            else
            {
                var version = await RunCaptureAsync(dotnet, "--version");
                var major = version.Split('.').FirstOrDefault() ?? "10";
                if (!int.TryParse(major, out _)) major = "10";
                var tfm = $"net{major}.0-windows";

                Note($"No repo is loaded, so only the basics are fetched (SDK {version}, {tfm}).",
                     InkMuted);
                Note("Load the repo you actually intend to build and run this again for full " +
                     "coverage.", InkWarn);

                try
                {
                    Directory.CreateDirectory(scratch);
                    File.WriteAllText(Path.Combine(scratch, "Prime.csproj"), $"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>{tfm}</TargetFramework>
    <UseWPF>true</UseWPF>
    <EnableDefaultApplicationDefinition>false</EnableDefaultApplicationDefinition>
    <StartupObject>Prime.Program</StartupObject>
  </PropertyGroup>
</Project>
""");
                    File.WriteAllText(Path.Combine(scratch, "Program.cs"), """
namespace Prime
{
    public static class Program
    {
        [System.STAThread]
        public static void Main() { }
    }
}
""");
                }
                catch (Exception ex)
                {
                    Note(ex.Message, InkError);
                    SetStatus("Could not create the scratch project.", "Brush.Error");
                    TryDelete(scratch);
                    SetBusy(false);
                    return;
                }

                projectPath = Path.Combine(scratch, "Prime.csproj");
                workDir = scratch;
            }

            var outDir = Path.Combine(scratch, "out");
            var args = new StringBuilder();
            args.Append("publish \"").Append(projectPath).Append('"');
            args.Append(" -c Release -r ").Append(rid);
            args.Append(" --self-contained true");
            args.Append(" -p:PublishSingleFile=true");
            args.Append(" -p:IncludeNativeLibrariesForSelfExtract=true");
            args.Append(" -p:EnableCompressionInSingleFile=true");
            args.Append(" -p:NuGetAudit=false");
            // Same rule as PublishArgs: never trim a desktop project. The throwaway
            // project is WPF, and with no repo loaded nothing has cleared the box.
            var primesDesktop = _project.SelectedItem is not ProjectInfo primed || primed.UsesDesktop;
            if (_trim.IsChecked == true && !primesDesktop) args.Append(" -p:PublishTrimmed=true");
            if (_r2r.IsChecked == true) args.Append(" -p:PublishReadyToRun=true");
            args.Append(" -v minimal -o \"").Append(outDir).Append('"');
            var extraArgs = _extraArgs.Text.Trim();
            if (extraArgs.Length > 0) args.Append(' ').Append(extraArgs);

            Directory.CreateDirectory(target);
            var sw = Stopwatch.StartNew();
            int exit;
            try
            {
                exit = await RunAsync(dotnet, args.ToString(), workDir, packagesOverride: target);
            }
            catch (Exception ex)
            {
                Note(ex.Message, InkError);
                SetStatus("Fetching could not be started.", "Brush.Error");
                TryDelete(scratch);
                SetBusy(false);
                return;
            }
            sw.Stop();

            if (exit != 0)
            {
                Note("Fetching failed. The Output tab has the detail.", InkError);
                if (_sawRestoreSourceError)
                    Note("The package source could not be reached — this step needs a machine " +
                         "with internet.", InkWarn);
                SetStatus("Fetching failed — see the Output tab.", "Brush.Error");
                TryDelete(scratch);
                SetBusy(false);
                return;
            }

            var size = await Task.Run(() => FolderSizeMb(target));
            RefreshSizes();
            Note($"Done — {size:F0} MB stored in {sw.Elapsed.TotalSeconds:F0} seconds.", InkOk);
            Note("Copy RepoBuilder.exe, the sdk folder and the packages folder together to the " +
                 "machine with no network.", InkMuted);
            if (usingRealProject)
                Note("This covers this repo and this runtime. A different repo, or a different " +
                     "runtime, may need more — run this again with those selected.", InkMuted);
            TryDelete(scratch);
            SetStatus($"Packages stored ({size:F0} MB).", "Brush.Ok");
            SetBusy(false);
        }

        static double FolderSizeMb(string dir)
        {
            try
            {
                long total = 0;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
                return total / 1024d / 1024d;
            }
            catch { return 0; }
        }

        void ClearLog()
        {
            _log.Document.Blocks.Clear();
            _log.Document.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
        }

        void CopyLog()
        {
            var range = new TextRange(_log.Document.ContentStart, _log.Document.ContentEnd);
            try
            {
                Clipboard.SetText(range.Text);
                SetStatus("Log copied to the clipboard.", "Brush.Ok");
            }
            catch
            {
                SetStatus("The clipboard was busy — try again.", "Brush.Warn");
            }
        }

        // -------------------------------------------------------------- state

        /// <summary>The status strip. The dot's colour ranks the line — busy, went well,
        /// needs a look, failed — and defaults to the neutral stale grey for a line that is
        /// merely informational. The words always say what happened, so the colour never
        /// carries the meaning on its own.</summary>
        void SetStatus(string text, string dot = "Brush.Stale")
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SetStatus(text, dot)));
                return;
            }
            _status.Text = text;
            _statusDot.SetResourceReference(Control.ForegroundProperty, dot);
        }

        /// <summary>Locks the window down while work runs. <paramref name="label"/> is what
        /// the header pill calls that work; it is ignored when going idle.</summary>
        void SetBusy(bool busy, string label = "")
        {
            _busy = busy;
            if (busy)
            {
                _busyLabel = label;
                _runFor.Restart();
                _elapsed.Visibility = Visibility.Visible;
                ShowElapsed();
                _clock.Start();
            }
            else
            {
                _clock.Stop();
                _runFor.Stop();
                ShowElapsed();
            }

            _build.IsEnabled = !busy;
            _rebuildAll.IsEnabled = !busy;
            _cancel.IsEnabled = busy;
            _project.IsEnabled = !busy && _project.Items.Count > 0;
            _rid.IsEnabled = !busy;
            _trim.IsEnabled = !busy &&
                              !(_project.SelectedItem is ProjectInfo trimTarget && trimTarget.UsesDesktop);
            _r2r.IsEnabled = !busy;
            _repro.IsEnabled = !busy;
            _verbosity.IsEnabled = !busy;
            _extraArgs.IsEnabled = !busy;
            _progress.IsIndeterminate = busy;
            _progress.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
            if (busy) _openOutput.IsEnabled = false;

            // Everything that chooses what a run works on, so the source or output folder
            // cannot change under a running batch. Copy log, Clear and Copy list stay
            // live: reading is always safe.
            foreach (var control in _lockedWhileBusy) control.IsEnabled = !busy;
            foreach (var control in _statusActions) control.IsEnabled = !busy;
        }

        void Cleanup()
        {
            try { _sweep.Stop(); } catch { }
            try { _current?.Kill(entireProcessTree: true); } catch { }
            foreach (var dir in _tempDirs) TryDelete(dir);
        }
    }
}
