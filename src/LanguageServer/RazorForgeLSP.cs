using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Server;
using Compilers.Shared.Analysis;
using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.RazorForge.Parser;
using Compilers.RazorForge.Lexer;

namespace RazorForge.LanguageServer;

/// <summary>
/// RazorForge Language Server Protocol implementation.
/// Provides comprehensive IDE integration for the RazorForge programming language
/// including real-time error checking, semantic analysis, and document synchronization.
///
/// This server implements the Language Server Protocol (LSP) specification,
/// enabling RazorForge support in any LSP-compatible editor or IDE.
///
/// Key features:
/// - Real-time syntax and semantic error reporting
/// - Document lifecycle management (open, change, save, close)
/// - Integration with RazorForge compiler pipeline
/// - Support for .rf file extension
/// </summary>
public class RazorForgeLSP
{
    /// <summary>
    /// Logger for capturing LSP server events and diagnostics.
    /// </summary>
    private readonly ILogger<RazorForgeLSP> _logger;

    /// <summary>
    /// The underlying OmniSharp Language Server instance.
    /// Handles LSP protocol communication with clients.
    /// </summary>
    private OmniSharp.Extensions.LanguageServer.Server.LanguageServer? _server;

    /// <summary>
    /// Initializes a new instance of the RazorForgeLSP class.
    /// </summary>
    /// <param name="logger">Logger for capturing server events and diagnostics</param>
    public RazorForgeLSP(ILogger<RazorForgeLSP> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Starts the Language Server and begins listening for client connections.
    /// Sets up the LSP server with RazorForge-specific handlers and services.
    ///
    /// The server will:
    /// 1. Configure logging and protocol handling
    /// 2. Register RazorForge compiler services
    /// 3. Set up document synchronization handlers
    /// 4. Begin listening on stdin/stdout for LSP communication
    /// </summary>
    /// <param name="args">Command line arguments (currently unused)</param>
    /// <returns>Exit code: 0 for success, 1 for failure</returns>
    public async Task<int> StartAsync(string[] args)
    {
        var logFile = Path.Combine(Path.GetTempPath(), "razorforge-lsp.log");

        try
        {
            // Write to both stderr and a log file
            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] RazorForge LSP: Starting...\n");
            Console.Error.WriteLine("RazorForge LSP: Starting...");

            _logger.LogInformation(message: "Starting RazorForge Language Server...");

            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] Creating server configuration...\n");
            Console.Error.WriteLine("RazorForge LSP: Creating server configuration...");

            _server = await OmniSharp.Extensions.LanguageServer.Server.LanguageServer.From(
                optionsAction: options => options.WithInput(input: Console.OpenStandardInput())
                                                 .WithOutput(output: Console.OpenStandardOutput())
                                                 .ConfigureLogging(builderAction: x => x
                                                     .AddLanguageProtocolLogging()
                                                     .SetMinimumLevel(level: LogLevel.Debug))
                                                 .WithServices(servicesAction: ConfigureServices)
                                                 .WithHandler<Handlers.TextDocumentSyncHandler>()
                                                 .WithHandler<Handlers.CompletionHandler>()
                                                 .WithHandler<Handlers.HoverHandler>()
                                                 .OnInitialize(@delegate: OnInitialize)
                                                  // .OnInitialized(OnInitialized)
                                                 .OnStarted(@delegate: OnStarted));

            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] Server created, waiting for exit...\n");
            Console.Error.WriteLine("RazorForge LSP: Server created, waiting for exit...");
            await _server.WaitForExit;

            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] Server exited normally\n");
            Console.Error.WriteLine("RazorForge LSP: Server exited normally");
            return 0;
        }
        catch (Exception ex)
        {
            var errorMsg = $"[{DateTime.Now:HH:mm:ss}] FATAL ERROR - {ex.GetType().Name}: {ex.Message}\nStack: {ex.StackTrace}\n";
            if (ex.InnerException != null)
            {
                errorMsg += $"Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}\n";
            }

            File.AppendAllText(logFile, errorMsg);
            Console.Error.WriteLine($"RazorForge LSP: FATAL ERROR - {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine($"Stack trace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Console.Error.WriteLine($"Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }

            _logger.LogError(exception: ex, message: "Language Server failed to start");
            return 1;
        }
    }

    /// <summary>
    /// Configures dependency injection services for the Language Server.
    /// Registers RazorForge-specific compiler services and document management.
    ///
    /// Services registered:
    /// - SemanticAnalyzer: For analyzing RazorForge code semantics
    /// - IRazorForgeCompilerService: Core compiler integration
    /// - DocumentManager: Document lifecycle and state management
    /// - DiagnosticsPublisher: For publishing diagnostics to the client
    /// </summary>
    /// <param name="services">Service collection to configure</param>
    private void ConfigureServices(IServiceCollection services)
    {
        try
        {
            Console.Error.WriteLine("RazorForge LSP: Configuring services...");

            // Register RazorForge compiler services
            Console.Error.WriteLine("RazorForge LSP: Registering SemanticAnalyzer...");
            services.AddSingleton<SemanticAnalyzer>(implementationFactory: provider =>
                new SemanticAnalyzer(language: Language.RazorForge, mode: LanguageMode.Normal));

            Console.Error.WriteLine("RazorForge LSP: Registering IRazorForgeCompilerService...");
            services.AddSingleton<IRazorForgeCompilerService, RazorForgeCompilerService>();

            Console.Error.WriteLine("RazorForge LSP: Registering DocumentManager...");
            services.AddSingleton<DocumentManager>();

            Console.Error.WriteLine("RazorForge LSP: Registering DiagnosticsPublisher...");
            services.AddSingleton<Analysis.DiagnosticsPublisher>();

            Console.Error.WriteLine("RazorForge LSP: Services configured successfully");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RazorForge LSP: ERROR in ConfigureServices - {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Handles the LSP initialize request from a client.
    /// Called when a client first connects to establish capabilities.
    /// </summary>
    /// <param name="server">The language server instance</param>
    /// <param name="request">Initialize request parameters from client</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>Completed task</returns>
    private Task OnInitialize(ILanguageServer server, InitializeParams request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(message: "Language Server initializing...");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles the LSP initialized notification from a client.
    /// Called after successful initialization to confirm readiness.
    /// </summary>
    /// <param name="server">The language server instance</param>
    /// <param name="request">Initialized notification parameters</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>Completed task</returns>
    private Task OnInitialized(ILanguageServer server, InitializedParams request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(message: "Language Server initialized successfully");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles the server started event.
    /// Called when the server is fully operational and ready for requests.
    /// </summary>
    /// <param name="server">The language server instance</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>Completed task</returns>
    private Task OnStarted(ILanguageServer server, CancellationToken cancellationToken)
    {
        _logger.LogInformation(message: "Language Server started and ready for connections");

        // Initialize DiagnosticsPublisher with server reference
        var diagnosticsPublisher = server.Services.GetService(typeof(Analysis.DiagnosticsPublisher))
            as Analysis.DiagnosticsPublisher;
        diagnosticsPublisher?.SetServer(server);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the Language Server gracefully.
    /// Shuts down the server and cleans up resources.
    /// Should be called when the server process is terminating.
    /// </summary>
    /// <returns>Task representing the shutdown operation</returns>
    public async Task StopAsync()
    {
        if (_server != null)
        {
            _logger.LogInformation(message: "Stopping RazorForge Language Server...");
            _server.Dispose();
        }
    }
}
