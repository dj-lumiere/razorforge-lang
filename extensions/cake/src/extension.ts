import * as vscode from 'vscode';
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
    TransportKind
} from 'vscode-languageclient/node';

let client: LanguageClient;

export function activate(context: vscode.ExtensionContext) {
    console.log('Cake Language Support extension is now active!');

    // Language Server setup
    const config = vscode.workspace.getConfiguration('cake');
    const serverPath = config.get<string>('languageServer.path', 'RazorForge.exe');
    const serverArgs = config.get<string[]>('languageServer.args', ['--cake-lsp']);

    const serverOptions: ServerOptions = {
        command: serverPath,
        args: serverArgs,
        transport: TransportKind.stdio
    };

    const clientOptions: LanguageClientOptions = {
        documentSelector: [{ scheme: 'file', language: 'cake' }],
        synchronize: {
            fileEvents: vscode.workspace.createFileSystemWatcher('**/.cake')
        }
    };

    client = new LanguageClient(
        'cakeLanguageServer',
        'Cake Language Server',
        serverOptions,
        clientOptions
    );

    // Start the client
    client.start();

    // Register commands
    registerCommands(context);

    // Register formatting providers
    registerFormattingProviders(context);

    // Register linting features
    registerLintingFeatures(context);

    // Setup RazorForge integration
    setupRazorForgeIntegration(context);

    console.log('Cake Language Support activated successfully');
}

export function deactivate(): Thenable<void> | undefined {
    if (!client) {
        return undefined;
    }
    return client.stop();
}

function registerCommands(context: vscode.ExtensionContext) {
    // New Cake Script command
    const newScriptCommand = vscode.commands.registerCommand('cake.newScript', async () => {
        const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
        if (!workspaceFolder) {
            vscode.window.showErrorMessage('No workspace folder found');
            return;
        }

        const fileName = await vscode.window.showInputBox({
            prompt: 'Enter the name for the new Cake script',
            placeHolder: 'build.cake'
        });

        if (fileName) {
            const filePath = vscode.Uri.joinPath(workspaceFolder.uri, fileName.endsWith('.cake') ? fileName : `${fileName}.cake`);
            const template = getCakeTemplate();

            await vscode.workspace.fs.writeFile(filePath, Buffer.from(template, 'utf8'));
            const document = await vscode.workspace.openTextDocument(filePath);
            await vscode.window.showTextDocument(document);
        }
    });

    // Restart Language Server command
    const restartCommand = vscode.commands.registerCommand('cake.restartLanguageServer', async () => {
        if (client) {
            await client.stop();
            await client.start();
            vscode.window.showInformationMessage('Cake Language Server restarted');
        }
    });

    // Show Server Status command
    const statusCommand = vscode.commands.registerCommand('cake.showServerStatus', () => {
        const state = client?.state;
        const stateText = state === 1 ? 'Starting' : state === 2 ? 'Running' : state === 3 ? 'Stopped' : 'Unknown';
        vscode.window.showInformationMessage(`Cake Language Server Status: ${stateText}`);
    });

    // Format Document command
    const formatCommand = vscode.commands.registerCommand('cake.formatDocument', async () => {
        const editor = vscode.window.activeTextEditor;
        if (editor && editor.document.languageId === 'cake') {
            await vscode.commands.executeCommand('editor.action.formatDocument');
        }
    });

    // Lint Document command
    const lintCommand = vscode.commands.registerCommand('cake.lintDocument', async () => {
        const editor = vscode.window.activeTextEditor;
        if (editor && editor.document.languageId === 'cake') {
            await lintCakeDocument(editor.document);
            vscode.window.showInformationMessage('Cake document linted');
        }
    });

    // Sync with RazorForge command
    const syncCommand = vscode.commands.registerCommand('cake.syncWithRazorForge', async () => {
        await syncWithRazorForge();
        vscode.window.showInformationMessage('Synced with RazorForge extension');
    });

    context.subscriptions.push(
        newScriptCommand,
        restartCommand,
        statusCommand,
        formatCommand,
        lintCommand,
        syncCommand
    );
}

function registerFormattingProviders(context: vscode.ExtensionContext) {
    // Document formatting provider
    const documentFormattingProvider = vscode.languages.registerDocumentFormattingEditProvider(
        'cake',
        {
            provideDocumentFormattingEdits(document: vscode.TextDocument): vscode.TextEdit[] {
                return formatCakeCode(document, null);
            }
        }
    );

    // Range formatting provider
    const rangeFormattingProvider = vscode.languages.registerDocumentRangeFormattingEditProvider(
        'cake',
        {
            provideDocumentRangeFormattingEdits(document: vscode.TextDocument, range: vscode.Range): vscode.TextEdit[] {
                return formatCakeCode(document, range);
            }
        }
    );

    // On-type formatting provider
    const onTypeFormattingProvider = vscode.languages.registerOnTypeFormattingEditProvider(
        'cake',
        {
            provideOnTypeFormattingEdits(document: vscode.TextDocument, position: vscode.Position, ch: string): vscode.TextEdit[] {
                return formatOnType(document, position, ch);
            }
        },
        ';', '}', '\n'
    );

    context.subscriptions.push(
        documentFormattingProvider,
        rangeFormattingProvider,
        onTypeFormattingProvider
    );
}

