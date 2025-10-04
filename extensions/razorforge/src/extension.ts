import * as vscode from 'vscode';
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
    TransportKind,
    ExecutableOptions,
} from 'vscode-languageclient/node';

let client: LanguageClient;

/**
 * Activates the RazorForge language extension.
 * Sets up the Language Server Protocol client and starts communication
 * with the RazorForge LSP server for IDE integration features.
 */
export function activate(context: vscode.ExtensionContext) {
    // Get configuration for RazorForge language server
    const config = vscode.workspace.getConfiguration('razorforge');
    const serverPath = config.get<string>(
        'languageServer.path',
        'RazorForge.exe'
    );
    const serverArgs = config.get<string[]>('languageServer.args', ['--lsp']);

    console.log('RazorForge Language Support: Activating extension');

    // Configure the language server executable
    const serverOptions: ServerOptions = {
        command: serverPath,
        args: serverArgs,
        transport: TransportKind.stdio,
        options: {
            cwd: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath,
        } as ExecutableOptions,
    };

    // Configure client options for LSP communication
    const clientOptions: LanguageClientOptions = {
        // Register the server for RazorForge documents
        documentSelector: [
            {
                scheme: 'file',
                language: 'razorforge',
            },
        ],

        // Output channel for debugging LSP communication
        outputChannelName: 'RazorForge Language Server',

        // Trace LSP communication if configured
        traceOutputChannel: vscode.window.createOutputChannel(
            'RazorForge LSP Trace'
        ),
    };

    try {
        // Create and start the language client
        client = new LanguageClient(
            'razorforgeLanguageServer',
            'RazorForge Language Server',
            serverOptions,
            clientOptions
        );

        // Start the client and LSP server
        console.log('RazorForge Language Support: Starting Language Server...');
        client.start();

        // Register commands and providers
        registerCommands(context);

        // Register formatting and linting providers
        registerFormattingProviders(context);
        registerLintingFeatures(context);

        // Set up automatic file syncing
        setupAutoSync(context);

        // Set up cross-extension integration
        setupCrossExtensionIntegration(context);

        // Show activation message
        vscode.window.showInformationMessage(
            'RazorForge Language Support activated! LSP server starting...'
        );

        console.log(
            'RazorForge Language Support: Extension activated successfully'
        );
    } catch (error) {
        console.error(
            'RazorForge Language Support: Failed to start language server:',
            error
        );
        vscode.window.showErrorMessage(
            `Failed to start RazorForge Language Server: ${error}`
        );
    }
}

/**
 * Registers additional commands and providers for RazorForge development.
 */
