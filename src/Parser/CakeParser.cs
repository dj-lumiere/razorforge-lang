using Compilers.Shared.AST;
using Compilers.Shared.Parser;
using Compilers.Shared.Lexer;

namespace Compilers.Cake.Parser;

/// <summary>
/// Parser for Cake language (indentation-based syntax)
/// Handles Python-like indentation with colons and blocks
/// </summary>
public class CakeParser : BaseParser
{
    private readonly Stack<int> _indentationStack = new();
    private int _currentIndentationLevel = 0;

    public CakeParser(List<Token> tokens) : base(tokens) 
    {
        _indentationStack.Push(0); // Base indentation level
    }

    public override Compilers.Shared.AST.Program Parse()
    {
        var declarations = new List<IAstNode>();
        
        while (!IsAtEnd)
        {
            try
            {
                // Skip newlines at top level
                if (Match(TokenType.Newline)) continue;
                
                // Handle dedent tokens (should not occur at top level, but be safe)
                if (Check(TokenType.Dedent))
                {
                    ProcessDedentTokens();
                    continue;
                }
                
                var decl = ParseDeclaration();
                if (decl != null)
                    declarations.Add(decl);
            }
            catch (ParseException ex)
            {
                Console.Error.WriteLine($"Parse error: {ex.Message}");
                Synchronize();
            }
        }
        
        return new Compilers.Shared.AST.Program(declarations, GetLocation());
    }

    private IAstNode? ParseDeclaration()
    {
        // Import declaration
        if (Match(TokenType.Import))
        {
            return ParseImportDeclaration();
        }
        
        // Redefinition
        if (Match(TokenType.Define))
        {
            return ParseRedefinitionDeclaration();
        }
        
        // Using declaration
        if (Match(TokenType.Using))
        {
            return ParseUsingDeclaration();
        }
        
        // Parse visibility modifier
        var visibility = ParseVisibilityModifier();
        
        // Variable declarations
        if (Match(TokenType.Var, TokenType.Let))
        {
            return ParseVariableDeclaration(visibility);
        }
        
        // Recipe (function) declaration - using 'recipe' keyword in Cake
        if (Match(TokenType.recipe)) return ParseRecipeDeclaration(visibility);
        
        // Entity/Record/Choice declarations
        if (Match(TokenType.Entity)) return ParseClassDeclaration(visibility);
        if (Match(TokenType.Record)) return ParseStructDeclaration(visibility);
        if (Match(TokenType.Choice)) return ParseChoiceDeclaration(visibility); // choice in Cake
        if (Match(TokenType.Chimera)) return ParseVariantDeclaration(visibility, VariantKind.Chimera);
        if (Match(TokenType.Variant)) return ParseVariantDeclaration(visibility, VariantKind.Variant);
        if (Match(TokenType.Mutant)) return ParseVariantDeclaration(visibility, VariantKind.Mutant);
        if (Match(TokenType.Protocol)) return ParseFeatureDeclaration(visibility);
        
        // Implementation blocks (Type follows Trait:)
        if (CheckImplementation())
        {
            return ParseImplementationDeclaration();
        }
        
        // If we parsed a visibility modifier but no declaration follows, reset position
        if (visibility != VisibilityModifier.Private)
        {
            // Go back to before the visibility modifier
            Position--;
        }
        
        // Otherwise parse as statement
        return ParseStatement();
    }

    private bool CheckImplementation()
    {
        // Look for pattern: Identifier follows Identifier:
        if (Check(TokenType.Identifier) || Check(TokenType.TypeIdentifier))
        {
            var saved = Position;
            Advance(); // Skip type name
            if (Match(TokenType.Follows))
            {
                Position = saved; // Reset position
                return true;
            }
            Position = saved; // Reset position
        }
        return false;
    }

