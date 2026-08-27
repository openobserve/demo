using dotnet_demo.ServiceKit;
using dotnet_demo.ServiceKit.Topology;

// One binary, one role per process. The role is chosen with --service <name> or the
// SERVICE_NAME environment variable; start-platform.sh launches one process per service,
// so each gets its own service.name, port and PID — a real multi-service topology.
var requested = ResolveRoleName(args);

if (string.IsNullOrWhiteSpace(requested) || requested is "--list" or "list")
{
    Console.WriteLine("dotnet_demo platform services:");
    Console.WriteLine();
    foreach (var s in ServiceCatalog.Services)
    {
        var deps = s.Routes.SelectMany(r => r.Steps.OfType<CallStep>())
            .Select(c => c.TargetService.Replace("dotnet_demo-", string.Empty))
            .Distinct()
            .ToArray();

        Console.WriteLine($"  {s.Name,-42} :{s.Port}  [{s.Tier}]");
        Console.WriteLine($"      {s.Description}");
        if (deps.Length > 0)
        {
            Console.WriteLine($"      depends on: {string.Join(", ", deps)}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"  {ServiceCatalog.LegacyAdapterName,-42} :{ServiceCatalog.LegacyAdapterPort}  [legacy]");
    Console.WriteLine("      .NET Framework 4.7.2 adapter — separate project, see dotnet_demo.Legacy.MainframeAdapter");
    Console.WriteLine();
    Console.WriteLine("Usage: dotnet dotnet_demo.Platform.dll --service <name>");
    return 0;
}

var definition = ServiceCatalog.Find(requested);
if (definition is null)
{
    Console.Error.WriteLine($"error: unknown service '{requested}'. Run with --list to see the catalog.");
    return 2;
}

await PlatformService.RunAsync(definition, args);
return 0;

static string? ResolveRoleName(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] is "--service" or "-s")
        {
            return args[i + 1];
        }
    }

    if (args.Length == 1 && !args[0].StartsWith("-"))
    {
        return args[0];
    }

    if (args.Length == 1)
    {
        return args[0];
    }

    return Environment.GetEnvironmentVariable("SERVICE_NAME");
}