function registerCommands(context: vscode.ExtensionContext) {
    // Command to restart the language server
    const restartCommand = vscode.commands.registerCommand(
        'razorforge.restartLanguageServer',
        async () => {
            try {
                vscode.window.showInformationMessage(
                    'Restarting RazorForge Language Server...'
                );

                if (client) {
                    await client.stop();
                    await client.start();
                }

                vscode.window.showInformationMessage(
                    'RazorForge Language Server restarted successfully'
                );
            } catch (error) {
                vscode.window.showErrorMessage(
                    `Failed to restart language server: ${error}`
                );
            }
        }
    );

    // Command to show server status
    const statusCommand = vscode.commands.registerCommand(
        'razorforge.showServerStatus',
        () => {
            const isRunning = client?.isRunning() ?? false;
            const status = isRunning ? 'Running' : 'Stopped';

            vscode.window.showInformationMessage(
                `RazorForge Language Server Status: ${status}`
            );
        }
    );

    // Command to create new RazorForge file
    const newFileCommand = vscode.commands.registerCommand(
        'razorforge.newFile',
        async () => {
            try {
                const fileName = await vscode.window.showInputBox({
                    prompt: 'Enter RazorForge file name',
                    placeHolder: 'example.rf',
                    validateInput: value => {
                        if (!value) {
                            return 'File name is required';
                        }
                        if (!value.endsWith('.rf')) {
                            return 'File must have .rf extension';
                        }
                        return null;
                    },
                });

                if (fileName) {
                    const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
                    if (workspaceFolder) {
                        const filePath = vscode.Uri.joinPath(workspaceFolder.uri, fileName);

                        // Create file with basic RazorForge template
                        const template = `// RazorForge file: ${fileName}
// Generated by VS Code RazorForge extension

recipe main() {
    // Your RazorForge code here
}
`;

                        await vscode.workspace.fs.writeFile(
                            filePath,
                            Buffer.from(template)
                        );
                        const document = await vscode.workspace.openTextDocument(filePath);
                        await vscode.window.showTextDocument(document);
                    }
                }
            } catch (error) {
                vscode.window.showErrorMessage(
                    `Failed to create RazorForge file: ${error}`
                );
            }
        }
    );

    // Command to create integrated project
    const createProjectCommand = vscode.commands.registerCommand(
        'razorforge.createIntegratedProject',
        createIntegratedProject
    );

    // Formatting commands
    const formatDocumentCommand = vscode.commands.registerCommand(
        'razorforge.formatDocument',
        () => formatRazorForgeDocument()
    );

    const formatSelectionCommand = vscode.commands.registerCommand(
        'razorforge.formatSelection',
        () => formatRazorForgeSelection()
    );

    // Linting commands
    const lintDocumentCommand = vscode.commands.registerCommand(
        'razorforge.lintDocument',
        () => lintRazorForgeDocument()
    );

    const fixAllProblemsCommand = vscode.commands.registerCommand(
        'razorforge.fixAllProblems',
        () => fixAllRazorForgeProblems()
    );

    const organizeImportsCommand = vscode.commands.registerCommand(
        'razorforge.organizeImports',
        () => organizeRazorForgeImports()
    );

    context.subscriptions.push(
        restartCommand,
        statusCommand,
        newFileCommand,
        createProjectCommand,
        formatDocumentCommand,
        formatSelectionCommand,
        lintDocumentCommand,
        fixAllProblemsCommand,
        organizeImportsCommand
    );
}

/**
 * Sets up automatic file syncing and change detection for RazorForge files.
 * Monitors file changes, saves, and workspace modifications to keep LSP in sync.
 */
function setupAutoSync(context: vscode.ExtensionContext) {
    // Auto-save when switching between files
    const onDidChangeActiveTextEditor = vscode.window.onDidChangeActiveTextEditor(
        editor => {
            if (editor?.document.languageId === 'razorforge') {
                if (editor.document.isDirty) {
                    editor.document.save();
                }
            }
        }
    );

    // Auto-sync on file save
    const onDidSaveDocument = vscode.workspace.onDidSaveTextDocument(document => {
        if (document.languageId === 'razorforge') {
            console.log(`RazorForge: Auto-synced ${document.fileName}`);

            // Notify about external dependencies if they exist
            const workspaceFolder = vscode.workspace.getWorkspaceFolder(document.uri);
            if (workspaceFolder) {
                checkForConfigChanges(workspaceFolder.uri);
            }
        }
    });

    // Watch for configuration file changes
    const configWatcher = vscode.workspace.createFileSystemWatcher(
        '**/razorforge.config.json'
    );

    configWatcher.onDidChange(_uri => {
        vscode.window.showInformationMessage(
            'RazorForge configuration changed. Restarting language server...'
        );
        restartLanguageServer();
    });

    configWatcher.onDidCreate(_uri => {
        vscode.window.showInformationMessage(
            'RazorForge configuration file created. Reloading...'
        );
        restartLanguageServer();
    });

    // Watch for project file changes
    const projectWatcher = vscode.workspace.createFileSystemWatcher(
        '**/*.{csproj,fsproj,vbproj}'
    );

    projectWatcher.onDidChange(() => {
        vscode.window.showInformationMessage(
            'Project file changed. RazorForge language server may need restart.'
        );
    });

    // Auto-format on save (if enabled)
    const onWillSaveDocument = vscode.workspace.onWillSaveTextDocument(event => {
        if (event.document.languageId === 'razorforge') {
            const config = vscode.workspace.getConfiguration('razorforge');
            const formatOnSave = config.get<boolean>('formatOnSave', false);

            if (formatOnSave) {
                const edit = vscode.commands.executeCommand(
                    'vscode.executeFormatDocumentProvider',
                    event.document.uri
                );

                if (edit) {
                    event.waitUntil(edit);
                }
            }
        }
    });

    context.subscriptions.push(
        onDidChangeActiveTextEditor,
        onDidSaveDocument,
        configWatcher,
        projectWatcher,
        onWillSaveDocument
    );
}