    private Statement? ParseStatement()
    {
        // Handle dedent tokens
        if (Check(TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        
        // Skip newlines
        while (Match(TokenType.Newline)) { }
        
        if (IsAtEnd) return null;
        
        // Control flow
        if (Match(TokenType.If)) return ParseIfStatement();
        if (Match(TokenType.While)) return ParseWhileStatement();
        if (Match(TokenType.For)) return ParseForStatement();
        if (Match(TokenType.When)) return ParseWhenStatement();
        if (Match(TokenType.Return)) return ParseReturnStatement();
        if (Match(TokenType.Break)) return ParseBreakStatement();
        if (Match(TokenType.Continue)) return ParseContinueStatement();

        // Danger and mayhem blocks
        if (Match(TokenType.Danger)) return ParseDangerStatement();
        if (Match(TokenType.Mayhem)) return ParseMayhemStatement();
        
        // Variable declarations (can appear in statement context)
        if (Match(TokenType.Var, TokenType.Let))
        {
            var varDecl = ParseVariableDeclaration();
            return new ExpressionStatement(
                new IdentifierExpression($"var {varDecl.Name}", GetLocation()),
                GetLocation());
        }
        
        // Cake's display statement (equivalent to print/console.log)
        if (Check(TokenType.Identifier) && CurrentToken.Text == "display")
        {
            Advance();
            Consume(TokenType.LeftParen, "Expected '(' after 'display'");
            var expr = ParseExpression();
            Consume(TokenType.RightParen, "Expected ')' after display expression");
            Match(TokenType.Newline);
            
            // Convert to a function call expression
            var displayCall = new CallExpression(
                new IdentifierExpression("Console.WriteLine", GetLocation()),
                new List<Expression> { expr },
                GetLocation());
            
            return new ExpressionStatement(displayCall, GetLocation());
        }
        
        // Expression statement
        return ParseExpressionStatement();
    }

    private VariableDeclaration ParseVariableDeclaration(VisibilityModifier visibility = VisibilityModifier.Private)
    {
        var location = GetLocation(PeekToken(-1));
        var isMutable = PeekToken(-1).Type == TokenType.Var;
        
        var name = ConsumeIdentifier("Expected variable name");
        
        TypeExpression? type = null;
        if (Match(TokenType.Colon))
        {
            type = ParseType();
        }
        
        Expression? initializer = null;
        if (Match(TokenType.Assign))
        {
            initializer = ParseExpression();
        }
        
        ConsumeStatementTerminator();
        
        return new VariableDeclaration(name, type, initializer, visibility, isMutable, location);
    }

    private FunctionDeclaration ParseRecipeDeclaration(VisibilityModifier visibility = VisibilityModifier.Private)
    {
        var location = GetLocation(PeekToken(-1));
        
        var name = ConsumeIdentifier("Expected recipe name");
        
        // Parameters
        Consume(TokenType.LeftParen, "Expected '(' after recipe name");
        var parameters = new List<Parameter>();
        
        if (!Check(TokenType.RightParen))
        {
            do
            {
                var paramName = ConsumeIdentifier("Expected parameter name");
                Consume(TokenType.Colon, "Expected ':' after parameter name");
                var paramType = ParseType();
                
                Expression? defaultValue = null;
                if (Match(TokenType.Assign))
                {
                    defaultValue = ParseExpression();
                }
                
                parameters.Add(new Parameter(paramName, paramType, defaultValue, GetLocation()));
            } while (Match(TokenType.Comma));
        }
        
        Consume(TokenType.RightParen, "Expected ')' after parameters");
        
        // Return type
        TypeExpression? returnType = null;
        if (Match(TokenType.Arrow))
        {
            returnType = ParseType();
        }
        
        // Colon to start indented block
        Consume(TokenType.Colon, "Expected ':' after recipe header");
        
        // Body (indented block)
        var body = ParseIndentedBlock();
        
        return new FunctionDeclaration(name, parameters, returnType, body, visibility, new List<string>(), location);
    }

    private ClassDeclaration ParseClassDeclaration(VisibilityModifier visibility = VisibilityModifier.Private)
    {
        var location = GetLocation(PeekToken(-1));
        
        var name = ConsumeIdentifier("Expected entity name");
        
        // Base entity - can use "from Animal" syntax
        TypeExpression? baseClass = null;
        var interfaces = new List<TypeExpression>();
        if (Match(TokenType.From))
        {
            baseClass = ParseType();
        }
        
        // Colon to start indented block
        Consume(TokenType.Colon, "Expected ':' after entity header");
        
        var members = new List<Declaration>();
        
        // Parse indented members
        if (Match(TokenType.Indent))
        {
            while (!Check(TokenType.Dedent) && !IsAtEnd)
            {
                if (Match(TokenType.Newline)) continue;
                
                var member = ParseDeclaration() as Declaration;
                if (member != null)
                    members.Add(member);
            }
            
            if (!Match(TokenType.Dedent))
            {
                // If no dedent token, we might be at end of file
                if (!IsAtEnd)
                {
                    throw new ParseException("Expected dedent after entity body");
                }
            }
        }
        
        return new ClassDeclaration(name, null, baseClass, interfaces, members, visibility, location);
    }

    private StructDeclaration ParseStructDeclaration(VisibilityModifier visibility = VisibilityModifier.Private)
    {
        var location = GetLocation(PeekToken(-1));
        
        var name = ConsumeIdentifier("Expected record name");
        
        // Colon to start indented block
        Consume(TokenType.Colon, "Expected ':' after record header");
        
        var members = new List<Declaration>();
        
        // Parse record body as indented block
        Consume(TokenType.Newline, "Expected newline after ':'");
        
        if (Check(TokenType.Indent))
        {
            ProcessIndentToken();
            
            while (!Check(TokenType.Dedent) && !IsAtEnd)
            {
                if (Match(TokenType.Newline)) continue;
                
                var member = ParseDeclaration() as Declaration;
                if (member != null)
                    members.Add(member);
            }
            
            if (Check(TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw new ParseException("Expected dedent after record body");
            }
        }
        
        return new StructDeclaration(name, null, members, visibility, location);
    }

    private MenuDeclaration ParseChoiceDeclaration(VisibilityModifier visibility = VisibilityModifier.Private)
    {
        var location = GetLocation(PeekToken(-1));
        
        var name = ConsumeIdentifier("Expected option name");
        
        // Colon to start indented block
        Consume(TokenType.Colon, "Expected ':' after option header");
        
        var variants = new List<EnumVariant>();
        var methods = new List<FunctionDeclaration>();
        
        // Parse option body as indented block
        Consume(TokenType.Newline, "Expected newline after ':'");
        
        if (Check(TokenType.Indent))
        {
            ProcessIndentToken();
            
            while (!Check(TokenType.Dedent) && !IsAtEnd)
            {
                if (Match(TokenType.Newline)) continue;
                
                // Check if it's a method (recipe)
                if (Check(TokenType.Public) || Check(TokenType.Private) || Check(TokenType.recipe))
                {
                    var method = ParseDeclaration() as FunctionDeclaration;
                    if (method != null)
                        methods.Add(method);
                }
                else
                {
                    // Parse enum variant
                    var variantName = ConsumeIdentifier("Expected option variant name");
                    
                    int? value = null;
                    if (Match(TokenType.Assign))
                    {
                        var expr = ParseExpression();
                        if (expr is LiteralExpression literal && literal.Value is int intVal)
                        {
                            value = intVal;
                        }
                    }
                    
                    variants.Add(new EnumVariant(variantName, value, GetLocation()));
                    Match(TokenType.Newline);
                }
            }
            
            if (Check(TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw new ParseException("Expected dedent after option body");
            }
        }
        
        return new MenuDeclaration(name, variants, methods, visibility, location);
    }

    private VariantDeclaration ParseVariantDeclaration(VisibilityModifier visibility = VisibilityModifier.Private, VariantKind kind = VariantKind.Chimera)
    {
        var location = GetLocation(PeekToken(-1));
        
        var name = ConsumeIdentifier("Expected variant name");
        
        // Generic parameters
        List<string>? genericParams = null;
        if (Match(TokenType.Less))
        {
            genericParams = new List<string>();
            do
            {
                genericParams.Add(ConsumeIdentifier("Expected generic parameter name"));
            } while (Match(TokenType.Comma));
            
            Consume(TokenType.Greater, "Expected '>' after generic parameters");
        }
        
        // Colon to start indented block
        Consume(TokenType.Colon, "Expected ':' after variant header");
        
        var cases = new List<VariantCase>();
        var methods = new List<FunctionDeclaration>();
        
        // Parse variant body as indented block
        Consume(TokenType.Newline, "Expected newline after ':'");
        
        if (Check(TokenType.Indent))
        {
            ProcessIndentToken();
            
            while (!Check(TokenType.Dedent) && !IsAtEnd)
            {
                if (Match(TokenType.Newline)) continue;
                
                // Check if it's a method (recipe)
                if (Check(TokenType.Public) || Check(TokenType.Private) || Check(TokenType.recipe))
                {
                    var method = ParseDeclaration() as FunctionDeclaration;
                    if (method != null)
                        methods.Add(method);
                }
                else
                {
                    // Parse variant case
                    var caseName = ConsumeIdentifier("Expected variant case name");
                    
                    List<TypeExpression>? associatedTypes = null;
                    if (Match(TokenType.LeftParen))
                    {
                        associatedTypes = new List<TypeExpression>();
                        if (!Check(TokenType.RightParen))
                        {
                            do
                            {
                                associatedTypes.Add(ParseType());
                            } while (Match(TokenType.Comma));
                        }
                        Consume(TokenType.RightParen, "Expected ')' after variant case types");
                    }
                    
                    cases.Add(new VariantCase(caseName, associatedTypes, GetLocation()));
                    Match(TokenType.Newline);
                }
            }
            
            if (Check(TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw new ParseException("Expected dedent after variant body");
            }
        }
        
        return new VariantDeclaration(name, genericParams, cases, methods, visibility, kind, location);
    }

    private FeatureDeclaration ParseFeatureDeclaration(VisibilityModifier visibility = VisibilityModifier.Private)
    {
        var location = GetLocation(PeekToken(-1));
        
        var name = ConsumeIdentifier("Expected feature name");
        
        // Colon to start indented block
        Consume(TokenType.Colon, "Expected ':' after feature header");
        
        var methods = new List<FunctionSignature>();
        
        // Parse feature body as indented block
        Consume(TokenType.Newline, "Expected newline after ':'");
        
        if (Check(TokenType.Indent))
        {
            ProcessIndentToken();
            
            while (!Check(TokenType.Dedent) && !IsAtEnd)
            {
                if (Match(TokenType.Newline)) continue;
                
                // Parse recipe signature
                Consume(TokenType.recipe, "Expected 'recipe' in feature method");
                var methodName = ConsumeIdentifier("Expected method name");
                
                // Parameters
                Consume(TokenType.LeftParen, "Expected '(' after method name");
                var parameters = new List<Parameter>();
                
                if (!Check(TokenType.RightParen))
                {
                    do
                    {
                        var paramName = ConsumeIdentifier("Expected parameter name");
                        Consume(TokenType.Colon, "Expected ':' after parameter name");
                        var paramType = ParseType();
                        parameters.Add(new Parameter(paramName, paramType, null, GetLocation()));
                    } while (Match(TokenType.Comma));
                }
                
                Consume(TokenType.RightParen, "Expected ')' after parameters");
                
                // Return type
                TypeExpression? returnType = null;
                if (Match(TokenType.Arrow))
                {
                    returnType = ParseType();
                }
                
                methods.Add(new FunctionSignature(methodName, parameters, returnType, GetLocation()));
                Match(TokenType.Newline);
            }
            
            if (Check(TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw new ParseException("Expected dedent after feature body");
            }
        }
        
        return new FeatureDeclaration(name, null, methods, visibility, location);
    }

    private ImplementationDeclaration ParseImplementationDeclaration()
    {
        var location = GetLocation();
        
        var type = ParseType();
        Consume(TokenType.Follows, "Expected 'follows' in implementation");
        var trait = ParseType();
        
        // Colon to start indented block
        Consume(TokenType.Colon, "Expected ':' after implementation header");
        
        var methods = new List<FunctionDeclaration>();
        
        // Parse implementation body as indented block
        Consume(TokenType.Newline, "Expected newline after ':'");
        
        if (Check(TokenType.Indent))
        {
            ProcessIndentToken();
            
            while (!Check(TokenType.Dedent) && !IsAtEnd)
            {
                if (Match(TokenType.Newline)) continue;
                
                var method = ParseDeclaration() as FunctionDeclaration;
                if (method != null)
                    methods.Add(method);
            }
            
            if (Check(TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw new ParseException("Expected dedent after implementation body");
            }
        }
        
        return new ImplementationDeclaration(type, trait, methods, location);
    }

    private ImportDeclaration ParseImportDeclaration()
    {
        var location = GetLocation(PeekToken(-1));
        
        var modulePath = "";
        var alias = (string?)null;
        var specificImports = (List<string>?)null;
        
        // Parse module path - could be multiple identifiers separated by slashes
        do
        {
            var part = ConsumeIdentifier("Expected module name");
            modulePath += part;
            if (Match(TokenType.Slash))
            {
                modulePath += "/";
            }
            else
            {
                break;
            }
        } while (true);
        
        // Optional alias
        if (Match(TokenType.As))
        {
            alias = ConsumeIdentifier("Expected alias name");
        }
        
        ConsumeStatementTerminator();
        
        return new ImportDeclaration(modulePath, alias, specificImports, location);
    }

    private IAstNode ParseRedefinitionDeclaration()
    {
        var location = GetLocation(PeekToken(-1));
        
        var oldName = ConsumeIdentifier("Expected identifier after 'redefine'");
        Consume(TokenType.As, "Expected 'as' in redefinition");
        var newName = ConsumeIdentifier("Expected new identifier in redefinition");
        
        ConsumeStatementTerminator();
        
        return new RedefinitionDeclaration(oldName, newName, location);
    }

    private IAstNode ParseUsingDeclaration()
    {
        var location = GetLocation(PeekToken(-1));
        
        var type = ParseType();
        Consume(TokenType.As, "Expected 'as' in using declaration");
        var alias = ConsumeIdentifier("Expected alias name in using declaration");
        
        ConsumeStatementTerminator();
        
        return new UsingDeclaration(type, alias, location);
    }

    private VisibilityModifier ParseVisibilityModifier()
    {
        // Handle Cake's special visibility syntax: public(family), public(module)
        if (Match(TokenType.Public))
        {
            if (Match(TokenType.LeftParen))
            {
                var modifier = ConsumeIdentifier("Expected visibility modifier");
                Consume(TokenType.RightParen, "Expected ')' after visibility modifier");
                
                return modifier switch
                {
                    "family" => VisibilityModifier.Protected,
                    "module" => VisibilityModifier.Internal,
                    _ => throw new ParseException($"Unknown visibility modifier: {modifier}")
                };
            }
            return VisibilityModifier.Public;
        }
        
        if (Match(TokenType.PublicFamily))
            return VisibilityModifier.Protected;
        if (Match(TokenType.PublicModule))
            return VisibilityModifier.Internal;
        if (Match(TokenType.Private))
            return VisibilityModifier.Private;
        if (Match(TokenType.External))
            return VisibilityModifier.External;
        if (Match(TokenType.Global))
            return VisibilityModifier.Global;
        
        return VisibilityModifier.Private; // Default
    }

    private Statement ParseIfStatement()
    {
        var location = GetLocation(PeekToken(-1));
        
        var condition = ParseExpression();
        Consume(TokenType.Colon, "Expected ':' after if condition");
        var thenBranch = ParseIndentedBlock();
        
        Statement? elseBranch = null;
        if (Match(TokenType.Else))
        {
            Consume(TokenType.Colon, "Expected ':' after else");
            elseBranch = ParseIndentedBlock();
        }
        
        return new IfStatement(condition, thenBranch, elseBranch, location);
    }

    private Statement ParseWhileStatement()
    {
        var location = GetLocation(PeekToken(-1));
        
        var condition = ParseExpression();
        Consume(TokenType.Colon, "Expected ':' after while condition");
        var body = ParseIndentedBlock();
        
        return new WhileStatement(condition, body, location);
    }

    private Statement ParseForStatement()
    {
        var location = GetLocation(PeekToken(-1));
        
        var variable = ConsumeIdentifier("Expected variable name");
        Consume(TokenType.In, "Expected 'in' in for loop");
        var iterable = ParseExpression();
        Consume(TokenType.Colon, "Expected ':' after for header");
        var body = ParseIndentedBlock();
        
        return new ForStatement(variable, iterable, body, location);
    }

    private WhenStatement ParseWhenStatement()
    {
        var location = GetLocation(PeekToken(-1));
        
        var expression = ParseExpression();
        Consume(TokenType.Colon, "Expected ':' after when expression");
        
        Consume(TokenType.Newline, "Expected newline after when header");
        Consume(TokenType.Indent, "Expected indented block after when");
        
        var clauses = new List<WhenClause>();
        
        while (!Check(TokenType.Dedent) && !IsAtEnd)
        {
            // Skip newlines
            if (Match(TokenType.Newline)) continue;
            
            var pattern = ParsePattern();
            Consume(TokenType.FatArrow, "Expected '=>' after pattern");
            
            Statement body;
            if (Check(TokenType.Colon))
            {
                Consume(TokenType.Colon, "Expected ':' after '=>'");
                body = ParseIndentedBlock();
            }
            else
            {
                body = ParseExpressionStatement();
            }
            
            clauses.Add(new WhenClause(pattern, body, GetLocation()));
            
            // Optional newline between clauses
            Match(TokenType.Newline);
        }
        
        Consume(TokenType.Dedent, "Expected dedent after when clauses");
        
        return new WhenStatement(expression, clauses, location);
    }

    private Pattern ParsePattern()
    {
        var location = GetLocation();
        
        // Wildcard pattern: _
        if (Match(TokenType.Identifier) && PeekToken(-1).Text == "_")
        {
            return new WildcardPattern(location);
        }
        
        // Type pattern with optional variable binding: Type variableName or Type
        if (Check(TokenType.TypeIdentifier))
        {
            var type = ParseType();
            
            // Check for variable binding
            string? variableName = null;
            if (Check(TokenType.Identifier))
            {
                variableName = ConsumeIdentifier("Expected variable name for type pattern");
            }
            
            return new TypePattern(type, variableName, location);
        }
        
        // Identifier pattern: variable binding
        if (Check(TokenType.Identifier))
        {
            var name = ConsumeIdentifier("Expected identifier for pattern");
            return new IdentifierPattern(name, location);
        }
        
        // Literal pattern: constants like 42, "hello", true, etc.
        var expr = ParsePrimary();
        if (expr is LiteralExpression literal)
        {
            return new LiteralPattern(literal.Value, location);
        }
        
        throw new ParseException($"Expected pattern, got {CurrentToken.Type}");
    }

    private Statement ParseReturnStatement()
    {
        var location = GetLocation(PeekToken(-1));
        
        Expression? value = null;
        if (!Check(TokenType.Newline) && !IsAtEnd)
        {
            value = ParseExpression();
        }
        
        ConsumeStatementTerminator();
        
        return new ReturnStatement(value, location);
    }

    private Statement ParseBreakStatement()
    {
        var location = GetLocation(PeekToken(-1));
        ConsumeStatementTerminator();
        return new BreakStatement(location);
    }

    private Statement ParseContinueStatement()
    {
        var location = GetLocation(PeekToken(-1));
        ConsumeStatementTerminator();
        return new ContinueStatement(location);
    }

    private Statement ParseDangerStatement()
    {
        var location = GetLocation(PeekToken(-1));
        Consume(TokenType.Colon, "Expected ':' after 'danger'");
        var body = (BlockStatement)ParseIndentedBlock();
        return new DangerStatement(body, location);
    }

    private Statement ParseMayhemStatement()
    {
        var location = GetLocation(PeekToken(-1));
        Consume(TokenType.Colon, "Expected ':' after 'mayhem'");
        var body = (BlockStatement)ParseIndentedBlock();
        return new MayhemStatement(body, location);
    }

    private Statement ParseIndentedBlock()
    {
        var location = GetLocation();
        var statements = new List<Statement>();
        
        // Consume newline after colon
        Consume(TokenType.Newline, "Expected newline after ':'");
        
        // Must have an indent token for a proper indented block
        if (!Check(TokenType.Indent))
        {
            throw new ParseException("Expected indented block after ':'");
        }
        
        // Process the indent token
        ProcessIndentToken();
        
        // Parse statements until we hit a dedent
        while (!Check(TokenType.Dedent) && !IsAtEnd)
        {
            // Skip empty lines
            if (Match(TokenType.Newline)) continue;
            
            var stmt = ParseStatement();
            if (stmt != null)
                statements.Add(stmt);
        }
        
        // Process dedent tokens
        if (Check(TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw new ParseException("Expected dedent to close indented block");
        }
        
        return new BlockStatement(statements, location);
    }

    private ExpressionStatement ParseExpressionStatement()
    {
        var expr = ParseExpression();
        ConsumeStatementTerminator();
        return new ExpressionStatement(expr, expr.Location);
    }

    private Expression ParseExpression()
    {
        return ParseAssignment();
    }

    private Expression ParseAssignment()
    {
        var expr = ParseTernary();
        
        if (Match(TokenType.Assign))
        {
            var value = ParseAssignment();
            // For now, treat assignment as a binary expression
            return new BinaryExpression(expr, BinaryOperator.Assign, value, expr.Location);
        }
        
        return expr;
    }

    private Expression ParseTernary()
    {
        var expr = ParseLogicalOr();
        
        if (Match(TokenType.Question))
        {
            var thenExpr = ParseExpression();
            Consume(TokenType.Colon, "Expected ':' in ternary expression");
            var elseExpr = ParseExpression();
            return new ConditionalExpression(expr, thenExpr, elseExpr, expr.Location);
        }
        
        return expr;
    }

    private Expression ParseLogicalOr()
    {
        var expr = ParseRange();
        
        while (Match(TokenType.Or))
        {
            var op = PeekToken(-1);
            var right = ParseRange();
            expr = new BinaryExpression(expr, TokenToBinaryOperator(op.Type), right, GetLocation(op));
        }
        
        return expr;
    }

    private Expression ParseRange()
    {
        var expr = ParseLogicalAnd();
        
        // Handle range expressions: A to B or A to B step C
        if (Match(TokenType.To))
        {
            var end = ParseLogicalAnd();
            Expression? step = null;
            
            if (Match(TokenType.Step))
            {
                step = ParseLogicalAnd();
            }
            
            // Create a range expression - for now use a call expression to represent it
            var args = new List<Expression> { expr, end };
            if (step != null)
                args.Add(step);
            
            return new CallExpression(
                new IdentifierExpression("range", expr.Location),
                args,
                expr.Location);
        }
        
        return expr;
    }

    private Expression ParseLogicalAnd()
    {
        var expr = ParseEquality();
        
        while (Match(TokenType.And))
        {
            var op = PeekToken(-1);
            var right = ParseEquality();
            expr = new BinaryExpression(expr, TokenToBinaryOperator(op.Type), right, GetLocation(op));
        }
        
        return expr;
    }

    private Expression ParseEquality()
    {
        var expr = ParseComparison();
        
        while (Match(TokenType.Equal, TokenType.NotEqual))
        {
            var op = PeekToken(-1);
            var right = ParseComparison();
            expr = new BinaryExpression(expr, TokenToBinaryOperator(op.Type), right, GetLocation(op));
        }
        
        return expr;
    }

    private Expression ParseComparison()
    {
        var expr = ParseIsExpression();
        
        while (Match(TokenType.Less, TokenType.LessEqual, TokenType.Greater, TokenType.GreaterEqual))
        {
            var op = PeekToken(-1);
            var right = ParseIsExpression();
            expr = new BinaryExpression(expr, TokenToBinaryOperator(op.Type), right, GetLocation(op));
        }
        
        return expr;
    }

    private Expression ParseIsExpression()
    {
        var expr = ParseBitwiseOr();
        
        // Handle is/from/follows expressions when not in entity context
        while (Match(TokenType.Is, TokenType.From, TokenType.Follows))
        {
            var op = PeekToken(-1);
            var location = GetLocation(op);
            
            if (op.Type == TokenType.Is)
            {
                // Handle is expressions: expr is Type or expr is Type(pattern)
                var type = ParseType();
                
                // Check if it's a pattern with variable binding
                string? variableName = null;
                if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
                {
                    variableName = PeekToken(-1).Text;
                }
                
                // For now, represent as a call expression
                var args = new List<Expression> { expr };
                if (variableName != null)
                {
                    args.Add(new IdentifierExpression(variableName, location));
                }
                
                expr = new CallExpression(
                    new IdentifierExpression($"is_{type.Name}", location),
                    args,
                    location);
            }
            else
            {
                // Handle from/follows as comparison operators
                var right = ParseBitwiseOr();
                var operatorName = op.Type == TokenType.From ? "from" : "follows";
                
                expr = new CallExpression(
                    new IdentifierExpression(operatorName, location),
                    new List<Expression> { expr, right },
                    location);
            }
        }
        
        return expr;
    }

    private Expression ParseBitwiseOr()
    {
        var expr = ParseBitwiseXor();
        
        while (Match(TokenType.Pipe))
        {
            var op = PeekToken(-1);
            var right = ParseBitwiseXor();
            expr = new BinaryExpression(expr, TokenToBinaryOperator(op.Type), right, GetLocation(op));
        }
        
        return expr;
    }

    private Expression ParseBitwiseXor()
    {
        var expr = ParseBitwiseAnd();
        
        while (Match(TokenType.Caret))
        {
            var op = PeekToken(-1);
            var right = ParseBitwiseAnd();
            expr = new BinaryExpression(expr, TokenToBinaryOperator(op.Type), right, GetLocation(op));
        }
        
        return expr;
    }

    private Expression ParseBitwiseAnd()
    {
        var expr = ParseShift();
        
        while (Match(TokenType.Ampersand))
        {
            var op = PeekToken(-1);
            var right = ParseShift();
            expr = new BinaryExpression(expr, TokenToBinaryOperator(op.Type), right, GetLocation(op));
        }
        
        return expr;
    }

    private Expression ParseShift()
    {
        var expr = ParseAdditive();
        
        while (Match(TokenType.LeftShift, TokenType.RightShift))
        {
            var op = PeekToken(-1);
            var right = ParseAdditive();
            expr = new BinaryExpression(expr, TokenToBinaryOperator(op.Type), right, GetLocation(op));
        }
        
        return expr;
    }

    private Expression ParseAdditive()
    {
        var expr = ParseMultiplicative();
        
        while (Match(TokenType.Plus, TokenType.Minus, 
                     TokenType.PlusWrap, TokenType.PlusSaturate, TokenType.PlusUnchecked, TokenType.PlusChecked,
                     TokenType.MinusWrap, TokenType.MinusSaturate, TokenType.MinusUnchecked, TokenType.MinusChecked))
        {
            var op = PeekToken(-1);
            var right = ParseMultiplicative();
            expr = new BinaryExpression(expr, TokenToBinaryOperator(op.Type), right, GetLocation(op));
        }
        
        return expr;
    }

    private Expression ParseMultiplicative()
    {
        var expr = ParsePower();
        
        while (Match(TokenType.Star, TokenType.Slash, TokenType.Percent, TokenType.Divide,
                     TokenType.MultiplyWrap, TokenType.MultiplySaturate, TokenType.MultiplyUnchecked, TokenType.MultiplyChecked,
                     TokenType.DivideWrap, TokenType.DivideSaturate, TokenType.DivideUnchecked, TokenType.DivideChecked,
                     TokenType.ModuloWrap, TokenType.ModuloSaturate, TokenType.ModuloUnchecked, TokenType.ModuloChecked))
        {
            var op = PeekToken(-1);
            var right = ParsePower();
            expr = new BinaryExpression(expr, TokenToBinaryOperator(op.Type), right, GetLocation(op));
        }
        
        return expr;
    }

    private Expression ParsePower()
    {
        var expr = ParseUnary();
        
        while (Match(TokenType.Power,
                     TokenType.PowerWrap, TokenType.PowerSaturate, TokenType.PowerUnchecked, TokenType.PowerChecked))
        {
            var op = PeekToken(-1);
            var right = ParseUnary();
            expr = new BinaryExpression(expr, TokenToBinaryOperator(op.Type), right, GetLocation(op));
        }
        
        return expr;
    }

    private Expression ParseUnary()
    {
        if (Match(TokenType.Plus, TokenType.Minus, TokenType.Bang, TokenType.Not, TokenType.Tilde))
        {
            var op = PeekToken(-1);
            var expr = ParseUnary();
            return new UnaryExpression(TokenToUnaryOperator(op.Type), expr, GetLocation(op));
        }
        
        return ParsePostfix();
    }

    private Expression ParsePostfix()
    {
        var expr = ParsePrimary();
        
        while (true)
        {
            if (Match(TokenType.LeftParen))
            {
                // Function call
                var args = new List<Expression>();
                
                if (!Check(TokenType.RightParen))
                {
                    do
                    {
                        args.Add(ParseExpression());
                    } while (Match(TokenType.Comma));
                }
                
                Consume(TokenType.RightParen, "Expected ')' after arguments");
                expr = new CallExpression(expr, args, expr.Location);
            }
            else if (Match(TokenType.LeftBracket))
            {
                // Array/map indexing
                var index = ParseExpression();
                Consume(TokenType.RightBracket, "Expected ']' after index");
                expr = new IndexExpression(expr, index, expr.Location);
            }
            else if (Match(TokenType.Dot))
            {
                // Member access
                var member = ConsumeIdentifier("Expected member name after '.'");
                expr = new MemberExpression(expr, member, expr.Location);
            }
            else
            {
                break;
            }
        }
        
        return expr;
    }

    private Expression ParsePrimary()
    {
        var location = GetLocation();
        
        // Literals
        if (Match(TokenType.True))
            return new LiteralExpression(true, TokenType.True, location);
        
        if (Match(TokenType.False))
            return new LiteralExpression(false, TokenType.False, location);
        
        if (Match(TokenType.None))
            return new LiteralExpression(null!, TokenType.None, location);
        
        // Integer literals
        if (Match(TokenType.Integer, TokenType.S8Literal, TokenType.S16Literal, TokenType.S32Literal,
                  TokenType.S64Literal, TokenType.S128Literal, TokenType.SyssintLiteral,
                  TokenType.U8Literal, TokenType.U16Literal, TokenType.U32Literal,
                  TokenType.U64Literal, TokenType.U128Literal, TokenType.SysuintLiteral))
        {
            var token = PeekToken(-1);
            var value = token.Text;
            var cleanValue = value.Replace("s8", "").Replace("s16", "").Replace("s32", "").Replace("s64", "")
                                  .Replace("s128", "").Replace("syssint", "").Replace("u8", "").Replace("u16", "")
                                  .Replace("u32", "").Replace("u64", "").Replace("u128", "").Replace("sysuint", "");
            if (long.TryParse(cleanValue, out var intVal))
                return new LiteralExpression(intVal, token.Type, location);
            throw new ParseException($"Invalid integer literal: {value}");
        }
        
        // Float literals  
        if (Match(TokenType.Decimal, TokenType.F16Literal, TokenType.F32Literal, TokenType.F64Literal, TokenType.F128Literal,
                  TokenType.D32Literal, TokenType.D64Literal, TokenType.D128Literal))
        {
            var token = PeekToken(-1);
            var value = token.Text;
            var cleanValue = value.Replace("f16", "").Replace("f32", "").Replace("f64", "").Replace("f128", "")
                                  .Replace("d32", "").Replace("d64", "").Replace("d128", "");
            if (double.TryParse(cleanValue, out var floatVal))
                return new LiteralExpression(floatVal, token.Type, location);
            throw new ParseException($"Invalid float literal: {value}");
        }
        
        // String literals with Cake-specific handling
        if (Match(TokenType.TextLiteral, TokenType.FormattedText, TokenType.RawText, TokenType.RawFormattedText,
                  TokenType.Text8Literal, TokenType.Text8FormattedText, TokenType.Text8RawText, TokenType.Text8RawFormattedText,
                  TokenType.Text16Literal, TokenType.Text16FormattedText, TokenType.Text16RawText, TokenType.Text16RawFormattedText))
        {
            var token = PeekToken(-1);
            var value = token.Text;
            
            // Handle Cake's formatted string literals (f"...")
            if (value.StartsWith("f\"") && value.EndsWith("\""))
            {
                // This is a formatted string like f"{name} says hello"
                // For now, treat as regular string but mark as formatted
                value = value.Substring(2, value.Length - 3); // Remove f" and "
                return new LiteralExpression(value, TokenType.FormattedText, location);
            }
            
            // Regular string literals
            if (value.StartsWith("\"") && value.EndsWith("\""))
            {
                value = value.Substring(1, value.Length - 2);
            }
            
            // Handle text encoding prefixes (t8", t16", t32")
            if (value.StartsWith("t8\"") || value.StartsWith("t16\"") || value.StartsWith("t32\""))
            {
                var prefixEnd = value.IndexOf('"');
                if (prefixEnd > 0)
                {
                    value = value.Substring(prefixEnd + 1, value.Length - prefixEnd - 2);
                }
            }
            
            return new LiteralExpression(value, token.Type, location);
        }
        
        // Character literals
        if (Match(TokenType.Letter8Literal, TokenType.Letter16Literal, TokenType.LetterLiteral))
        {
            var token = PeekToken(-1);
            var value = token.Text;
            
            try
            {
                // Handle different prefixes: l8'a', l16'a', 'a'
                var quoteStart = value.LastIndexOf('\'');
                var quoteEnd = value.Length - 1;
                
                if (quoteStart >= 0 && quoteEnd > quoteStart && value[quoteEnd] == '\'')
                {
                    var charContent = value.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                    
                    // Handle escape sequences
                    if (charContent == "\\'")  // Single quote
                        return new LiteralExpression('\'', token.Type, location);
                    else if (charContent == "\\\\")  // Backslash
                        return new LiteralExpression('\\', token.Type, location);
                    else if (charContent == "\\n")  // Newline
                        return new LiteralExpression('\n', token.Type, location);
                    else if (charContent == "\\t")  // Tab
                        return new LiteralExpression('\t', token.Type, location);
                    else if (charContent == "\\r")  // Carriage return
                        return new LiteralExpression('\r', token.Type, location);
                    else if (charContent.Length == 1)
                        return new LiteralExpression(charContent[0], token.Type, location);
                }
            }
            catch
            {
                // Fall through to error
            }
            
            // For now, just return a placeholder for invalid character literals
            return new LiteralExpression('?', token.Type, location);
        }
        
        // Identifiers and Cake-specific keywords
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            var text = PeekToken(-1).Text;
            
            // Handle Cake's "me" keyword for self-reference
            if (text == "me")
            {
                return new IdentifierExpression("this", location); // Map to C# "this"
            }
            
            return new IdentifierExpression(text, location);
        }
        
        // Parenthesized expression
        if (Match(TokenType.LeftParen))
        {
            var expr = ParseExpression();
            Consume(TokenType.RightParen, "Expected ')' after expression");
            return expr;
        }
        
        // Conditional expression: if A then B else C
        if (Match(TokenType.If))
        {
            var condition = ParseExpression();
            Consume(TokenType.Then, "Expected 'then' in conditional expression");
            var thenExpr = ParseExpression();
            Consume(TokenType.Else, "Expected 'else' in conditional expression");
            var elseExpr = ParseExpression();
            return new ConditionalExpression(condition, thenExpr, elseExpr, location);
        }
        
        throw new ParseException($"Unexpected token: {CurrentToken.Type} at line {CurrentToken.Line}, column {CurrentToken.Column}");
    }

    private TypeExpression ParseType()
    {
        var location = GetLocation();
        
        // Named type (identifier or type identifier)
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            var name = PeekToken(-1).Text;
            
            // Check for generic type arguments
            if (Match(TokenType.Less))
            {
                var typeArgs = new List<TypeExpression>();
                
                do
                {
                    typeArgs.Add(ParseType());
                } while (Match(TokenType.Comma));
                
                Consume(TokenType.Greater, "Expected '>' after type arguments");
                
                return new TypeExpression(name, typeArgs, location);
            }
            
            return new TypeExpression(name, null, location);
        }
        
        throw new ParseException($"Expected type, got {CurrentToken.Type}");
    }

    private void ConsumeStatementTerminator()
    {
        // Check for unnecessary semicolons
        CheckUnnecessarySemicolon();

        // Check for unnecessary closing braces
        CheckUnnecessaryBrace();

        // Accept newline as statement terminator
        if (!Check(TokenType.Dedent) && !Check(TokenType.Else) && !IsAtEnd)
        {
            Match(TokenType.Newline);
        }
    }

    private string ConsumeIdentifier(string errorMessage)
    {
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            return PeekToken(-1).Text;
        }
        
        var current = CurrentToken;
        throw new ParseException($"{errorMessage} at line {current.Line}, column {current.Column}. " +
                                $"Expected Identifier, got {current.Type}.");
    }

    /// <summary>
    /// Process an INDENT token by pushing a new indentation level
    /// </summary>
    private void ProcessIndentToken()
    {
        if (!Match(TokenType.Indent))
        {
            throw new ParseException("Expected INDENT token");
        }
        
        _currentIndentationLevel++;
        _indentationStack.Push(_currentIndentationLevel);
    }

    /// <summary>
    /// Process one or more DEDENT tokens by popping indentation levels
    /// </summary>
    private void ProcessDedentTokens()
    {
        // Check for unnecessary closing braces before processing dedents
        CheckUnnecessaryBrace();

        while (Check(TokenType.Dedent) && !IsAtEnd)
        {
            Advance(); // Consume the DEDENT token
            
            if (_indentationStack.Count > 1) // Keep base level
            {
                _indentationStack.Pop();
                _currentIndentationLevel = _indentationStack.Peek();
            }
            else
            {
                throw new ParseException("Unexpected dedent - no matching indent");
            }
        }
    }

    /// <summary>
    /// Check if we're at a valid indentation level for statements
    /// </summary>
    private bool IsAtValidIndentationLevel()
    {
        return _indentationStack.Count > 0;
    }

    /// <summary>
    /// Get current indentation depth for debugging
    /// </summary>
    private int GetIndentationDepth()
    {
        return _indentationStack.Count - 1; // Subtract 1 for base level
    }
}