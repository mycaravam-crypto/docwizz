using System.Diagnostics;

namespace DocWizz.Tests.Journey;

// Runs the built docwizz as a separate process, as a user would: exit code, stdout and stderr kept apart. The working
// directory is a fresh scratch directory, so the repository's own docwizz.yaml (the self-scan config) never applies.
static class Docwizz
{
    public record Result(int Exit, string Out, string Err)
    {
        public override string ToString() => $"exit {Exit}\n--- stdout\n{Out}\n--- stderr\n{Err}";
    }

    static readonly string Dll = typeof(Cli).Assembly.Location;

    // Absolute, so a test can put fake tools first on PATH without shadowing the runtime that runs docwizz.
    public static readonly string Dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host
        : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")).FirstOrDefault(File.Exists) ?? "dotnet";

    public static Result Run(params string[] args) => Run(null, null, args);

    public static Result Run(string? cwd, IDictionary<string, string?>? env, params string[] args)
    {
        var scratch = cwd is null ? Directory.CreateTempSubdirectory("docwizz-cwd-").FullName : null;
        var psi = new ProcessStartInfo(Dotnet, [Dll, .. args])
        {
            WorkingDirectory = cwd ?? scratch!, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.Environment.Remove("DOCWIZZ_DEBUG");   // errors as a user sees them, not as a developer does
        psi.Environment.Remove("OLLAMA_HOST");
        foreach (var (k, v) in env ?? new Dictionary<string, string?>())
            if (v is null) psi.Environment.Remove(k); else psi.Environment[k] = v;
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromMinutes(3)))
            {
                p.Kill(entireProcessTree: true);
                throw new TimeoutException($"docwizz {string.Join(' ', args)} did not finish");
            }
            return new(p.ExitCode, stdout.Result, stderr.Result);
        }
        finally { if (scratch is not null) Directory.Delete(scratch, recursive: true); }
    }

    // What a user must never see: a crash dump instead of a message.
    public static void NoStackTrace(Result r)
    {
        Assert.DoesNotContain("Unhandled exception", r.Err);
        Assert.DoesNotMatch(@"(?m)^\s+at \S+\(", r.Out + r.Err);
    }

    // Vue/TS scanning needs scanner-vue's node_modules (npm ci --prefix scanner-vue). In CI it is required; elsewhere a
    // test that needs it is skipped with the reason.
    public static void RequireFrontendScanner()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "scanner-vue", "node_modules"))) return;
        if (Environment.GetEnvironmentVariable("CI") is { Length: > 0 }) Assert.Fail("scanner-vue is not installed: npm ci --prefix scanner-vue");
        Assert.Skip("scanner-vue is not installed (npm ci --prefix scanner-vue)");
    }

    // Every file under `dir` (relative path → content), to compare two runs.
    public static SortedDictionary<string, string> Snapshot(string dir) => new(
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllText), StringComparer.Ordinal);
}

// Small repositories of each kind setup has to cope with, written to a scratch directory.
static class Repos
{
    const string WebProject = """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";

    static readonly (string, string)[] AspNetFiles =
    [
        ("src/Shop/Shop.csproj", WebProject),
        ("src/Shop/Api/OrdersController.cs", """
            using Microsoft.AspNetCore.Mvc;
            using Shop.Application;
            namespace Shop.Api;
            [ApiController, Route("api/orders")]
            public class OrdersController(IOrderService orders) : ControllerBase
            {
                [HttpGet("{id}")] public IActionResult Get(int id) => Ok(orders.Find(id));
            }
            """),
        ("src/Shop/Application/OrderService.cs", """
            using Shop.Domain;
            namespace Shop.Application;
            public interface IOrderService { Order? Find(int id); }
            public class OrderService : IOrderService { public Order? Find(int id) => null; }
            """),
        ("src/Shop/Domain/Order.cs", "namespace Shop.Domain;\npublic record Order(int Id, decimal Total);\n"),
        ("src/Shop/Program.cs", """
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddScoped<Shop.Application.IOrderService, Shop.Application.OrderService>();
            var app = builder.Build();
            app.MapControllers();
            app.Run();
            """),
        ("tests/Shop.Tests/OrderServiceTests.cs", "namespace Shop.Tests;\npublic class OrderServiceTests { public void Finds() { } }\n"),
    ];

    static readonly (string, string)[] VueFiles =
    [
        ("web/package.json", """{ "name": "web", "dependencies": { "vue": "^3.5.0", "pinia": "^3.0.0" } }"""),
        ("web/src/components/OrderList.vue", """
            <script setup lang="ts">
            import { useOrders } from '../stores/orders'
            const orders = useOrders()
            </script>
            <template><ul><li v-for="o in orders.items" :key="o.id">{{ o.total }}</li></ul></template>
            """),
        ("web/src/stores/orders.ts", """
            import { defineStore } from 'pinia'
            export const useOrders = defineStore('orders', { state: () => ({ items: [] as { id: number, total: number }[] }) })
            """),
    ];

    public static Sources AspNet() => new(AspNetFiles);

    public static Sources Java() => new(
        ("pom.xml", """
            <project><modelVersion>4.0.0</modelVersion><groupId>com.example</groupId><artifactId>inventory</artifactId><version>1.0</version>
              <parent><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-parent</artifactId><version>3.5.0</version></parent>
              <dependencies><dependency><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-web</artifactId></dependency></dependencies>
            </project>
            """),
        ("src/main/java/com/example/inventory/web/ItemController.java", """
            package com.example.inventory.web;
            import com.example.inventory.service.ItemService;
            import org.springframework.web.bind.annotation.*;
            @RestController @RequestMapping("/api/items")
            public class ItemController {
                private final ItemService items;
                public ItemController(ItemService items) { this.items = items; }
                @GetMapping("/{id}") public String get(@PathVariable Long id) { return items.name(id); }
            }
            """),
        ("src/main/java/com/example/inventory/service/ItemService.java", """
            package com.example.inventory.service;
            import org.springframework.stereotype.Service;
            @Service public class ItemService { public String name(Long id) { return "item"; } }
            """),
        ("src/test/java/com/example/inventory/ItemServiceTest.java", "package com.example.inventory;\nclass ItemServiceTest { void names() { } }\n"));

    public static Sources Frontend() => new(VueFiles);

    public static Sources Mixed() => new([.. AspNetFiles, .. VueFiles]);

    public static Sources ConsoleApp() => new(
        ("Tool.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""),
        ("Program.cs", "/// <summary>Prints a greeting.</summary>\nstatic class Program { static void Main() => System.Console.WriteLine(\"hi\"); }\n"));

    // Languages docwizz doesn't read next to one it does.
    public static Sources Partial() => new(
        ("tools/report.py", "def report():\n    return 42\n"),
        ("cmd/main.go", "package main\nfunc main() {}\n"),
        ("lib/Calc.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""),
        ("lib/Calc.cs", "namespace Calc;\npublic static class Adder { public static int Add(int a, int b) => a + b; }\n"));

    public static Sources Empty() => new(("README.md", "# nothing here yet\n"));

    // Code, but in no directory a default layer glob matches.
    public static Sources NoLayers() => new(
        ("Flat.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""),
        ("Billing.cs", "namespace Flat;\npublic class Billing { public decimal Total(decimal net) => net * 1.2m; }\n"),
        ("Invoice.cs", "namespace Flat;\npublic record Invoice(int Id, decimal Net);\n"));
}