/**
 * Checks for configuration changes that might affect RazorForge compilation.
 */
async function checkForConfigChanges(workspaceUri: vscode.Uri) {
    try {
        const configFiles = await vscode.workspace.findFiles(
            new vscode.RelativePattern(workspaceUri, '**/razorforge.config.json')
        );

        const projectFiles = await vscode.workspace.findFiles(
            new vscode.RelativePattern(workspaceUri, '**/*.{csproj,fsproj}')
        );

        if (configFiles.length > 0 || projectFiles.length > 0) {
            console.log('RazorForge: Configuration files detected, ensuring sync...');
        }
    } catch (error) {
        console.error('RazorForge: Error checking config files:', error);
    }
}

/**
 * Restarts the language server connection.
 */
async function restartLanguageServer() {
    try {
        if (client) {
            await client.stop();
            await client.start();
        }
    } catch (error) {
        console.error('RazorForge: Failed to restart language server:', error);
    }
}

/**
 * Sets up automatic integration with Cake extension and shared workspace features.
 */
function setupCrossExtensionIntegration(context: vscode.ExtensionContext) {
    const config = vscode.workspace.getConfiguration('razorforge');
    const cakeIntegrationEnabled = config.get<boolean>('cakeIntegration', true);
    const sharedWorkspaceEnabled = config.get<boolean>('sharedWorkspace', true);

    if (!cakeIntegrationEnabled) {
        return;
    }

    // Check if Cake extension is installed
    const cakeExtension = vscode.extensions.getExtension(
        'razorforge.cake-language-support'
    );

    if (cakeExtension) {
        console.log('RazorForge: Cake extension detected, enabling integration...');

        // Set up shared configurations
        if (sharedWorkspaceEnabled) {
            setupSharedConfiguration();
        }

        // Notify user about integration
        vscode.window
            .showInformationMessage(
                'RazorForge & Cake integration activated! 🍰⚔️',
                'Show Settings'
            )
            .then(selection => {
                if (selection === 'Show Settings') {
                    vscode.commands.executeCommand(
                        'workbench.action.openSettings',
                        'razorforge'
                    );
                }
            });
    } else {
        // Offer to install Cake extension
        vscode.window
            .showInformationMessage(
                'Install Cake Language Support for complete RazorForge development experience?',
                'Install',
                'Not Now'
            )
            .then(selection => {
                if (selection === 'Install') {
                    vscode.commands.executeCommand(
                        'workbench.extensions.search',
                        'razorforge.cake-language-support'
                    );
                }
            });
    }

    // Watch for Cake extension installation
    const extensionChangeHandler = vscode.extensions.onDidChange(() => {
        const newCakeExtension = vscode.extensions.getExtension(
            'razorforge.cake-language-support'
        );
        if (newCakeExtension && !cakeExtension) {
            vscode.window.showInformationMessage(
                'Cake extension installed! Restart VS Code to enable full integration.'
            );
        }
    });

    context.subscriptions.push(extensionChangeHandler);
}

/**
 * Sets up shared configuration between RazorForge and Cake extensions.
 */
function setupSharedConfiguration() {
    // Create shared workspace settings
    const workspaceConfig = vscode.workspace.getConfiguration();

    // Share language server settings if they don't exist for Cake
    const razorForgeServerPath = workspaceConfig.get(
        'razorforge.languageServer.path'
    );
    const cakeServerPath = workspaceConfig.get('cake.languageServer.path');

    if (razorForgeServerPath && !cakeServerPath) {
        workspaceConfig.update(
            'cake.languageServer.path',
            razorForgeServerPath,
            vscode.ConfigurationTarget.Workspace
        );
    }

    // Sync formatting settings
    const razorForgeFormatOnSave = workspaceConfig.get('razorforge.formatOnSave');
    const cakeFormatOnSave = workspaceConfig.get('cake.formatOnSave');

    if (razorForgeFormatOnSave !== undefined && cakeFormatOnSave === undefined) {
        workspaceConfig.update(
            'cake.formatOnSave',
            razorForgeFormatOnSave,
            vscode.ConfigurationTarget.Workspace
        );
    }

    console.log(
        'RazorForge: Shared configuration synchronized with Cake extension'
    );
}

