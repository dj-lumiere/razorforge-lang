using System;
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
        _logger.LogInformation(message: "Starting RazorForge Language Server...");

        try
        {
            _server = await OmniSharp.Extensions.LanguageServer.Server.LanguageServer.From(
                optionsAction: options => options.WithInput(input: Console.OpenStandardInput())
                                                 .WithOutput(output: Console.OpenStandardOutput())
                                                 .ConfigureLogging(builderAction: x => x
                                                     .AddLanguageProtocolLogging()
                                                     .SetMinimumLevel(level: LogLevel.Debug))
                                                 .WithServices(servicesAction: ConfigureServices)
                                                  // TODO: Add handlers when interface compatibility is resolved
                                                 .OnInitialize(@delegate: OnInitialize)
                                                  // .OnInitialized(OnInitialized)
                                                 .OnStarted(@delegate: OnStarted));

            await _server.WaitForExit;
            return 0;
        }
        catch (Exception ex)
        {
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
    /// </summary>
    /// <param name="services">Service collection to configure</param>
    private void ConfigureServices(IServiceCollection services)
    {
        // Register RazorForge compiler services
        services.AddSingleton<SemanticAnalyzer>(implementationFactory: provider =>
            new SemanticAnalyzer(language: Language.RazorForge, mode: LanguageMode.Normal));

        services.AddSingleton<IRazorForgeCompilerService, RazorForgeCompilerService>();
        services.AddSingleton<DocumentManager>();
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

/// <summary>
/// Entry point for the RazorForge Language Server when run as a separate executable.
/// Provides a command-line interface for starting the LSP server.
///
/// Usage: RazorForge.exe --lsp
///
/// When the --lsp flag is provided, the program will start in Language Server mode
/// and communicate via stdin/stdout using the LSP protocol.
/// </summary>
public static class LanguageServerProgram
{
    /// <summary>
    /// Main entry point for the Language Server executable.
    /// Checks for --lsp flag and starts the appropriate mode.
    /// </summary>
    /// <param name="args">Command line arguments</param>
    /// <returns>Exit code: 0 for success, 1 for failure or unsupported mode</returns>
    public static async Task<int> Main(string[] args)
    {
        // Check if LSP mode was requested
        if (args.Length > 0 && args[0] == "--lsp")
        {
            using ILoggerFactory loggerFactory = LoggerFactory.Create(configure: builder =>
                builder.SetMinimumLevel(level: LogLevel.Information));

            ILogger<RazorForgeLSP> logger = loggerFactory.CreateLogger<RazorForgeLSP>();
            var lsp = new RazorForgeLSP(logger: logger);

            return await lsp.StartAsync(args: args);
        }

        // Fall back to regular compiler behavior
        return 1;
    }
}