function registerLintingFeatures(context: vscode.ExtensionContext) {
    const diagnosticCollection = vscode.languages.createDiagnosticCollection('cake');
    context.subscriptions.push(diagnosticCollection);

    // Lint on document open and change
    const lintOnChange = vscode.workspace.onDidChangeTextDocument(async (event) => {
        if (event.document.languageId === 'cake') {
            await lintCakeDocument(event.document, diagnosticCollection);
        }
    });

    const lintOnOpen = vscode.workspace.onDidOpenTextDocument(async (document) => {
        if (document.languageId === 'cake') {
            await lintCakeDocument(document, diagnosticCollection);
        }
    });

    context.subscriptions.push(lintOnChange, lintOnOpen);
}

function setupRazorForgeIntegration(context: vscode.ExtensionContext) {
    const config = vscode.workspace.getConfiguration('cake');
    if (!config.get<boolean>('razorforgeIntegration', true)) {
        return;
    }

    // Check if RazorForge extension is installed
    const razorforgeExtension = vscode.extensions.getExtension('razorforge.razorforge-language-support');

    if (razorforgeExtension) {
        console.log('RazorForge extension detected, enabling integration');

        // Setup file watcher for .rf files
        const rfWatcher = vscode.workspace.createFileSystemWatcher('**/*.rf');

        rfWatcher.onDidCreate(async (uri) => {
            await syncWorkspaceSettings();
        });

        rfWatcher.onDidChange(async (uri) => {
            await syncWorkspaceSettings();
        });

        context.subscriptions.push(rfWatcher);

        // Setup shared workspace configuration
        if (config.get<boolean>('sharedWorkspace', true)) {
            setupSharedWorkspace(context);
        }
    } else {
        console.log('RazorForge extension not found');

        // Show notification to install RazorForge extension
        vscode.window.showInformationMessage(
            'Install RazorForge extension for enhanced integration',
            'Install'
        ).then(selection => {
            if (selection === 'Install') {
                vscode.commands.executeCommand(
                    'workbench.extensions.installExtension',
                    'razorforge.razorforge-language-support'
                );
            }
        });
    }
}

function setupSharedWorkspace(context: vscode.ExtensionContext) {
    // Sync configuration changes between extensions
    const configWatcher = vscode.workspace.onDidChangeConfiguration(async (event) => {
        if (event.affectsConfiguration('cake') || event.affectsConfiguration('razorforge')) {
            await syncWorkspaceSettings();
        }
    });

    context.subscriptions.push(configWatcher);
}

async function syncWorkspaceSettings() {
    const cakeConfig = vscode.workspace.getConfiguration('cake');
    const razorforgeConfig = vscode.workspace.getConfiguration('razorforge');

    // Sync formatting settings
    const cakeIndentSize = cakeConfig.get<number>('formatting.indentSize');
    const razorforgeIndentSize = razorforgeConfig.get<number>('formatting.indentSize');

    if (cakeIndentSize && cakeIndentSize !== razorforgeIndentSize) {
        await razorforgeConfig.update('formatting.indentSize', cakeIndentSize, vscode.ConfigurationTarget.Workspace);
    }
}

async function syncWithRazorForge() {
    await syncWorkspaceSettings();

    // Trigger RazorForge extension sync if available
    try {
        await vscode.commands.executeCommand('razorforge.syncWithCake');
    } catch (error) {
        console.log('RazorForge sync command not available');
    }
}

function formatCakeCode(document: vscode.TextDocument, range: vscode.Range | null): vscode.TextEdit[] {
    const config = vscode.workspace.getConfiguration('cake');
    const indentSize = config.get<number>('formatting.indentSize', 4);
    const insertFinalNewline = config.get<boolean>('formatting.insertFinalNewline', true);

    const edits: vscode.TextEdit[] = [];
    const startLine = range ? range.start.line : 0;
    const endLine = range ? range.end.line : document.lineCount - 1;

    for (let i = startLine; i <= endLine; i++) {
        const line = document.lineAt(i);
        const text = line.text;

        // Basic indentation formatting
        const leadingWhitespace = text.match(/^\\s*/)?.[0] || '';
        const expectedIndent = calculateExpectedIndent(document, i) * indentSize;
        const expectedWhitespace = ' '.repeat(expectedIndent);

        if (leadingWhitespace !== expectedWhitespace && text.trim().length > 0) {
            const range = new vscode.Range(i, 0, i, leadingWhitespace.length);
            edits.push(vscode.TextEdit.replace(range, expectedWhitespace));
        }
    }

    // Add final newline if needed
    if (insertFinalNewline && !range) {
        const lastLine = document.lineAt(document.lineCount - 1);
        if (lastLine.text.trim().length > 0) {
            const range = new vscode.Range(document.lineCount - 1, lastLine.text.length, document.lineCount - 1, lastLine.text.length);
            edits.push(vscode.TextEdit.insert(range.start, '\n'));
        }
    }

    return edits;
}