/**
 * Creates an integrated project structure for RazorForge + Cake development.
 */
async function createIntegratedProject() {
    const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
    if (!workspaceFolder) {
        vscode.window.showErrorMessage('No workspace folder found');
        return;
    }

    try {
        // Create project structure
        const srcFolder = vscode.Uri.joinPath(workspaceFolder.uri, 'src');
        const buildFolder = vscode.Uri.joinPath(workspaceFolder.uri, 'build');

        await vscode.workspace.fs.createDirectory(srcFolder);
        await vscode.workspace.fs.createDirectory(buildFolder);

        // Create sample RazorForge file
        const sampleRf = vscode.Uri.joinPath(srcFolder, 'main.rf');
        const rfContent = `// RazorForge main program
recipe main() {
    // Your RazorForge code here
    let message = "Hello from RazorForge!";
}`;
        await vscode.workspace.fs.writeFile(sampleRf, Buffer.from(rfContent));

        // Create sample Cake build script
        const buildScript = vscode.Uri.joinPath(buildFolder, 'build.cake');
        const cakeContent = `// Cake build script for RazorForge project
#tool "nuget:?package=RazorForge.Tools"

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");

Task("Clean")
    .Does(() => {
        CleanDirectory("./output");
    });

Task("Build")
    .IsDependentOn("Clean")
    .Does(() => {
        // Build RazorForge files
        RazorForgeCompile("./src/**/*.rf", new RazorForgeSettings {
            Configuration = configuration,
            OutputDirectory = "./output"
        });
    });

Task("Default")
    .IsDependentOn("Build");

RunTarget(target);`;
        await vscode.workspace.fs.writeFile(buildScript, Buffer.from(cakeContent));

        // Create workspace configuration
        const vscodePath = vscode.Uri.joinPath(workspaceFolder.uri, '.vscode');
        await vscode.workspace.fs.createDirectory(vscodePath);

        const settingsPath = vscode.Uri.joinPath(vscodePath, 'settings.json');
        const settings = {
            'razorforge.languageServer.path': 'RazorForge.exe',
            'cake.languageServer.path': 'RazorForge.exe',
            'razorforge.formatOnSave': true,
            'cake.formatOnSave': true,
            'files.associations': {
                '*.rf': 'razorforge',
                '*.cake': 'cake',
            },
        };
        await vscode.workspace.fs.writeFile(
            settingsPath,
            Buffer.from(JSON.stringify(settings, null, 2))
        );

        vscode.window
            .showInformationMessage(
                'Integrated RazorForge + Cake project created! 🍰⚔️',
                'Open Main File'
            )
            .then(selection => {
                if (selection === 'Open Main File') {
                    vscode.window.showTextDocument(sampleRf);
                }
            });
    } catch (error) {
        vscode.window.showErrorMessage(`Failed to create project: ${error}`);
    }
}

/**
 * Registers formatting providers for RazorForge language.
 */
function registerFormattingProviders(context: vscode.ExtensionContext) {
    // Document formatting provider
    const documentFormattingProvider =
        vscode.languages.registerDocumentFormattingEditProvider('razorforge', {
            provideDocumentFormattingEdits(
                document: vscode.TextDocument
            ): vscode.TextEdit[] {
                return formatRazorForgeCode(document, null);
            },
        });

    // Range formatting provider
    const rangeFormattingProvider =
        vscode.languages.registerDocumentRangeFormattingEditProvider('razorforge', {
            provideDocumentRangeFormattingEdits(
                document: vscode.TextDocument,
                range: vscode.Range
            ): vscode.TextEdit[] {
                return formatRazorForgeCode(document, range);
            },
        });

    // On-type formatting provider
    const onTypeFormattingProvider =
        vscode.languages.registerOnTypeFormattingEditProvider(
            'razorforge',
            {
                provideOnTypeFormattingEdits(
                    document: vscode.TextDocument,
                    position: vscode.Position,
                    ch: string
                ): vscode.TextEdit[] {
                    if (ch === '}' || ch === ';' || ch === '\n') {
                        const config = vscode.workspace.getConfiguration('razorforge');
                        if (config.get<boolean>('formatOnSave', true)) {
                            const _line = document.lineAt(position.line);
                            const range = new vscode.Range(
                                new vscode.Position(Math.max(0, position.line - 1), 0),
                                position
                            );
                            return formatRazorForgeCode(document, range);
                        }
                    }
                    return [];
                },
            },
            '}',
            ';',
            '\n'
        );

    context.subscriptions.push(
        documentFormattingProvider,
        rangeFormattingProvider,
        onTypeFormattingProvider
    );
}

