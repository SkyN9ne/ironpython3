// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information.

using MSAst = System.Linq.Expressions;

using System.Collections.Generic;
using System.Diagnostics;

using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

using IronPython.Runtime;

/*
 * The name binding:
 *
 * The name binding happens in 4 passes.
 *
 * The first pass is a full recursive walk of the AST. During this walk, all scopes are established
 * (created by functions, classes, comprehensions, generators, and the AST root),
 * and in each scope all variables are collected that are defined (local or parameter)
 * or declared (global or nonlocal) there. Also, for each scope, all references used in the
 * scope are collected but kept unresolved as yet.
 *
 * The second pass uses the collected stack of all scopes and has each scope resolve its references.
 * The references can be resolved locally within the scope or as free variables, 
 * either as globals or as references to lexically enclosing scopes.
 *
 * The second pass happens in post-order (a scope is processed after processing all its nested scopes).
 * Consequently, when the scope is processing its free variables, it also knows already
 * which of its locals are being lifted to the closure.
 *
 * The third pass goes over all the scopes again, this time in pre-order (except for the root AST).
 * In this pass, all scopes build theirs closures, if needed. Becasue nested scopes are processed
 * after their encompassing scope, scopes may use the already prepared closure
 * from their parent.
 *
 * During the fourth pass, each scope is being inspected by the FlowChecker,
 * which analyzes the data flow in the scope. Information collected during this analysis
 * allows for some optimizations later on, when the expression trees are being constructed.
 */

namespace IronPython.Compiler.Ast {
    using Ast = MSAst.Expression;

    internal class DefineBinder : PythonWalkerNonRecursive {
        private PythonNameBinder _binder;
        public DefineBinder(PythonNameBinder binder) {
            _binder = binder;
        }
        public override bool Walk(NameExpression node) {
            _binder.DefineName(node.Name);
            return false;
        }
        public override bool Walk(ParenthesisExpression node) {
            return true;
        }
        public override bool Walk(TupleExpression node) {
            return true;
        }
        public override bool Walk(ListExpression node) {
            return true;
        }
    }

    internal class DeleteBinder : PythonWalkerNonRecursive {
        private PythonNameBinder _binder;
        public DeleteBinder(PythonNameBinder binder) {
            _binder = binder;
        }
        public override bool Walk(NameExpression node) {
            _binder.DefineDeleted(node.Name);
            return false;
        }
    }

    internal class PythonNameBinder : PythonWalker {
        private PythonAst _globalScope;
        internal ScopeStatement _currentScope;
        private List<ScopeStatement> _scopes = new List<ScopeStatement>();
        private List<ILoopStatement> _loops = new List<ILoopStatement>();
        private List<int> _finallyCount = new List<int>();

        #region Recursive binders

        private readonly DefineBinder _define;
        private readonly DeleteBinder _delete;

        #endregion

        public CompilerContext Context { get; }

        private PythonNameBinder(CompilerContext context) {
            _define = new DefineBinder(this);
            _delete = new DeleteBinder(this);
            Context = context;
        }

        #region Public surface

        internal static void BindAst(PythonAst ast, CompilerContext context) {
            Assert.NotNull(ast, context);

            PythonNameBinder binder = new PythonNameBinder(context);
            binder.Bind(ast);
        }

        #endregion

        private void Bind(PythonAst unboundAst) {
            Assert.NotNull(unboundAst);

            _currentScope = _globalScope = unboundAst;
            _finallyCount.Add(0);

            // Find all scopes and variables
            unboundAst.Walk(this);

            // Bind scopes
            foreach (ScopeStatement scope in _scopes) {
                scope.Bind(this);
            }

            // Bind globals
            unboundAst.Bind(this);

            // Finish binding w/ outer most scopes first.
            for (int i = _scopes.Count - 1; i >= 0; i--) {
                _scopes[i].FinishBind(this);
            }

            // Finish globals
            unboundAst.FinishBind(this);

            // Run flow checker
            foreach (ScopeStatement scope in _scopes) {
                FlowChecker.Check(scope);
            }
        }

        private void PushScope(ScopeStatement node) {
            node.Parent = _currentScope;
            _currentScope = node;
            _finallyCount.Add(0);
        }

        private void PopScope() {
            _scopes.Add(_currentScope);
            _currentScope = _currentScope.Parent;
            _finallyCount.RemoveAt(_finallyCount.Count - 1);
        }

