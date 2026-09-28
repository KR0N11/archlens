using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ArchLens.Core.Loading;

public record LoadedProject(string Name, string Directory, CSharpCompilation Compilation);

// Turns a folder of .csproj projects into Roslyn compilations without running a build.
// One compilation per project, like MSBuild does, but NuGet packages are never restored:
// calls between the repo's own classes resolve through the semantic model, and types from
// missing packages (EF Core's DbSet, for example) are still visible by name.
public static class RepoLoader
{
    private static readonly string[] SkippedFolders = { "bin", "obj", ".git", "node_modules" };

    // What the SDK adds as global usings when <ImplicitUsings> is on. We add the same list
    // ourselves because we never run the build that would normally generate it.
    private static readonly string[] BaseUsings =
    {
        "System", "System.Collections.Generic", "System.IO", "System.Linq",
        "System.Net.Http", "System.Threading", "System.Threading.Tasks",
    };

    private static readonly string[] WebUsings =
    {
        "System.Net.Http.Json", "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Routing", "Microsoft.Extensions.Configuration",
        "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging",
    };

    // Loads every project under root. Test projects are skipped unless asked for,
    // because they call everything and would drown the real architecture.
    public static List<LoadedProject> Load(string root, bool includeTests = false)
    {
        var allProjects = FindFiles(root, "*.csproj");
        var projectFiles = allProjects
            .Where(p => includeTests || !IsTestProject(p))
            .ToList();

        // Each .cs file belongs to the closest project folder above it. Skipped test projects
        // still claim their files here, so those files don't fall through to a parent project.
        var filesByProject = allProjects.ToDictionary(p => p, _ => new List<string>());
        var projectDirs = allProjects
            .Select(p => (File: p, Dir: Path.GetDirectoryName(p)!))
            .OrderByDescending(p => p.Dir.Length)
            .ToList();
        foreach (var cs in FindFiles(root, "*.cs"))
        {
            var owner = projectDirs.FirstOrDefault(p => IsUnder(cs, p.Dir));
            // A file outside every project is not part of the app.
            if (owner.File != null)
                filesByProject[owner.File].Add(cs);
        }

        var platformRefs = PlatformReferences();
        var built = new Dictionary<string, LoadedProject>(StringComparer.OrdinalIgnoreCase);
        var result = new List<LoadedProject>();

        // Build in dependency order so a project can reference the compilations it depends on.
        foreach (var projectFile in TopologicalOrder(projectFiles))
        {
            var xml = XDocument.Load(projectFile);
            var dir = Path.GetDirectoryName(projectFile)!;
            var name = Path.GetFileNameWithoutExtension(projectFile);
            var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);

            var trees = filesByProject[projectFile]
                .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parseOptions, path: Path.GetRelativePath(root, f)))
                .ToList();
            var usings = GlobalUsings(xml, BuildProps(root, dir));
            // Only add the synthetic usings file when there is something to add.
            if (usings.Count > 0)
            {
                var text = string.Join("\n", usings.Select(u => $"global using {u};"));
                trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path: "<implicit-usings>"));
            }

            var references = new List<MetadataReference>(platformRefs);
            foreach (var reference in ProjectReferences(projectFile, xml))
            {
                // A reference to a project we skipped (a test helper, say) is simply left out.
                if (built.TryGetValue(reference, out var dependency))
                    references.Add(dependency.Compilation.ToMetadataReference());
            }

            var compilation = CSharpCompilation.Create(
                name,
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var loaded = new LoadedProject(name, Path.GetRelativePath(root, dir), compilation);
            built[Path.GetFullPath(projectFile)] = loaded;
            result.Add(loaded);
        }
        return result;
    }

    // Test projects are recognized by name, which is the .NET convention (Foo.Tests, Foo.UnitTests).
    private static bool IsTestProject(string projectFile)
    {
        var name = Path.GetFileNameWithoutExtension(projectFile);
        return name.Contains("Test", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> FindFiles(string root, string pattern)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            found.AddRange(Directory.GetFiles(dir, pattern));
            foreach (var sub in Directory.GetDirectories(dir))
            {
                // bin/obj hold generated copies of the source; walking them would double every class.
                if (!SkippedFolders.Contains(Path.GetFileName(sub)))
                    pending.Push(sub);
            }
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static bool IsUnder(string file, string dir) =>
        file.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static List<string> ProjectReferences(string projectFile, XDocument xml)
    {
        var dir = Path.GetDirectoryName(projectFile)!;
        return xml.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => v != null)
            .Select(v => Path.GetFullPath(Path.Combine(dir, v!.Replace('\\', Path.DirectorySeparatorChar))))
            .ToList();
    }

    // Orders projects so dependencies come first. MSBuild forbids reference cycles,
    // so a plain depth-first walk is enough.
    private static List<string> TopologicalOrder(List<string> projectFiles)
    {
        var known = projectFiles.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        void Visit(string file)
        {
            // Already placed, or referenced but not loaded (skipped test project, outside the repo).
            if (!visited.Add(file) || !known.Contains(file))
                return;
            foreach (var dependency in ProjectReferences(file, XDocument.Load(file)))
                Visit(dependency);
            order.Add(file);
        }

        foreach (var file in projectFiles)
            Visit(Path.GetFullPath(file));
        return order;
    }

    // MSBuild automatically imports Directory.Build.props from the project folder and every
    // folder above it. Repos often switch on ImplicitUsings there, so we read them too.
    private static List<XDocument> BuildProps(string root, string projectDir)
    {
        var found = new List<XDocument>();
        var stop = Path.GetFullPath(root);
        for (var dir = Path.GetFullPath(projectDir); dir != null; dir = Path.GetDirectoryName(dir))
        {
            var props = Path.Combine(dir, "Directory.Build.props");
            if (File.Exists(props))
                found.Add(XDocument.Load(props));
            // Never read above the repo we were asked to analyze.
            if (dir == stop)
                break;
        }
        return found;
    }

    private static List<string> GlobalUsings(XDocument xml, List<XDocument> buildProps)
    {
        var usings = new List<string>();
        var sdk = xml.Root?.Attribute("Sdk")?.Value ?? "";
        var all = buildProps.Append(xml).ToList();
        var implicitOn = all.SelectMany(d => d.Descendants())
            .Any(e => e.Name.LocalName == "ImplicitUsings" && e.Value.Trim() is "enable" or "true");
        // Without <ImplicitUsings> the SDK adds nothing, so neither do we.
        if (implicitOn)
        {
            usings.AddRange(BaseUsings);
            // Web projects get the ASP.NET namespaces on top of the base list.
            if (sdk.Contains("Web", StringComparison.OrdinalIgnoreCase))
                usings.AddRange(WebUsings);
            // Worker services get the hosting namespaces without the ASP.NET ones.
            else if (sdk.Contains("Worker", StringComparison.OrdinalIgnoreCase))
                usings.AddRange(WebUsings.Where(u => u.StartsWith("Microsoft.Extensions")));
        }
        // Explicit <Using Include="..."/> items are added whether or not implicit usings are on.
        usings.AddRange(all.SelectMany(d => d.Descendants())
            .Where(e => e.Name.LocalName == "Using" && e.Attribute("Include") != null && e.Attribute("Alias") == null && e.Attribute("Static") == null)
            .Select(e => e.Attribute("Include")!.Value));
        return usings.Distinct().ToList();
    }

    // The .NET and ASP.NET Core framework assemblies from the runtime ArchLens itself runs on.
    // They ship with the SDK, so no download is needed.
    private static List<MetadataReference> PlatformReferences()
    {
        var coreDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var dirs = new List<string> { coreDir };
        var aspDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(coreDir)!)!, "Microsoft.AspNetCore.App", Path.GetFileName(coreDir));
        // ASP.NET Core is only present when the full SDK is installed; without it, web types resolve by name only.
        if (Directory.Exists(aspDir))
            dirs.Add(aspDir);

        var refs = new List<MetadataReference>();
        foreach (var dll in dirs.SelectMany(d => Directory.GetFiles(d, "*.dll")))
        {
            // Some files in the runtime folder are native libraries, not .NET assemblies; Roslyn can't read those.
            if (IsManagedAssembly(dll))
                refs.Add(MetadataReference.CreateFromFile(dll));
        }
        return refs;
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            System.Reflection.AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}