/**
 * Registers linting and diagnostic features for RazorForge.
 */
function registerLintingFeatures(context: vscode.ExtensionContext) {
    const diagnosticsCollection =
        vscode.languages.createDiagnosticCollection('razorforge');
    context.subscriptions.push(diagnosticsCollection);

    // Code action provider for quick fixes
    const codeActionProvider = vscode.languages.registerCodeActionsProvider(
        'razorforge',
        {
            provideCodeActions(
                document: vscode.TextDocument,
                range: vscode.Range,
                context: vscode.CodeActionContext
            ): vscode.CodeAction[] {
                const actions: vscode.CodeAction[] = [];
                const config = vscode.workspace.getConfiguration('razorforge');

                // Auto-import actions
                if (config.get<boolean>('codeActions.autoImport', true)) {
                    const importAction = new vscode.CodeAction(
                        'Auto-import missing symbols',
                        vscode.CodeActionKind.QuickFix
                    );
                    importAction.command = {
                        command: 'razorforge.organizeImports',
                        title: 'Organize Imports',
                    };
                    actions.push(importAction);
                }

                // Format action
                const formatAction = new vscode.CodeAction(
                    'Format code',
                    vscode.CodeActionKind.SourceFixAll
                );
                formatAction.command = {
                    command: 'razorforge.formatDocument',
                    title: 'Format Document',
                };
                actions.push(formatAction);

                // Fix common issues
                for (const diagnostic of context.diagnostics) {
                    if (diagnostic.source === 'razorforge') {
                        actions.push(...createQuickFixActions(document, diagnostic));
                    }
                }

                return actions;
            },
        }
    );

    // Real-time linting on document changes
    const onDidChangeDocument = vscode.workspace.onDidChangeTextDocument(
        event => {
            if (event.document.languageId === 'razorforge') {
                const config = vscode.workspace.getConfiguration('razorforge');
                if (config.get<boolean>('linting.enabled', true)) {
                    // Debounce linting to avoid excessive calls
                    setTimeout(() => {
                        updateDiagnostics(event.document, diagnosticsCollection);
                    }, 500);
                }
            }
        }
    );

    context.subscriptions.push(codeActionProvider, onDidChangeDocument);
}

/**
 * Formats RazorForge code according to configured style rules.
 */
function formatRazorForgeCode(
    document: vscode.TextDocument,
    range: vscode.Range | null
): vscode.TextEdit[] {
    const config = vscode.workspace.getConfiguration('razorforge');
    const indentSize = config.get<number>('formatting.indentSize', 4);
    const bracesOnNewLine = config.get<boolean>(
        'formatting.bracesOnNewLine',
        false
    );
    const trimTrailingWhitespace = config.get<boolean>(
        'formatting.trimTrailingWhitespace',
        true
    );

    const edits: vscode.TextEdit[] = [];
    const startLine = range ? range.start.line : 0;
    const endLine = range ? range.end.line : document.lineCount - 1;

    let indentLevel = 0;

    for (let i = startLine; i <= endLine; i++) {
        const line = document.lineAt(i);
        const text = line.text;
        const trimmedText = text.trim();

        // Skip empty lines
        if (trimmedText === '') {
            if (trimTrailingWhitespace && text.length > 0) {
                edits.push(vscode.TextEdit.replace(line.range, ''));
            }
            continue;
        }

        // Adjust indent level based on content
        if (trimmedText.endsWith('{')) {
            const newIndent = ' '.repeat(indentLevel * indentSize);
            let formattedText = newIndent + trimmedText;

            if (bracesOnNewLine && trimmedText !== '{') {
                // Split opening brace to new line (Allman style)
                const withoutBrace = trimmedText.slice(0, -1).trim();
                formattedText = newIndent + withoutBrace + '\n' + newIndent + '{';
            }

            if (text !== formattedText) {
                edits.push(vscode.TextEdit.replace(line.range, formattedText));
            }
            indentLevel++;
        } else if (trimmedText === '}' || trimmedText.startsWith('}')) {
            indentLevel = Math.max(0, indentLevel - 1);
            const newIndent = ' '.repeat(indentLevel * indentSize);
            const formattedText = newIndent + trimmedText;

            if (text !== formattedText) {
                edits.push(vscode.TextEdit.replace(line.range, formattedText));
            }
        } else {
            // Regular line formatting
            const newIndent = ' '.repeat(indentLevel * indentSize);
            const formattedText = newIndent + trimmedText;

            if (text !== formattedText) {
                edits.push(vscode.TextEdit.replace(line.range, formattedText));
            }
        }
    }

    return edits;
}