        internal PythonReference Reference(string name) {
            return _currentScope.Reference(name);
        }

        internal PythonVariable DefineName(string name) {
            return _currentScope.EnsureVariable(name);
        }

        internal PythonVariable DefineParameter(string name) {
            return _currentScope.DefineParameter(name);
        }

        internal PythonVariable DefineDeleted(string name) {
            PythonVariable variable = _currentScope.EnsureVariable(name);
            variable.RegisterDeletion();
            return variable;
        }

        internal void ReportSyntaxWarning(string message, Node node) {
            Context.Errors.Add(Context.SourceUnit, message, node.Span, ErrorCodes.SyntaxError, Severity.Warning);
        }

        internal void ReportSyntaxError(string message, Node node) {
            Context.Errors.Add(Context.SourceUnit, message, node.Span, ErrorCodes.SyntaxError, Severity.FatalError);
        }

        #region AstBinder Overrides

        // AssignmentStatement
        public override bool Walk(AssignmentStatement node) {
            node.Parent = _currentScope;
            foreach (Expression e in node.Left) {
                e.Walk(_define);
            }
            return true;
        }

        // AnnotatedAssignStatement
        public override bool Walk(AnnotatedAssignStatement node) {
            node.Parent = _currentScope;
            node.Target.Walk(_define);
            return true;
        }

        // AugmentedAssignStatement
        public override bool Walk(AugmentedAssignStatement node) {
            node.Parent = _currentScope;
            node.Left.Walk(_define);
            return true;
        }

        public override void PostWalk(CallExpression node) {
            if (node.NeedsLocalsDictionary()) {
                _currentScope.NeedsLocalsDictionary = true;
            }
        }

        // ClassDefinition
        public override bool Walk(ClassDefinition node) {
            node.PythonVariable = DefineName(node.Name);

            // Base references are in the outer context
            foreach (var b in node.Bases) b.Walk(this);

            foreach (var a in node.Keywords) a.Walk(this);

            // process the decorators in the outer context
            if (node.Decorators != null) {
                foreach (Expression dec in node.Decorators) {
                    dec.Walk(this);
                }
            }

            PushScope(node);

            node.ModuleNameVariable = _globalScope.EnsureGlobalVariable("__name__");

            // define the __doc__ and the __module__
            if (node.Body.Documentation != null) {
                node.DocVariable = DefineName("__doc__");
            }
            node.ModVariable = DefineName("__module__");

            // Walk the body
            node.Body.Walk(this);
            return false;
        }

        // ClassDefinition
        public override void PostWalk(ClassDefinition node) {
            Debug.Assert(node == _currentScope);
            PopScope();
        }

        // DelStatement
        public override bool Walk(DelStatement node) {
            node.Parent = _currentScope;

            foreach (Expression e in node.Expressions) {
                e.Walk(_delete);
            }
            return true;
        }

        // Comprehensions

        private void WalkComprehensionIterators(Comprehension node) {
            node.Parent = _currentScope;

            // Special walk case: first (outermost) "for" iterator
            // See also: PythonAst.LookupVisitor.VisitComprehension(...)
            var outermostFor = (ComprehensionFor)node.Iterators[0];
            outermostFor.List.Walk(this);
            PushScope(node.Scope);
            Walk(outermostFor);
            outermostFor.Left.Walk(this);
            PostWalk(outermostFor);

            // Regular walk cases: remaining iterators/conditionals
            for (int i = 1; i < node.Iterators.Count; i++) {
                node.Iterators[i].Walk(this);
            }
        }

        public override bool Walk(ListComprehension node) {
            WalkComprehensionIterators(node);
            node.Item.Walk(this);
            return false;
        }

        public override void PostWalk(ListComprehension node) {
            base.PostWalk(node);
            PopScope();

            if (node.Scope.NeedsLocalsDictionary) {
                _currentScope.NeedsLocalsDictionary = true;
            }
        }

        public override bool Walk(SetComprehension node) {
            WalkComprehensionIterators(node);
            node.Item.Walk(this);
            return false;
        }

        public override void PostWalk(SetComprehension node) {
            base.PostWalk(node);
            PopScope();

            if (node.Scope.NeedsLocalsDictionary) {
                _currentScope.NeedsLocalsDictionary = true;
            }
        }

