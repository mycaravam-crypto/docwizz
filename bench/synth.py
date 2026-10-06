#!/usr/bin/env python3
"""Writes a synthetic, deterministic repository for benchmarks: `synth.py <out-dir> <modules>`.

Each module is a vertical slice in the shape DocWizz is built for: an ASP.NET controller → service (+ interface) →
repository → a shared DbContext, an entity, a Vue component and TS API client calling the endpoints, and every fourth
module also a Spring slice and SQL procedures. Modules reference their neighbours, so the graph grows with cross-module
calls rather than as isolated islands. Same input, same files: results are comparable across runs and machines.
"""
import os
import sys


def write(root, path, text):
    full = os.path.join(root, path)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    with open(full, "w") as f:
        f.write(text)


def csharp(root, i, n):
    m, nxt = f"Module{i}", f"Module{(i + 1) % n}"
    write(root, f"backend/Domain/{m}Item.cs", f"""namespace Bench.Domain;

/// <summary>An item of {m}.</summary>
public class {m}Item
{{
    public int Id {{ get; set; }}
    public string Name {{ get; set; }} = "";
    public int Quantity {{ get; set; }}
}}
""")
    write(root, f"backend/Application/I{m}Service.cs", f"""using Bench.Domain;

namespace Bench.Application;

/// <summary>Use cases of {m}.</summary>
public interface I{m}Service
{{
    /// <summary>Finds an item.</summary>
    /// <param name="id">Item id.</param>
    Task<{m}Item?> FindAsync(int id);
    Task<int> CreateAsync(string name, int quantity);
    Task<int> CountAsync();
}}
""")
    write(root, f"backend/Application/{m}Service.cs", f"""using Bench.Domain;

namespace Bench.Application;

public class {m}Service(I{m}Repository repository, I{nxt}Service next) : I{m}Service
{{
    public Task<{m}Item?> FindAsync(int id) => repository.FindAsync(id);

    public async Task<int> CreateAsync(string name, int quantity)
    {{
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name");
        if (quantity < 0 || quantity > 10000) throw new ArgumentException("quantity");
        var priority = quantity > 100 ? 1 : quantity > 10 ? 2 : 3;
        if (priority == 1 && await next.CountAsync() > 0) priority++;
        return await repository.AddAsync(new {m}Item {{ Name = name, Quantity = quantity }});
    }}

    public async Task<int> CountAsync() => (await repository.AllAsync()).Count;
}}

public interface I{m}Repository
{{
    Task<{m}Item?> FindAsync(int id);
    Task<int> AddAsync({m}Item item);
    Task<List<{m}Item>> AllAsync();
}}
""")
    write(root, f"backend/Infrastructure/{m}Repository.cs", f"""using Bench.Application;
using Bench.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bench.Infrastructure;

public class {m}Repository(BenchDb db) : I{m}Repository
{{
    public async Task<{m}Item?> FindAsync(int id) => await db.{m}Items.FindAsync(id);

    public async Task<int> AddAsync({m}Item item)
    {{
        db.{m}Items.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }}

    public Task<List<{m}Item>> AllAsync() => db.{m}Items.ToListAsync();
}}
""")
    write(root, f"backend/Api/{m}Controller.cs", f"""using Bench.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Bench.Api;

/// <summary>HTTP API of {m}.</summary>
[ApiController]
[Route("api/{m.lower()}")]
public class {m}Controller(I{m}Service service) : ControllerBase
{{
    /// <summary>One item.</summary>
    /// <param name="id">Item id.</param>
    [HttpGet("{{id}}")]
    public async Task<IActionResult> Get(int id) => await service.FindAsync(id) is {{ }} item ? Ok(item) : NotFound();

    [Authorize]
    [HttpPost]
    public async Task<ActionResult<int>> Create([FromQuery] string name, [FromQuery] int quantity) =>
        Ok(await service.CreateAsync(name, quantity));

    [HttpGet("count")]
    public Task<int> Count() => service.CountAsync();
}}
""")
    write(root, f"tests/{m}ServiceTests.cs", f"""using Bench.Application;

namespace Bench.Tests;

public class {m}ServiceTests
{{
    public async Task Creates({m}Service service) => await service.CreateAsync("a", 1);
}}
""")