/**
 * Updates diagnostic information for a RazorForge document.
 */
function updateDiagnostics(
    document: vscode.TextDocument,
    collection: vscode.DiagnosticCollection
) {
    const diagnostics: vscode.Diagnostic[] = [];
    const config = vscode.workspace.getConfiguration('razorforge');
    const minLevel = config.get<string>('linting.level', 'warning');

    // Basic RazorForge linting rules
    for (let i = 0; i < document.lineCount; i++) {
        const line = document.lineAt(i);
        const text = line.text;

        // Check for trailing whitespace
        if (config.get<boolean>('formatting.trimTrailingWhitespace', true)) {
            const trailingWhitespace = text.match(/\\s+$/);
            if (trailingWhitespace) {
                const start = new vscode.Position(
                    i,
                    text.length - trailingWhitespace[0].length
                );
                const end = new vscode.Position(i, text.length);

                const diagnostic = new vscode.Diagnostic(
                    new vscode.Range(start, end),
                    'Trailing whitespace',
                    vscode.DiagnosticSeverity.Hint
                );
                diagnostic.source = 'razorforge';
                diagnostic.code = 'trailing-whitespace';
                diagnostics.push(diagnostic);
            }
        }

        // Check for incorrect comment syntax (should use # not //)
        const trimmed = text.trim();
        if (trimmed.includes('//') && !trimmed.startsWith('#')) {
            const diagnostic = new vscode.Diagnostic(
                line.range,
                'RazorForge uses # for comments, not //',
                vscode.DiagnosticSeverity.Information
            );
            diagnostic.source = 'razorforge';
            diagnostic.code = 'wrong-comment-syntax';
            diagnostics.push(diagnostic);
        }

        // Check for unnecessary semicolons (RazorForge doesn't need them)
        if (trimmed.endsWith(';') && !trimmed.startsWith('#')) {
            const diagnostic = new vscode.Diagnostic(
                line.range,
                'RazorForge does not require semicolons',
                vscode.DiagnosticSeverity.Hint
            );
            diagnostic.source = 'razorforge';
            diagnostic.code = 'unnecessary-semicolon';
            diagnostics.push(diagnostic);
        }

        // Check for inconsistent indentation
        const leadingWhitespace = text.match(/^\\s*/);
        if (leadingWhitespace && leadingWhitespace[0].includes('\\t')) {
            const diagnostic = new vscode.Diagnostic(
                new vscode.Range(i, 0, i, leadingWhitespace[0].length),
                'Use spaces for indentation instead of tabs',
                vscode.DiagnosticSeverity.Information
            );
            diagnostic.source = 'razorforge';
            diagnostic.code = 'indent-style';
            diagnostics.push(diagnostic);
        }
    }

    // Filter diagnostics based on minimum level
    const filteredDiagnostics = diagnostics.filter(diagnostic => {
        const severity: vscode.DiagnosticSeverity =
            diagnostic.severity !== undefined
                ? diagnostic.severity
                : vscode.DiagnosticSeverity.Warning;
        switch (minLevel) {
            case 'error':
                return severity === vscode.DiagnosticSeverity.Error;
            case 'warning':
                return severity <= vscode.DiagnosticSeverity.Warning;
            case 'info':
                return severity <= vscode.DiagnosticSeverity.Information;
            case 'hint':
                return true;
            default:
                return true;
        }
    });

    collection.set(document.uri, filteredDiagnostics);
}