function formatOnType(document: vscode.TextDocument, position: vscode.Position, ch: string): vscode.TextEdit[] {
    const edits: vscode.TextEdit[] = [];

    if (ch === ';') {
        // Format the current line after semicolon
        const line = document.lineAt(position.line);
        const formatted = formatCakeCode(document, line.range);
        edits.push(...formatted);
    } else if (ch === '}') {
        // Auto-dedent on closing brace
        const line = document.lineAt(position.line);
        const expectedIndent = calculateExpectedIndent(document, position.line);
        const config = vscode.workspace.getConfiguration('cake');
        const indentSize = config.get<number>('formatting.indentSize', 4);
        const expectedWhitespace = ' '.repeat(expectedIndent * indentSize);

        const leadingWhitespace = line.text.match(/^\\s*/)?.[0] || '';
        if (leadingWhitespace !== expectedWhitespace) {
            const range = new vscode.Range(position.line, 0, position.line, leadingWhitespace.length);
            edits.push(vscode.TextEdit.replace(range, expectedWhitespace));
        }
    }

    return edits;
}

function calculateExpectedIndent(document: vscode.TextDocument, lineNumber: number): number {
    let indent = 0;

    for (let i = 0; i < lineNumber; i++) {
        const line = document.lineAt(i).text.trim();
        if (line.endsWith('{')) {
            indent++;
        } else if (line.startsWith('}')) {
            indent = Math.max(0, indent - 1);
        }
    }

    const currentLine = document.lineAt(lineNumber).text.trim();
    if (currentLine.startsWith('}')) {
        indent = Math.max(0, indent - 1);
    }

    return indent;
}

async function lintCakeDocument(document: vscode.TextDocument, collection?: vscode.DiagnosticCollection) {
    if (!collection) return;

    const config = vscode.workspace.getConfiguration('cake');
    if (!config.get<boolean>('linting.enabled', true)) {
        return;
    }

    const diagnostics: vscode.Diagnostic[] = [];

    for (let i = 0; i < document.lineCount; i++) {
        const line = document.lineAt(i);
        const text = line.text;

        // Check for common Cake script issues

        // Missing using statements
        if (text.includes('Task(') && !document.getText().includes('using Cake.Core;')) {
            const diagnostic = new vscode.Diagnostic(
                line.range,
                'Consider adding "using Cake.Core;" for Task definitions',
                vscode.DiagnosticSeverity.Information
            );
            diagnostic.source = 'cake';
            diagnostic.code = 'missing-using';
            diagnostics.push(diagnostic);
        }

        // Deprecated RunTarget usage
        if (text.includes('RunTarget(')) {
            const diagnostic = new vscode.Diagnostic(
                line.range,
                'RunTarget is deprecated, use the Cake runner instead',
                vscode.DiagnosticSeverity.Warning
            );
            diagnostic.source = 'cake';
            diagnostic.code = 'deprecated-runtarget';
            diagnostics.push(diagnostic);
        }

        // Missing task descriptions
        if (text.includes('Task(') && !text.includes('.Description(')) {
            const diagnostic = new vscode.Diagnostic(
                line.range,
                'Consider adding a description to this task',
                vscode.DiagnosticSeverity.Hint
            );
            diagnostic.source = 'cake';
            diagnostic.code = 'missing-description';
            diagnostics.push(diagnostic);
        }

        // Check for incorrect comment syntax (should use # not //)
        const trimmed = text.trim();
        if (trimmed.includes('//') && !trimmed.startsWith('#')) {
            const diagnostic = new vscode.Diagnostic(
                line.range,
                'Cake uses # for comments, not //',
                vscode.DiagnosticSeverity.Information
            );
            diagnostic.source = 'cake';
            diagnostic.code = 'wrong-comment-syntax';
            diagnostics.push(diagnostic);
        }

        // Check for unnecessary semicolons (Cake uses Python-style syntax)
        if (trimmed.endsWith(';') && !trimmed.startsWith('#')) {
            const diagnostic = new vscode.Diagnostic(
                line.range,
                'Cake does not require semicolons (Python-style syntax)',
                vscode.DiagnosticSeverity.Hint
            );
            diagnostic.source = 'cake';
            diagnostic.code = 'unnecessary-semicolon';
            diagnostics.push(diagnostic);
        }
    }

    collection.set(document.uri, diagnostics);
}

function getCakeTemplate(): string {
    return `# Cake build script with Python-style syntax
# No semicolons needed, use # for comments

var target = Argument("target", "Default")

Task("Clean")
    .Description("Cleans the output directory")
    .Does(() => {
        CleanDirectory("./output")
    })

Task("Build")
    .Description("Builds the project")
    .IsDependentOn("Clean")
    .Does(() => {
        DotNetCoreBuild("./src/Project.csproj")
    })

Task("Test")
    .Description("Runs unit tests")
    .IsDependentOn("Build")
    .Does(() => {
        DotNetCoreTest("./tests/Project.Tests.csproj")
    })

Task("Default")
    .Description("Default task")
    .IsDependentOn("Test")

RunTarget(target)
`;
}