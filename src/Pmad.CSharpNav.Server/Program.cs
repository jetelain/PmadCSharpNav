using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pmad.CSharpNav.Application.Exceptions;
using Pmad.CSharpNav.Application.Ports;
using Pmad.CSharpNav.Application.Queries;
using Pmad.CSharpNav.Contracts;
using Pmad.CSharpNav.Infrastructure.Adapters;
using Pmad.CSharpNav.Infrastructure.Watcher;
using Pmad.CSharpNav.Infrastructure.Workspace;
using Pmad.CSharpNav.Server.Lifecycle;
using Pmad.CSharpNav.Server.Security;

RoslynLocator.EnsureRegistered();

var builder = WebApplication.CreateBuilder(args);

// Parse command line arguments
string? solutionPath = null;
var port = 0;
var timeoutMinutes = 30;
string? explicitToken = null;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--solution" or "-s" && i + 1 < args.Length)
    {
        solutionPath = args[++i];
    }
    else if (args[i] is "--port" or "-p" && i + 1 < args.Length && int.TryParse(args[++i], out var p))
    {
        port = p;
    }
    else if (args[i] is "--timeout-minutes" && i + 1 < args.Length && int.TryParse(args[++i], out var t))
    {
        timeoutMinutes = t;
    }
    else if (args[i] is "--token" && i + 1 < args.Length)
    {
        explicitToken = args[++i];
    }
}

if (string.IsNullOrWhiteSpace(solutionPath))
{
    // Search current directory upward for solution
    solutionPath = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.slnx").FirstOrDefault()
                   ?? Directory.GetFiles(Directory.GetCurrentDirectory(), "*.sln").FirstOrDefault()
                   ?? Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj").FirstOrDefault();

    if (solutionPath == null)
    {
        Console.Error.WriteLine("Error: No solution or project file specified and none found in the current directory.");
        return 1;
    }
}

solutionPath = Path.GetFullPath(solutionPath);

// Configure structured rolling file logging under user logs directory
var logFilePath = NavDiscovery.GetLogFilePath(solutionPath);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider(logFilePath));

// Configure Kestrel to bind to loopback on ephemeral or specified port
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Listen(System.Net.IPAddress.Loopback, port);
});

// Configure services
var securityOptions = new DaemonSecurityOptions(explicitToken);
builder.Services.AddSingleton(securityOptions);

var workspace = new RoslynSolutionWorkspace(solutionPath);
builder.Services.AddSingleton<ISolutionWorkspace>(workspace);
builder.Services.AddSingleton<RoslynReferenceFinder>();
builder.Services.AddSingleton<IReferenceFinder>(sp => sp.GetRequiredService<RoslynReferenceFinder>());
builder.Services.AddSingleton<IDefinitionFinder>(sp => sp.GetRequiredService<RoslynReferenceFinder>());
builder.Services.AddSingleton<IImplementationFinder>(sp => sp.GetRequiredService<RoslynReferenceFinder>());
builder.Services.AddSingleton<ITypeExplorer, RoslynTypeExplorer>();
builder.Services.AddSingleton<IApiSearchEngine, RoslynApiSearchEngine>();
builder.Services.AddSingleton<IAssemblyDecompiler, ILSpyDecompiler>();

builder.Services.AddTransient<FindReferencesHandler>();
builder.Services.AddTransient<FindDefinitionHandler>();
builder.Services.AddTransient<FindImplementationsHandler>();
builder.Services.AddTransient<SearchApiHandler>();
builder.Services.AddTransient<ExploreTypeHandler>();
builder.Services.AddTransient<DecompileMemberHandler>();
builder.Services.AddTransient<GetWorkspaceStatusHandler>();
builder.Services.AddTransient<SyncDocumentHandler>();

builder.Services.AddSingleton(sp => new IdleTimeoutService(
    sp.GetRequiredService<IHostApplicationLifetime>(),
    sp.GetRequiredService<ILogger<IdleTimeoutService>>(),
    TimeSpan.FromMinutes(timeoutMinutes)));
builder.Services.AddHostedService(sp => sp.GetRequiredService<IdleTimeoutService>());

var app = builder.Build();

// Security middleware
app.UseMiddleware<DaemonSecurityMiddleware>();

// Activity tracking middleware (only records authenticated activity)
app.Use(async (context, next) =>
{
    context.RequestServices.GetRequiredService<IdleTimeoutService>().RecordActivity();
    await next(context);
});

// Global exception handling & 30-second query timeout
app.Use(async (context, next) =>
{
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
    context.RequestAborted = timeoutCts.Token;

    try
    {
        await next(context);
    }
    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status408RequestTimeout;
        context.Response.ContentType = "application/json";
        var errResponse = new ErrorResponse(
            Error: "QUERY_TIMEOUT",
            Message: "The query exceeded the server timeout of 30 seconds.",
            Code: "QUERY_TIMEOUT");
        await context.Response.WriteAsync(NavJsonSerializer.Serialize(errResponse));
    }
    catch (NavException ex)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json";
        var errResponse = new ErrorResponse(
            Error: ex.Code,
            Message: ex.Message,
            Code: ex.Code,
            Candidates: ex.Candidates);
        await context.Response.WriteAsync(NavJsonSerializer.Serialize(errResponse));
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        var errResponse = new ErrorResponse(
            Error: "INTERNAL_ERROR",
            Message: ex.Message,
            Code: "INTERNAL_ERROR");
        await context.Response.WriteAsync(NavJsonSerializer.Serialize(errResponse));
    }
});

