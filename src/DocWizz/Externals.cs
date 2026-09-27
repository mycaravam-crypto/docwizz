// External systems: what the code talks to outside itself, as `external` nodes (`ext:<key>`) with `connects` edges.
// Tags: [category, certainty]. Certainty: detected (a call in the code shows it), inferred (only a package reference
// or a single candidate does), unknown (something is there, but not what). Never derived from names alone.
static class Externals
{
    // Calls: invoked method names ("UseSqlServer"), `Type.Method` prefixes ("File.WriteAll") or created type names ("SmtpClient").
    public record Known(string Key, string Name, string Category, string[] Packages, string[] Calls);

    // ponytail: a curated list; extend it when a project's dependencies go unnamed.
    public static readonly Known[] All =
    [
        new("sqlserver", "SQL Server", "database", ["Microsoft.EntityFrameworkCore.SqlServer", "Microsoft.Data.SqlClient", "System.Data.SqlClient"], ["UseSqlServer", "SqlConnection"]),
        new("postgresql", "PostgreSQL", "database", ["Npgsql.EntityFrameworkCore.PostgreSQL", "Npgsql"], ["UseNpgsql", "NpgsqlConnection", "NpgsqlDataSource.Create"]),
        new("sqlite", "SQLite", "database", ["Microsoft.EntityFrameworkCore.Sqlite", "Microsoft.Data.Sqlite"], ["UseSqlite", "SqliteConnection"]),
        new("mysql", "MySQL", "database", ["Pomelo.EntityFrameworkCore.MySql", "MySql.Data", "MySqlConnector"], ["UseMySql", "MySqlConnection"]),
        new("oracle", "Oracle", "database", ["Oracle.EntityFrameworkCore", "Oracle.ManagedDataAccess.Core"], ["UseOracle", "OracleConnection"]),
        new("cosmos", "Azure Cosmos DB", "database", ["Microsoft.EntityFrameworkCore.Cosmos", "Microsoft.Azure.Cosmos"], ["UseCosmos", "CosmosClient"]),
        new("mongodb", "MongoDB", "database", ["MongoDB.Driver"], ["MongoClient"]),
        new("redis", "Redis", "cache", ["StackExchange.Redis", "Microsoft.Extensions.Caching.StackExchangeRedis"], ["AddStackExchangeRedisCache", "ConnectionMultiplexer.Connect"]),
        new("rabbitmq", "RabbitMQ", "messaging", ["RabbitMQ.Client", "MassTransit.RabbitMQ"], ["UsingRabbitMq"]),
        new("kafka", "Kafka", "messaging", ["Confluent.Kafka"], ["ProducerBuilder", "ConsumerBuilder"]),
        new("servicebus", "Azure Service Bus", "messaging", ["Azure.Messaging.ServiceBus", "MassTransit.Azure.ServiceBus.Core"], ["ServiceBusClient", "UsingAzureServiceBus"]),
        new("smtp", "SMTP server", "email", ["MailKit"], ["SmtpClient"]),
        new("sendgrid", "SendGrid", "email", ["SendGrid"], ["SendGridClient"]),
        new("entra", "Microsoft Entra ID", "identity", ["Microsoft.Identity.Web", "@azure/msal-browser", "@azure/msal-node"], ["AddMicrosoftIdentityWebApi", "AddMicrosoftIdentityWebApp"]),
        new("keycloak", "Keycloak", "identity", ["keycloak-js", "Keycloak.AuthServices.Authentication"], []),
        new("oidc", "Identity provider (OpenID Connect / JWT)", "identity",
            ["Microsoft.AspNetCore.Authentication.JwtBearer", "Microsoft.AspNetCore.Authentication.OpenIdConnect", "oidc-client-ts"], ["AddJwtBearer", "AddOpenIdConnect"]),
        new("blob", "Azure Blob Storage", "storage", ["Azure.Storage.Blobs"], ["BlobServiceClient", "BlobContainerClient"]),
        new("s3", "Amazon S3", "storage", ["AWSSDK.S3"], ["AmazonS3Client"]),
        new("elasticsearch", "Elasticsearch", "search", ["Elastic.Clients.Elasticsearch", "NEST"], ["ElasticsearchClient", "ElasticClient"]),
        new("anthropic", "Anthropic API", "http-api", ["Anthropic"], ["AnthropicClient"]),
        new("openai", "OpenAI API", "http-api", ["OpenAI", "Azure.AI.OpenAI", "openai"], ["OpenAIClient", "AzureOpenAIClient"]),
        new("soap", "SOAP service", "http-api", ["System.ServiceModel.Http", "System.ServiceModel.Primitives"], []),
        new("filesystem", "File system", "storage", [], ["File.WriteAll", "File.ReadAll", "File.AppendAll", "File.Open", "File.Create", "File.Delete", "File.Copy", "File.Move",
            "File.ReadLines", "Directory.CreateDirectory", "Directory.Delete", "Directory.EnumerateFiles", "Directory.GetFiles"]),
    ];

