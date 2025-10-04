using Compilers.Shared.AST;
using Compilers.Shared.Parser;
using Compilers.Shared.Lexer;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Parser for RazorForge language
/// </summary>
public class RazorForgeParser : BaseParser
{
    public RazorForgeParser(List<Token> tokens) : base(tokens) { }

    public override Compilers.Shared.AST.Program Parse()
    {
        var declarations = new List<IAstNode>();
        
        while (!IsAtEnd)
        {
            try
            {
                // Skip newlines at top level
                if (Match(TokenType.Newline)) continue;
                
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
        
        // External declaration
        if (visibility == VisibilityModifier.External && Match(TokenType.recipe))
        {
            return ParseExternalDeclaration();
        }

        // Variable declarations
        if (Match(TokenType.Var, TokenType.Let))
        {
            return ParseVariableDeclaration(visibility);
        }

        // Function declaration
        if (Match(TokenType.recipe)) return ParseFunctionDeclaration(visibility);
        
        // Entity/Record/Enum declarations
        if (Match(TokenType.Entity)) return ParseClassDeclaration(visibility);
        if (Match(TokenType.Record)) return ParseStructDeclaration(visibility);
        if (Match(TokenType.Choice)) return ParseEnumDeclaration(visibility);
        if (Match(TokenType.Chimera)) return ParseVariantDeclaration(visibility, VariantKind.Chimera);
        if (Match(TokenType.Variant)) return ParseVariantDeclaration(visibility, VariantKind.Variant);
        if (Match(TokenType.Mutant)) return ParseVariantDeclaration(visibility, VariantKind.Mutant);
        if (Match(TokenType.Protocol)) return ParseFeatureDeclaration(visibility);
        
        // If we parsed a visibility modifier but no declaration follows, reset position
        if (visibility != VisibilityModifier.Private)
        {
            // Go back to before the visibility modifier
            Position--;
        }
        
        // Otherwise parse as statement
        return ParseStatement();
    }

    private Statement? ParseStatement()
    {
        // Control flow
        if (Match(TokenType.If)) return ParseIfStatement();
        if (Match(TokenType.Unless)) return ParseUnlessStatement();
        if (Match(TokenType.While)) return ParseWhileStatement();
        if (Match(TokenType.For)) return ParseForStatement();
        if (Match(TokenType.When)) return ParseWhenStatement();
        if (Match(TokenType.Return)) return ParseReturnStatement();
        if (Match(TokenType.Break)) return ParseBreakStatement();
        if (Match(TokenType.Continue)) return ParseContinueStatement();

        // Danger block
        if (Match(TokenType.Danger)) return ParseDangerStatement();

        // Mayhem block
        if (Match(TokenType.Mayhem)) return ParseMayhemStatement();

        // Block statement
        if (Check(TokenType.LeftBrace))
        {
            return ParseBlockStatement();
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
        if (Match(TokenType.Public))
            return VisibilityModifier.Public;
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

    private FunctionDeclaration ParseFunctionDeclaration(VisibilityModifier visibility = VisibilityModifier.Private)
    {
        var location = GetLocation(PeekToken(-1));
        
        var name = ConsumeIdentifier("Expected function name");
        
        // Parameters
        Consume(TokenType.LeftParen, "Expected '(' after function name");
        var parameters = new List<Parameter>();
        
        if (!Check(TokenType.RightParen))
        {
            do
            {
                var paramName = ConsumeIdentifier("Expected parameter name");
                TypeExpression? paramType = null;
                Expression? defaultValue = null;
                
                if (Match(TokenType.Colon))
                {
                    paramType = ParseType();
                }
                
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
        
        // Body
        var body = ParseBlockStatement();
        
        return new FunctionDeclaration(name, parameters, returnType, body, visibility, new List<string>(), location);
    }

    private ClassDeclaration ParseClassDeclaration(VisibilityModifier visibility = VisibilityModifier.Private)
    {
        var location = GetLocation(PeekToken(-1));
        
        var name = ConsumeIdentifier("Expected entity name");
        
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
        
        // Base entity - can use "from Animal" syntax
        TypeExpression? baseClass = null;
        var interfaces = new List<TypeExpression>();
        if (Match(TokenType.From))
        {
            baseClass = ParseType();
        }
        
        Consume(TokenType.LeftBrace, "Expected '{' after entity header");
        
        var members = new List<Declaration>();
        
        while (!Check(TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(TokenType.Newline)) continue;
            
            var member = ParseDeclaration() as Declaration;
            if (member != null)
                members.Add(member);
        }
        
        Consume(TokenType.RightBrace, "Expected '}' after entity body");
        
        return new ClassDeclaration(name, genericParams, baseClass, interfaces, members, visibility, location);
    }

    private StructDeclaration ParseStructDeclaration(VisibilityModifier visibility = VisibilityModifier.Private)
    {
        var location = GetLocation(PeekToken(-1));
        
        var name = ConsumeIdentifier("Expected record name");
        
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
        
        Consume(TokenType.LeftBrace, "Expected '{' after record header");
        
        var members = new List<Declaration>();
        
        while (!Check(TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(TokenType.Newline)) continue;
            
            var member = ParseDeclaration() as Declaration;
            if (member != null)
                members.Add(member);
        }
        
        Consume(TokenType.RightBrace, "Expected '}' after record body");
        
        return new StructDeclaration(name, genericParams, members, visibility, location);
    }

    private MenuDeclaration ParseEnumDeclaration(VisibilityModifier visibility = VisibilityModifier.Private)
    {
        var location = GetLocation(PeekToken(-1));
        
        var name = ConsumeIdentifier("Expected enum name");
        
        Consume(TokenType.LeftBrace, "Expected '{' after enum name");
        
        var variants = new List<EnumVariant>();
        
        while (!Check(TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(TokenType.Newline)) continue;
            
            var variantName = ConsumeIdentifier("Expected enum variant name");
            
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
            
            if (!Match(TokenType.Comma))
            {
                Match(TokenType.Newline);
            }
        }
        
        Consume(TokenType.RightBrace, "Expected '}' after enum body");
        
        return new MenuDeclaration(name, variants, new List<FunctionDeclaration>(), visibility, location);
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
        
        Consume(TokenType.LeftBrace, "Expected '{' after variant header");
        
        var cases = new List<VariantCase>();
        var methods = new List<FunctionDeclaration>();
        
        while (!Check(TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(TokenType.Newline)) continue;
            
            // Try to parse as function first
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
                
                if (!Match(TokenType.Comma))
                {
                    Match(TokenType.Newline);
                }
            }
        }
        
        Consume(TokenType.RightBrace, "Expected '}' after variant body");
        
        return new VariantDeclaration(name, genericParams, cases, methods, visibility, kind, location);
    }

    private FeatureDeclaration ParseFeatureDeclaration(VisibilityModifier visibility = VisibilityModifier.Private)
    {
        var location = GetLocation(PeekToken(-1));
        
        var name = ConsumeIdentifier("Expected feature name");
        
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
        
        Consume(TokenType.LeftBrace, "Expected '{' after feature header");
        
        var methods = new List<FunctionSignature>();
        
        while (!Check(TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(TokenType.Newline)) continue;
            
            // Parse function signature
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
        
        Consume(TokenType.RightBrace, "Expected '}' after feature body");
        
        return new FeatureDeclaration(name, genericParams, methods, visibility, location);
    }

    private IfStatement ParseIfStatement()
    {
        var location = GetLocation(PeekToken(-1));
        
        var condition = ParseExpression();
        var thenBranch = ParseStatement();
        Statement? elseBranch = null;
        
        // Handle elif chain
        while (Match(TokenType.Elif))
        {
            var elifCondition = ParseExpression();
            var elifBranch = ParseStatement();
            
            // Convert elif to nested if-else
            var nestedIf = new IfStatement(elifCondition, elifBranch, null, GetLocation(PeekToken(-1)));
            
            if (elseBranch == null)
            {
                elseBranch = nestedIf;
            }
            else
            {
                // Chain elifs together
                if (elseBranch is IfStatement prevIf && prevIf.ElseStatement == null)
                {
                    elseBranch = new IfStatement(prevIf.Condition, prevIf.ThenStatement, nestedIf, prevIf.Location);
                }
                else
                {
                    elseBranch = nestedIf;
                }
            }
        }
        
        if (Match(TokenType.Else))
        {
            var finalElse = ParseStatement();
            
            if (elseBranch == null)
            {
                elseBranch = finalElse;
            }
            else if (elseBranch is IfStatement lastIf)
            {
                // Attach final else to the end of the elif chain
                var current = lastIf;
                while (current.ElseStatement is IfStatement nextIf)
                {
                    current = nextIf;
                }
                elseBranch = new IfStatement(lastIf.Condition, lastIf.ThenStatement, 
                    current.ElseStatement == null ? finalElse : current.ElseStatement, lastIf.Location);
            }
        }
        
        return new IfStatement(condition, thenBranch, elseBranch, location);
    }

    private IfStatement ParseUnlessStatement()
    {
        var location = GetLocation(PeekToken(-1));
        
        var condition = ParseExpression();
        var thenBranch = ParseStatement();
        Statement? elseBranch = null;
        
        if (Match(TokenType.Else))
        {
            elseBranch = ParseStatement();
        }
        
        // Unless is "if not condition"
        var negatedCondition = new UnaryExpression(UnaryOperator.Not, condition, condition.Location);
        
        return new IfStatement(negatedCondition, thenBranch, elseBranch, location);
    }

    private WhileStatement ParseWhileStatement()
    {
        var location = GetLocation(PeekToken(-1));
        
        var condition = ParseExpression();
        var body = ParseStatement();
        
        return new WhileStatement(condition, body, location);
    }

    private ForStatement ParseForStatement()
    {
        var location = GetLocation(PeekToken(-1));
        
        var variable = ConsumeIdentifier("Expected variable name");
        Consume(TokenType.In, "Expected 'in' in for loop");
        var iterable = ParseExpression();
        var body = ParseStatement();
        
        return new ForStatement(variable, iterable, body, location);
    }

    private WhenStatement ParseWhenStatement()
    {
        var location = GetLocation(PeekToken(-1));
        
        var expression = ParseExpression();
        
        Consume(TokenType.LeftBrace, "Expected '{' after when expression");
        
        var clauses = new List<WhenClause>();
        
        while (!Check(TokenType.RightBrace) && !IsAtEnd)
        {
            // Skip newlines
            if (Match(TokenType.Newline)) continue;
            
            var pattern = ParsePattern();
            Consume(TokenType.FatArrow, "Expected '=>' after pattern");
            var body = ParseStatement();
            
            clauses.Add(new WhenClause(pattern, body, GetLocation()));
            
            // Optional comma or newline between clauses
            Match(TokenType.Comma, TokenType.Newline);
        }
        
        Consume(TokenType.RightBrace, "Expected '}' after when clauses");
        
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

    private ReturnStatement ParseReturnStatement()
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

    private BreakStatement ParseBreakStatement()
    {
        var location = GetLocation(PeekToken(-1));
        ConsumeStatementTerminator();
        return new BreakStatement(location);
    }

    private ContinueStatement ParseContinueStatement()
    {
        var location = GetLocation(PeekToken(-1));
        ConsumeStatementTerminator();
        return new ContinueStatement(location);
    }

    private BlockStatement ParseBlockStatement()
    {
        var location = GetLocation();
        Consume(TokenType.LeftBrace, "Expected '{'");

        var statements = new List<Statement>();

        while (!Check(TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(TokenType.Newline)) continue;

            // Handle variable declarations inside blocks
            if (Match(TokenType.Var, TokenType.Let))
            {
                var varDecl = ParseVariableDeclaration();
                // Wrap the variable declaration as a declaration statement
                statements.Add(new DeclarationStatement(varDecl, varDecl.Location));
                continue;
            }

            var stmt = ParseStatement();
            if (stmt != null)
                statements.Add(stmt);
        }

        Consume(TokenType.RightBrace, "Expected '}'");

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
        
        // Check for chained comparisons (a < b < c)
        var operators = new List<BinaryOperator>();
        var operands = new List<Expression> { expr };
        
        while (Match(TokenType.Less, TokenType.LessEqual, TokenType.Greater, TokenType.GreaterEqual, 
                     TokenType.Equal, TokenType.NotEqual))
        {
            var op = PeekToken(-1);
            operators.Add(TokenToBinaryOperator(op.Type));
            
            var right = ParseIsExpression();
            operands.Add(right);
        }
        
        // If we have chained comparisons, create a ChainedComparisonExpression
        if (operators.Count > 1)
        {
            return new ChainedComparisonExpression(operands, operators, GetLocation());
        }
        else if (operators.Count == 1)
        {
            // Single comparison, create regular BinaryExpression
            return new BinaryExpression(operands[0], operators[0], operands[1], GetLocation());
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
            // Handle standalone generic function calls like func<T>!(args)
            if (expr is IdentifierExpression && Check(TokenType.Less))
            {
                Advance(); // consume '<'
                var typeArgs = new List<TypeExpression>();
                do
                {
                    typeArgs.Add(ParseType());
                } while (Match(TokenType.Comma));

                Consume(TokenType.Greater, "Expected '>' after generic type arguments");

                // Check for memory operation with !
                bool isMemoryOperation = Match(TokenType.Bang);

                if (Match(TokenType.LeftParen))
                {
                    var args = new List<Expression>();
                    if (!Check(TokenType.RightParen))
                    {
                        do
                        {
                            args.Add(ParseExpression());
                        } while (Match(TokenType.Comma));
                    }
                    Consume(TokenType.RightParen, "Expected ')' after arguments");

                    expr = new GenericMethodCallExpression(expr, ((IdentifierExpression)expr).Name, typeArgs, args, isMemoryOperation, expr.Location);
                }
                else
                {
                    // Generic type reference without call
                    expr = new GenericMemberExpression(expr, ((IdentifierExpression)expr).Name, typeArgs, expr.Location);
                }
            }
            else if (Match(TokenType.LeftParen))
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

                // Check if this is a slice constructor call
                if (expr is IdentifierExpression identifier &&
                    (identifier.Name == "HeapSlice" || identifier.Name == "StackSlice") &&
                    args.Count == 1)
                {
                    expr = new SliceConstructorExpression(identifier.Name, args[0], expr.Location);
                }
                else
                {
                    expr = new CallExpression(expr, args, expr.Location);
                }
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

                // Check for generic method call with type parameters
                if (Match(TokenType.Less))
                {
                    var typeArgs = new List<TypeExpression>();
                    do
                    {
                        typeArgs.Add(ParseType());
                    } while (Match(TokenType.Comma));

                    Consume(TokenType.Greater, "Expected '>' after generic type arguments");

                    // Check for method call with !
                    bool isMemoryOperation = Match(TokenType.Bang);

                    if (Match(TokenType.LeftParen))
                    {
                        var args = new List<Expression>();
                        if (!Check(TokenType.RightParen))
                        {
                            do
                            {
                                args.Add(ParseExpression());
                            } while (Match(TokenType.Comma));
                        }
                        Consume(TokenType.RightParen, "Expected ')' after arguments");

                        expr = new GenericMethodCallExpression(expr, member, typeArgs, args, isMemoryOperation, expr.Location);
                    }
                    else
                    {
                        expr = new GenericMemberExpression(expr, member, typeArgs, expr.Location);
                    }
                }
                else
                {
                    // Check for memory operation with !
                    bool isMemoryOperation = Match(TokenType.Bang);

                    if (isMemoryOperation && Match(TokenType.LeftParen))
                    {
                        var args = new List<Expression>();
                        if (!Check(TokenType.RightParen))
                        {
                            do
                            {
                                args.Add(ParseExpression());
                            } while (Match(TokenType.Comma));
                        }
                        Consume(TokenType.RightParen, "Expected ')' after arguments");

                        expr = new MemoryOperationExpression(expr, member, args, expr.Location);
                    }
                    else
                    {
                        expr = new MemberExpression(expr, member, expr.Location);
                    }
                }
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

            long intVal;
            // Handle hexadecimal literals (0x prefix)
            if (cleanValue.StartsWith("0x") || cleanValue.StartsWith("0X"))
            {
                var hexPart = cleanValue.Substring(2); // Remove "0x" prefix
                if (long.TryParse(hexPart, System.Globalization.NumberStyles.HexNumber, null, out intVal))
                    return new LiteralExpression(intVal, token.Type, location);
            }
            // Handle binary literals (0b prefix)
            else if (cleanValue.StartsWith("0b") || cleanValue.StartsWith("0B"))
            {
                var binaryPart = cleanValue.Substring(2); // Remove "0b" prefix
                try
                {
                    intVal = Convert.ToInt64(binaryPart, 2);
                    return new LiteralExpression(intVal, token.Type, location);
                }
                catch (Exception)
                {
                    throw new ParseException($"Invalid binary literal: {value}");
                }
            }
            // Handle decimal literals
            else if (long.TryParse(cleanValue, out intVal))
            {
                return new LiteralExpression(intVal, token.Type, location);
            }

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
        
        // String literals
        if (Match(TokenType.TextLiteral, TokenType.FormattedText, TokenType.RawText, TokenType.RawFormattedText,
                  TokenType.Text8Literal, TokenType.Text8FormattedText, TokenType.Text8RawText, TokenType.Text8RawFormattedText,
                  TokenType.Text16Literal, TokenType.Text16FormattedText, TokenType.Text16RawText, TokenType.Text16RawFormattedText))
        {
            var token = PeekToken(-1);
            var value = token.Text;

            // Handle RazorForge's formatted string literals (f"...")
            if (value.StartsWith("f\"") && value.EndsWith("\""))
            {
                // This is a formatted string like f"{name} says hello"
                // For now, treat as regular string but mark as formatted
                value = value.Substring(2, value.Length - 3); // Remove f" and "
                return new LiteralExpression(value, TokenType.FormattedText, location);
            }

            // Regular string literals - strip quotes and prefixes
            if (value.StartsWith("\"") && value.EndsWith("\""))
            {
                value = value.Substring(1, value.Length - 2);
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
        
        // Memory size literals
        if (Match(TokenType.ByteLiteral, TokenType.KilobyteLiteral, TokenType.KibibyteLiteral, 
                  TokenType.KilobitLiteral, TokenType.KibibitLiteral, TokenType.MegabyteLiteral, 
                  TokenType.MebibyteLiteral, TokenType.MegabitLiteral, TokenType.MebibitLiteral,
                  TokenType.GigabyteLiteral, TokenType.GibibyteLiteral, TokenType.GigabitLiteral, 
                  TokenType.GibibitLiteral, TokenType.TerabyteLiteral, TokenType.TebibyteLiteral,
                  TokenType.TerabitLiteral, TokenType.TebibitLiteral, TokenType.PetabyteLiteral, 
                  TokenType.PebibyteLiteral, TokenType.PetabitLiteral, TokenType.PebibitLiteral))
        {
            var token = PeekToken(-1);
            var value = token.Text;
            // Extract numeric part from memory literals like "100mb", "4gb", etc.
            var numericPart = new string(value.TakeWhile(char.IsDigit).ToArray());
            if (long.TryParse(numericPart, out var memoryVal))
                return new LiteralExpression(memoryVal, token.Type, location);
            throw new ParseException($"Invalid memory literal: {value}");
        }
        
        // Duration/time literals
        if (Match(TokenType.WeekLiteral, TokenType.DayLiteral, TokenType.HourLiteral, 
                  TokenType.MinuteLiteral, TokenType.SecondLiteral, TokenType.MillisecondLiteral,
                  TokenType.MicrosecondLiteral, TokenType.NanosecondLiteral))
        {
            var token = PeekToken(-1);
            var value = token.Text;
            // Extract numeric part from time literals like "30m", "24h", "500ms", etc.
            var numericPart = new string(value.TakeWhile(char.IsDigit).ToArray());
            if (long.TryParse(numericPart, out var timeVal))
                return new LiteralExpression(timeVal, token.Type, location);
            throw new ParseException($"Invalid time literal: {value}");
        }
        
        // Identifiers
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            return new IdentifierExpression(PeekToken(-1).Text, location);
        }

        // Parenthesized expression
        if (Match(TokenType.LeftParen))
        {
            var expr = ParseExpression();
            Consume(TokenType.RightParen, "Expected ')' after expression");
            return expr;
        }

        // Lambda expression
        if (Match(TokenType.recipe))
        {
            return ParseLambdaExpression(location);
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

    private LambdaExpression ParseLambdaExpression(SourceLocation location)
    {
        // Parse parameters
        Consume(TokenType.LeftParen, "Expected '(' after 'func' in lambda");
        var parameters = new List<Parameter>();
        
        if (!Check(TokenType.RightParen))
        {
            do
            {
                var paramName = ConsumeIdentifier("Expected parameter name");
                TypeExpression? paramType = null;
                Expression? defaultValue = null;
                
                if (Match(TokenType.Colon))
                {
                    paramType = ParseType();
                }
                
                if (Match(TokenType.Assign))
                {
                    defaultValue = ParseExpression();
                }
                
                parameters.Add(new Parameter(paramName, paramType, defaultValue, GetLocation()));
            } while (Match(TokenType.Comma));
        }
        
        Consume(TokenType.RightParen, "Expected ')' after lambda parameters");
        
        // Body - for now just parse expression (block lambdas would need special handling)
        var body = ParseExpression();
        
        return new LambdaExpression(parameters, body, location);
    }

    private TypeExpression ParseType()
    {
        var location = GetLocation();
        
        // Basic types - use identifiers for type names
        // RazorForge doesn't have specific type tokens, just identifier tokens for types
        
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

        throw new ParseException($"Expected type, got {CurrentToken.Type} ('{CurrentToken.Text}')");
    }

    private void ConsumeStatementTerminator()
    {
        // Check for unnecessary semicolon and issue warning
        CheckUnnecessarySemicolon();

        // Accept newline as statement terminator
        if (!Check(TokenType.RightBrace) && !Check(TokenType.Else) && !IsAtEnd)
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
                                $"Expected Identifier or TypeIdentifier, got {current.Type}.");
    }

    private ExternalDeclaration ParseExternalDeclaration()
    {
        var location = GetLocation(PeekToken(-2)); // -2 because we consumed 'external' and 'recipe'

        var name = ConsumeIdentifier("Expected function name");

        // Check for generic parameters
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

        // Handle external functions with ! suffix (memory operations)
        Match(TokenType.Bang);

        // Parameters
        Consume(TokenType.LeftParen, "Expected '(' after function name");
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

        ConsumeStatementTerminator();

        return new ExternalDeclaration(name, genericParams, parameters, returnType, location);
    }

    private DangerStatement ParseDangerStatement()
    {
        var location = GetLocation(PeekToken(-1));

        // Expect 'danger!'
        Consume(TokenType.Bang, "Expected '!' after 'danger'");

        var body = ParseBlockStatement();

        return new DangerStatement(body, location);
    }

    private MayhemStatement ParseMayhemStatement()
    {
        var location = GetLocation(PeekToken(-1));

        // Expect 'mayhem!'
        Consume(TokenType.Bang, "Expected '!' after 'mayhem'");

        var body = ParseBlockStatement();

        return new MayhemStatement(body, location);
    }

    private SliceConstructorExpression ParseSliceConstructor()
    {
        var location = GetLocation();
        var typeName = ConsumeIdentifier("Expected slice type name");

        Consume(TokenType.LeftParen, "Expected '(' after slice type");
        var sizeExpr = ParseExpression();
        Consume(TokenType.RightParen, "Expected ')' after slice size");

        return new SliceConstructorExpression(typeName, sizeExpr, location);
    }
}