def shared(root, n):
    sets = "\n".join(f"    public DbSet<Bench.Domain.Module{i}Item> Module{i}Items => Set<Bench.Domain.Module{i}Item>();" for i in range(n))
    write(root, "backend/Infrastructure/BenchDb.cs", f"""using Microsoft.EntityFrameworkCore;

namespace Bench.Infrastructure;

/// <summary>The one database.</summary>
public class BenchDb(DbContextOptions<BenchDb> options) : DbContext(options)
{{
{sets}
}}
""")
    regs = "\n".join(f"builder.Services.AddScoped<Bench.Application.IModule{i}Service, Bench.Application.Module{i}Service>();\n"
                     f"builder.Services.AddScoped<Bench.Application.IModule{i}Repository, Bench.Infrastructure.Module{i}Repository>();" for i in range(n))
    write(root, "backend/Program.cs", f"""var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<Bench.Infrastructure.BenchDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Bench")));
{regs}
var app = builder.Build();
app.MapControllers();
app.Run();
""")
    write(root, "backend/Bench.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="9.0.0" /></ItemGroup>
</Project>
""")
    write(root, "backend/appsettings.json", '{ "ConnectionStrings": { "Bench": "" } }\n')
    write(root, "frontend/package.json", '{ "name": "bench", "dependencies": { "vue": "^3.5.0", "pinia": "^3.0.0" } }\n')


def frontend(root, i):
    m = f"Module{i}"
    write(root, f"frontend/src/api/{m.lower()}.ts", f"""/** Client for the {m} API. */
export async function get{m}(id: number) {{
  const res = await fetch(`/api/{m.lower()}/${{id}}`)
  return res.json()
}}

export async function count{m}(): Promise<number> {{
  const res = await fetch('/api/{m.lower()}/count')
  return res.json()
}}
""")
    write(root, f"frontend/src/components/{m}View.vue", f"""<!-- Shows one {m} item. -->
<script setup lang="ts">
import {{ ref, onMounted }} from 'vue'
import {{ get{m} }} from '../api/{m.lower()}'

const props = defineProps<{{ id: number }}>()
const emit = defineEmits<{{ (e: 'loaded', name: string): void }}>()
const item = ref<any>(null)
onMounted(async () => {{
  item.value = await get{m}(props.id)
  emit('loaded', item.value.name)
}})
</script>

<template>
  <div v-if="item">{{{{ item.name }}}}</div>
</template>
""")


def java(root, i):
    m = f"Module{i}"
    pkg = f"com.bench.{m.lower()}"
    base = f"java/src/main/java/com/bench/{m.lower()}"
    write(root, f"{base}/{m}Repository.java", f"""package {pkg};

import org.springframework.data.jpa.repository.JpaRepository;

public interface {m}Repository extends JpaRepository<{m}Entity, Long> {{ }}
""")
    write(root, f"{base}/{m}Entity.java", f"""package {pkg};

import jakarta.persistence.Entity;
import jakarta.persistence.Id;

@Entity
public class {m}Entity {{
    @Id
    private Long id;
    private String name;
}}
""")
    write(root, f"{base}/{m}JavaService.java", f"""package {pkg};

import org.springframework.stereotype.Service;

/** Use cases of {m}. */
@Service
public class {m}JavaService {{
    private final {m}Repository repository;

    public {m}JavaService({m}Repository repository) {{
        this.repository = repository;
    }}

    public long count() {{
        return repository.count();
    }}
}}
""")
    write(root, f"{base}/{m}RestController.java", f"""package {pkg};

import org.springframework.web.bind.annotation.*;

@RestController
@RequestMapping("/java/{m.lower()}")
public class {m}RestController {{
    private final {m}JavaService service;

    public {m}RestController({m}JavaService service) {{
        this.service = service;
    }}

    /** How many there are. */
    @GetMapping("/count")
    public long count() {{
        return service.count();
    }}
}}
""")


def sql(root, i):
    m = f"module{i}"
    write(root, f"db/migrations/V{i + 1}__{m}.sql", f"CREATE TABLE dbo.{m}_items (id int PRIMARY KEY, name nvarchar(100), quantity int);\n")
    write(root, f"db/procedures/{m}.sql", f"""-- Items of {m} below a threshold.
CREATE PROCEDURE dbo.{m}_low @threshold int AS
    SELECT id, name FROM dbo.{m}_items WHERE quantity < @threshold
GO
""")


def main():
    out, n = sys.argv[1], int(sys.argv[2])
    shared(out, n)
    for i in range(n):
        csharp(out, i, n)
        frontend(out, i)
        if i % 4 == 0:
            java(out, i)
            sql(out, i)


if __name__ == "__main__":
    main()