    // Container images of well-known systems (repository name, without registry/tag) → Known key.
    static readonly (string Image, string Key)[] Images = [("mssql", "sqlserver"), ("azure-sql-edge", "sqlserver"), ("postgres", "postgresql"),
        ("postgis", "postgresql"), ("mysql", "mysql"), ("mariadb", "mysql"), ("mongo", "mongodb"), ("redis", "redis"), ("rabbitmq", "rabbitmq"),
        ("kafka", "kafka"), ("cp-kafka", "kafka"), ("elasticsearch", "elasticsearch"), ("keycloak", "keycloak"), ("azurite", "blob"),
        ("minio", "s3"), ("mailhog", "smtp"), ("mailpit", "smtp"), ("oracle", "oracle"), ("cosmosdb", "cosmos")];

    public static Known? ByImage(string image)
    {
        // Any path segment after the registry: mcr.microsoft.com/mssql/server → mssql, server.
        var segments = image.Split('@')[0].Split('/').Select(s => s.Split(':')[0]).Skip(image.Split('/')[0].Contains('.') ? 1 : 0).ToList();
        return Images.Where(i => segments.Any(s => s.StartsWith(i.Image))).Select(i => All.First(k => k.Key == i.Key)).FirstOrDefault();
    }

    // name: the invoked method or created type; qualified: "Receiver.Method" (last receiver segment) when there is one.
    public static Known? ByCall(string name, string? qualified = null) =>
        All.FirstOrDefault(k => k.Calls.Any(c => c.Contains('.') ? qualified?.StartsWith(c) == true : c == name));

    public static Node Node(string key, string name, string category, string certainty, string file, int line) =>
        new($"ext:{key}", "external", name, file, line, Tags: [category, certainty]);

    public static string Certainty(Node n) => n.Tags?.ElementAtOrDefault(1) ?? "unknown";
    public static string Category(Node n) => n.Tags?.FirstOrDefault() ?? "unknown";

    // After all scanners ran: absolute URLs called over HTTP, package-only evidence, and DbContexts without a known database.
    public static void Link(List<Node> nodes, List<Edge> edges)
    {
        var byId = nodes.ToDictionary(n => n.Id);
        void Add(Node n) { if (byId.TryAdd(n.Id, n)) nodes.Add(n); }

        foreach (var e in edges.Where(e => e.Kind == "http" && e.To.StartsWith("http:") && byId.ContainsKey(e.From)).ToList())
            if (e.To.Split(' ', 2) is [_, var url] && Uri.TryCreate(url.Replace("{}", "x"), UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
            {
                Add(Node($"http:{u.Host}", u.Host, "http-api", "detected", byId[e.From].File, byId[e.From].Line));
                edges.Add(new(e.From, $"ext:http:{u.Host}", "connects", "detected"));
            }

        foreach (var dep in edges.Where(e => e.Kind == "depends-on").ToList())
            if (byId.GetValueOrDefault(dep.To) is { } pkg && byId.GetValueOrDefault(dep.From) is { } project
                && All.FirstOrDefault(k => k.Packages.Contains(pkg.Name)) is { } k && !byId.ContainsKey($"ext:{k.Key}"))
            {
                Add(Node(k.Key, k.Name, k.Category, "inferred", project.File, 1));
                edges.Add(new(project.Id, $"ext:{k.Key}", "connects", "inferred"));
            }

        // Found outside any type (top-level statements): attribute it to the project the file belongs to.
        var projects = nodes.Where(n => n.Kind == "project").ToList();
        var connected = edges.Where(e => e.Kind == "connects").Select(e => e.To).ToHashSet();
        foreach (var ext in nodes.Where(n => n.Kind == "external" && !connected.Contains(n.Id)).ToList())
            if (global::Projects.Of(projects, ext.File) is { } p) edges.Add(new(p.Id, ext.Id, "connects", Certainty(ext)));

        var databases = nodes.Where(n => n.Kind == "external" && Category(n) == "database").ToList();
        var linked = edges.Where(e => e.Kind == "connects" && databases.Any(d => d.Id == e.To)).Select(e => e.From).ToHashSet();
        foreach (var ctx in nodes.Where(n => n.Tags?.Contains("dbcontext") == true && !linked.Contains(n.Id)).ToList())
        {
            if (databases.Count == 1) edges.Add(new(ctx.Id, databases[0].Id, "connects", "inferred"));
            else
            {
                Add(Node("database", "Database (type unknown)", "database", "unknown", ctx.File, ctx.Line));
                edges.Add(new(ctx.Id, "ext:database", "connects", "unknown"));
            }
        }
    }
}