        public override bool Walk(DictionaryComprehension node) {
            WalkComprehensionIterators(node);
            node.Key.Walk(this);
            node.Value.Walk(this);
            return false;
        }

        public override void PostWalk(DictionaryComprehension node) {
            base.PostWalk(node);
            PopScope();

            if (node.Scope.NeedsLocalsDictionary) {
                _currentScope.NeedsLocalsDictionary = true;
            }
        }

        public override void PostWalk(ConditionalExpression node) {
            node.Parent = _currentScope;
            base.PostWalk(node);
        }

        // This is generated by the scripts\generate_walker.py script.
        // That will scan all types that derive from the IronPython AST nodes that aren't interesting for scopes
        // and inject into here.

        #region Generated Python Name Binder Propagate Current Scope

        // *** BEGIN GENERATED CODE ***
        // generated by function: gen_python_name_binder from: generate_walker.py

        // AndExpression
        public override bool Walk(AndExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // AssertStatement
        public override bool Walk(AssertStatement node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // AsyncStatement
        public override bool Walk(AsyncStatement node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // BinaryExpression
        public override bool Walk(BinaryExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // ComprehensionIf
        public override bool Walk(ComprehensionIf node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // ConditionalExpression
        public override bool Walk(ConditionalExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // ConstantExpression
        public override bool Walk(ConstantExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // DictionaryExpression
        public override bool Walk(DictionaryExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // DottedName
        public override bool Walk(DottedName node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // EmptyStatement
        public override bool Walk(EmptyStatement node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // ErrorExpression
        public override bool Walk(ErrorExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // ExpressionStatement
        public override bool Walk(ExpressionStatement node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // FormattedValueExpression
        public override bool Walk(FormattedValueExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // GeneratorExpression
        public override bool Walk(GeneratorExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // IfStatement
        public override bool Walk(IfStatement node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // IfStatementTest
        public override bool Walk(IfStatementTest node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // IndexExpression
        public override bool Walk(IndexExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // JoinedStringExpression
        public override bool Walk(JoinedStringExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // Keyword
        public override bool Walk(Keyword node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // LambdaExpression
        public override bool Walk(LambdaExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // ListExpression
        public override bool Walk(ListExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // MemberExpression
        public override bool Walk(MemberExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // ModuleName
        public override bool Walk(ModuleName node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // OrExpression
        public override bool Walk(OrExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // Parameter
        public override bool Walk(Parameter node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // ParenthesisExpression
        public override bool Walk(ParenthesisExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // RelativeModuleName
        public override bool Walk(RelativeModuleName node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // SetExpression
        public override bool Walk(SetExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // SliceExpression
        public override bool Walk(SliceExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // StarredExpression
        public override bool Walk(StarredExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // SuiteStatement
        public override bool Walk(SuiteStatement node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // TryStatementHandler
        public override bool Walk(TryStatementHandler node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // TupleExpression
        public override bool Walk(TupleExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // UnaryExpression
        public override bool Walk(UnaryExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // YieldExpression
        public override bool Walk(YieldExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }
        // YieldFromExpression
        public override bool Walk(YieldFromExpression node) {
            node.Parent = _currentScope;
            return base.Walk(node);
        }

        // *** END GENERATED CODE ***

        #endregion

        public override bool Walk(RaiseStatement node) {
            node.Parent = _currentScope;
            node.InFinally = _finallyCount[_finallyCount.Count - 1] != 0;
            return base.Walk(node);
        }

        // ForEachStatement
        public override bool Walk(ForStatement node) {
            node.Parent = _currentScope;
            if (_currentScope is FunctionDefinition) {
                _currentScope.ShouldInterpret = false;
            }

            // we only push the loop for the body of the loop
            // so we need to walk the for statement ourselves
            node.Left.Walk(_define);

            node.Left?.Walk(this);
            node.List?.Walk(this);

            PushLoop(node);

            node.Body?.Walk(this);

            PopLoop();

            node.Else?.Walk(this);

            return false;
        }

#if DEBUG
        private static int _labelId;
#endif
        private void PushLoop(ILoopStatement node) {
#if DEBUG
            node.BreakLabel = Ast.Label("break" + _labelId++);
            node.ContinueLabel = Ast.Label("continue" + _labelId++);
#else
            node.BreakLabel = Ast.Label("break");
            node.ContinueLabel = Ast.Label("continue");
#endif
            _loops.Add(node);
        }

        private void PopLoop() {
            _loops.RemoveAt(_loops.Count - 1);
        }

        public override bool Walk(WhileStatement node) {
            node.Parent = _currentScope;

            // we only push the loop for the body of the loop
            // so we need to walk the while statement ourselves
            node.Test?.Walk(this);

            PushLoop(node);
            node.Body?.Walk(this);
            PopLoop();

            node.ElseStatement?.Walk(this);

            return false;
        }

        public override bool Walk(BreakStatement node) {
            node.Parent = _currentScope;
            node.LoopStatement = _loops[_loops.Count - 1];
            
            return base.Walk(node);
        }

        public override bool Walk(ContinueStatement node) {
            node.Parent = _currentScope;
            node.LoopStatement = _loops[_loops.Count - 1];
            
            return base.Walk(node);
        }

        public override bool Walk(ReturnStatement node) {
            node.Parent = _currentScope;
            if (_currentScope is FunctionDefinition funcDef) {
                funcDef._hasReturn = true;
            }
            return base.Walk(node);
        }

        // WithStatement
        public override bool Walk(WithStatement node) {
            node.Parent = _currentScope;
            _currentScope.ContainsExceptionHandling = true;

            if (node.Variable != null) {
                var assignError = node.Variable.CheckAssign();
                if (assignError != null) {
                    ReportSyntaxError(assignError, node);
                }
                node.Variable.Walk(_define);
            }
            return true;
        }

        // FromImportStatement
        public override bool Walk(FromImportStatement node) {
            node.Parent = _currentScope;

            if (node.Names != FromImportStatement.Star) {
                PythonVariable[] variables = new PythonVariable[node.Names.Count];
                node.Root.Parent = _currentScope;
                for (int i = 0; i < node.Names.Count; i++) {
                    string name = node.AsNames[i] ?? node.Names[i];
                    variables[i] = DefineName(name);
                }
                node.Variables = variables;
            } else {
                Debug.Assert(_currentScope != null);
                _currentScope.ContainsImportStar = true;
                _currentScope.NeedsLocalsDictionary = true;
                _currentScope.HasLateBoundVariableSets = true;
            }
            return true;
        }

        // FunctionDefinition
        public override bool Walk(FunctionDefinition node) {
            node._nameVariable = _globalScope.EnsureGlobalVariable("__name__");

            // Name is defined in the enclosing context
            if (!node.IsLambda) {
                node.PythonVariable = DefineName(node.Name);
            }

            // process the default arg values in the outer context
            foreach (Parameter p in node.Parameters) {
                p.DefaultValue?.Walk(this);
                p.Annotation?.Walk(this);
            }
            // process the decorators in the outer context
            if (node.Decorators != null) {
                foreach (Expression dec in node.Decorators) {
                    dec.Walk(this);
                }
            }

            node.ReturnAnnotation?.Walk(this);

            PushScope(node);

            foreach (Parameter p in node.Parameters) {
                p.Parent = _currentScope;
                p.PythonVariable = DefineParameter(p.Name);
            }

            node.Body.Walk(this);
            return false;
        }

        // FunctionDefinition
        public override void PostWalk(FunctionDefinition node) {
            Debug.Assert(_currentScope == node);
            PopScope();
        }

        // GlobalStatement
        public override bool Walk(GlobalStatement node) {
            node.Parent = _currentScope;

            foreach (string n in node.Names) {
                PythonVariable conflict;
                // Check current scope for conflicting variable
                bool assignedGlobal = false;
                if (_currentScope.TryGetVariable(n, out conflict)) {
                    // conflict?
                    switch (conflict.Kind) {
                        case VariableKind.Global:
                            break;

                        case VariableKind.Local:
                            assignedGlobal = true;
                            ReportSyntaxError($"name '{n}' is assigned to before global declaration", node);
                            break;

                        case VariableKind.Parameter:
                            ReportSyntaxError($"name '{n}' is parameter and global", node);
                            break;

                        case VariableKind.Nonlocal:
                            ReportSyntaxError($"name '{n}' is nonlocal and global", node);
                            break;
                    }
                }

                // Check for the name being referenced previously
                if (_currentScope.IsReferenced(n) && !assignedGlobal) {
                    ReportSyntaxError($"name '{n}' is used prior to global declaration", node);
                }

                // Create the variable in the global context or mark it as global
                PythonVariable variable = _globalScope.EnsureGlobalVariable(n);

                if (conflict == null) {
                    // no previously defined variables, add it to the current scope
                    _currentScope.AddGlobalVariable(variable);
                }
            }
            return true;
        }

        public override bool Walk(NameExpression node) {
            node.Parent = _currentScope;
            node.Reference = Reference(node.Name);
            return true;
        }

        // NonlocalStatement
        public override bool Walk(NonlocalStatement node) {
            node.Parent = _currentScope;

            if (_currentScope == _globalScope)
                ReportSyntaxError($"nonlocal declaration not allowed at module level", node);

            foreach (string n in node.Names) {
                PythonVariable conflict;
                // Check current scope for conflicting variable
                if (_currentScope.TryGetVariable(n, out conflict)) {
                    // conflict?
                    switch (conflict.Kind) {
                        case VariableKind.Global:
                            ReportSyntaxError($"name '{n}' is nonlocal and global", node);
                            break;

                        case VariableKind.Local:
                            ReportSyntaxError($"name '{n}' is assigned to before nonlocal declaration", node);
                            break;

                        case VariableKind.Parameter:
                            ReportSyntaxError($"name '{n}' is parameter and nonlocal", node);
                            break;

                        case VariableKind.Nonlocal:
                            // no conflict, name redeclared as nonlocal
                            break;
                    }
                }

                // Check for the name being referenced previously
                if (_currentScope.IsReferenced(n) && conflict is null) {
                    ReportSyntaxError($"name '{n}' is used prior to nonlocal declaration", node);
                }

                _currentScope.EnsureNonlocalVariable(n, node);
            }
            return true;
        }

        // PythonAst
        public override bool Walk(PythonAst node) {
            node.DocVariable = DefineName("__doc__");
            if (node.IsModule) {
                node.NameVariable = DefineName("__name__");
                node.PackageVariable = DefineName("__package__");
                node.SpecVariable = DefineName("__spec__");
                node.FileVariable = DefineName("__file__");

                // commonly used module variables that we want defined for optimization purposes
                DefineName("__path__");
                DefineName("__builtins__");
            }
            return true;
        }

        // PythonAst
        public override void PostWalk(PythonAst node) {
            // Do not add the global suite to the list of processed nodes,
            // the publishing must be done after the class local binding.
            Debug.Assert(_currentScope == node);
            _currentScope = _currentScope.Parent;
            _finallyCount.RemoveAt(_finallyCount.Count - 1);
        }

        // ImportStatement
        public override bool Walk(ImportStatement node) {
            node.Parent = _currentScope;

            PythonVariable[] variables = new PythonVariable[node.Names.Count];
            for (int i = 0; i < node.Names.Count; i++) {
                string name = node.AsNames[i] ?? node.Names[i].Names[0];
                variables[i] = DefineName(name);
                node.Names[i].Parent = _currentScope;
            }
            node.Variables = variables;
            return true;
        }

        // TryStatement
        public override bool Walk(TryStatement node) {
            // we manually walk the TryStatement so we can track finally blocks.
            node.Parent = _currentScope;
            _currentScope.ContainsExceptionHandling = true;

            node.Body.Walk(this);

            foreach (TryStatementHandler tsh in node.Handlers) {
                tsh.Target?.Walk(_define);
                tsh.Parent = _currentScope;
                tsh.Walk(this);
            }

            node.Else?.Walk(this);

            if (node.Finally != null) {
                _finallyCount[_finallyCount.Count - 1]++;
                node.Finally.Walk(this);
                _finallyCount[_finallyCount.Count - 1]--;
            }

            return false;
        }

        // ListComprehensionFor
        public override bool Walk(ComprehensionFor node) {
            node.Parent = _currentScope;
            node.Left.Walk(_define);
            return true;
        }

        // CallExpression
        public override bool Walk(CallExpression node) {
            node.Parent = _currentScope;

            if (node.Target is NameExpression nameExpr && nameExpr.Name == "super" && _currentScope is FunctionDefinition func) {
                _currentScope.Reference("__class__");
                if (node.Args.Count == 0 && node.Kwargs.Count == 0 && func.ParameterNames.Length > 0) {
                    node.SetImplicitArgs(new NameExpression("__class__"), new NameExpression(func.ParameterNames[0]));
                }
            }
            return base.Walk(node);
        }

        #endregion
    }
}