/**
 * Creates quick fix actions for common RazorForge issues.
 */
function createQuickFixActions(
    document: vscode.TextDocument,
    diagnostic: vscode.Diagnostic
): vscode.CodeAction[] {
    const actions: vscode.CodeAction[] = [];

    switch (diagnostic.code) {
        case 'trailing-whitespace': {
            const action = new vscode.CodeAction(
                'Remove trailing whitespace',
                vscode.CodeActionKind.QuickFix
            );
            action.edit = new vscode.WorkspaceEdit();
            action.edit.replace(document.uri, diagnostic.range, '');
            actions.push(action);
            break;
        }

        case 'missing-semicolon': {
            const action = new vscode.CodeAction(
                'Add semicolon',
                vscode.CodeActionKind.QuickFix
            );
            action.edit = new vscode.WorkspaceEdit();
            const position = new vscode.Position(
                diagnostic.range.end.line,
                diagnostic.range.end.character
            );
            action.edit.insert(document.uri, position, ';');
            actions.push(action);
            break;
        }

        case 'indent-style': {
            const action = new vscode.CodeAction(
                'Convert tabs to spaces',
                vscode.CodeActionKind.QuickFix
            );
            action.edit = new vscode.WorkspaceEdit();
            const _line = document.lineAt(diagnostic.range.start.line);
            const config = vscode.workspace.getConfiguration('razorforge');
            const indentSize = config.get<number>('formatting.indentSize', 4);
            const newText = _line.text.replace(/\\t/g, ' '.repeat(indentSize));
            action.edit.replace(document.uri, _line.range, newText);
            actions.push(action);
            break;
        }
    }

    return actions;
}

// Command implementations
async function formatRazorForgeDocument() {
    const editor = vscode.window.activeTextEditor;
    if (editor && editor.document.languageId === 'razorforge') {
        await vscode.commands.executeCommand('editor.action.formatDocument');
    }
}

async function formatRazorForgeSelection() {
    const editor = vscode.window.activeTextEditor;
    if (editor && editor.document.languageId === 'razorforge') {
        await vscode.commands.executeCommand('editor.action.formatSelection');
    }
}

async function lintRazorForgeDocument() {
    const editor = vscode.window.activeTextEditor;
    if (editor && editor.document.languageId === 'razorforge') {
        const collection =
            vscode.languages.createDiagnosticCollection('razorforge');
        updateDiagnostics(editor.document, collection);
        vscode.window.showInformationMessage('Document linted successfully');
    }
}

async function fixAllRazorForgeProblems() {
    const editor = vscode.window.activeTextEditor;
    if (editor && editor.document.languageId === 'razorforge') {
        await vscode.commands.executeCommand('editor.action.fixAll');
    }
}

async function organizeRazorForgeImports() {
    const editor = vscode.window.activeTextEditor;
    if (editor && editor.document.languageId === 'razorforge') {
        // Basic import organization (move imports to top)
        const document = editor.document;
        const edit = new vscode.WorkspaceEdit();

        const imports: string[] = [];
        const otherLines: string[] = [];

        for (let i = 0; i < document.lineCount; i++) {
            const line = document.lineAt(i).text;
            if (
                line.trim().startsWith('import ') ||
                line.trim().startsWith('using ')
            ) {
                imports.push(line);
            } else if (line.trim() !== '') {
                otherLines.push(line);
            }
        }

        if (imports.length > 0) {
            // Sort imports alphabetically
            imports.sort();

            const newContent = [...imports, '', ...otherLines].join('\n');

            const fullRange = new vscode.Range(
                document.positionAt(0),
                document.positionAt(document.getText().length)
            );

            edit.replace(document.uri, fullRange, newContent);
            await vscode.workspace.applyEdit(edit);

            vscode.window.showInformationMessage('Imports organized successfully');
        }
    }
}

/**
 * Deactivates the extension and stops the language server.
 */
export function deactivate(): Thenable<void> | undefined {
    console.log('RazorForge Language Support: Deactivating extension');

    if (!client) {
        return undefined;
    }

    return client.stop();
}