// Endpoints
app.MapPost("/api/references", async (FindReferencesRequest req, FindReferencesHandler handler, CancellationToken ct) =>
    Results.Json(await handler.HandleAsync(req, ct), NavJsonSerializer.Options));

app.MapPost("/api/definition", async (FindDefinitionRequest req, FindDefinitionHandler handler, CancellationToken ct) =>
    Results.Json(await handler.HandleAsync(req, ct), NavJsonSerializer.Options));

app.MapPost("/api/implementations", async (FindImplementationsRequest req, FindImplementationsHandler handler, CancellationToken ct) =>
    Results.Json(await handler.HandleAsync(req, ct), NavJsonSerializer.Options));

app.MapPost("/api/api-search", async (SearchApiRequest req, SearchApiHandler handler, CancellationToken ct) =>
    Results.Json(await handler.HandleAsync(req, ct), NavJsonSerializer.Options));

app.MapPost("/api/explore", async (ExploreTypeRequest req, ExploreTypeHandler handler, CancellationToken ct) =>
    Results.Json(await handler.HandleAsync(req, ct), NavJsonSerializer.Options));

app.MapPost("/api/decompile", async (DecompileRequest req, DecompileMemberHandler handler, CancellationToken ct) =>
    Results.Json(await handler.HandleAsync(req, ct), NavJsonSerializer.Options));

app.MapPost("/api/sync", async (SyncRequest req, SyncDocumentHandler handler, CancellationToken ct) =>
    Results.Json(await handler.HandleAsync(req, ct), NavJsonSerializer.Options));

app.MapGet("/api/health", (ISolutionWorkspace ws) =>
    Results.Json(new HealthResponse(
        Status: ws.State == "unhealthy" ? "unhealthy" : "healthy",
        State: ws.State,
        Version: NavConstants.CurrentContractVersion,
        SolutionPath: solutionPath), NavJsonSerializer.Options));

app.MapGet("/api/status", async (GetWorkspaceStatusHandler handler, CancellationToken ct) =>
    Results.Json(await handler.HandleAsync(ct), NavJsonSerializer.Options));

app.MapPost("/api/reload", async (ISolutionWorkspace ws, CancellationToken ct) =>
{
    await ws.ReloadSolutionAsync(ct);
    return Results.Ok();
});

app.MapPost("/api/shutdown", (IHostApplicationLifetime lifetime) =>
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(200);
        lifetime.StopApplication();
    });
    return Results.Ok(new { message = "Shutting down" });
});

// Lifecycle hooks: write discovery file on start, initialize workspace, cleanup on stop
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var watcher = new DebouncedFileWatcher(Path.GetDirectoryName(solutionPath)!, workspace);

lifetime.ApplicationStarted.Register(() =>
{
    var serverAddressesFeature = app.Urls;
    var boundUrl = serverAddressesFeature.FirstOrDefault() ?? $"http://127.0.0.1:{port}";
    var uri = new Uri(boundUrl);

    _ = Task.Run(async () =>
    {
        try
        {
            // Write discovery file immediately so CLI and health checks can connect while loading
            await NavDiscovery.WriteDiscoveryFileAsync(
                solutionPath,
                uri.Port,
                securityOptions.ExpectedToken);

            Console.WriteLine($"[csnav] Daemon started on port {uri.Port} for {solutionPath}");

            await workspace.InitializeAsync();
            watcher.Start();
            Console.WriteLine($"[csnav] Workspace initialized and file watcher active.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[csnav] Failed to initialize workspace: {ex.Message}");
        }
    });
});

lifetime.ApplicationStopping.Register(() =>
{
    watcher.Dispose();
    NavDiscovery.DeleteDiscoveryFile(solutionPath);
    workspace.Dispose();
    Console.WriteLine("[csnav] Daemon stopped.");
});

await app.RunAsync();
return 0;

public partial class Program { }

sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _lock = new();

    public FileLoggerProvider(string path) => _path = path;

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _path, _lock);

    public void Dispose() { }
}

sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly string _path;
    private readonly object _lock;

    public FileLogger(string category, string path, object fileLock)
    {
        _category = category;
        _path = path;
        _lock = fileLock;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        var logLine = $"[{timestamp}] [{logLevel}] [{_category}] {message}{(exception != null ? "\n" + exception : "")}\n";

        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var fileInfo = new FileInfo(_path);
                    if (fileInfo.Length > 10 * 1024 * 1024)
                    {
                        var oldPath = _path + ".old";
                        File.Move(_path, oldPath, overwrite: true);
                    }
                }
                File.AppendAllText(_path, logLine);
            }
            catch
            {
                // Best-effort file logging
            }
        }
    }
}
