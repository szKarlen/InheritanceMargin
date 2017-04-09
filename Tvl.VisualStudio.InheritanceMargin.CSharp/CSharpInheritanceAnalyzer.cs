﻿// Copyright (c) Tunnel Vision Laboratories, LLC. All Rights Reserved.
// Licensed under the MIT License. See LICENSE.txt in the project root for license information.

namespace Tvl.VisualStudio.InheritanceMargin.CSharp
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.FindSymbols;
    using Microsoft.CodeAnalysis.Text;
    using Microsoft.VisualStudio.LanguageServices;
    using Microsoft.VisualStudio.Text;
    using Microsoft.VisualStudio.Text.Tagging;
    using Tvl.VisualStudio.OutputWindow.Interfaces;
    using Stopwatch = System.Diagnostics.Stopwatch;
    using StringBuilder = System.Text.StringBuilder;
    using SVsServiceProvider = Microsoft.VisualStudio.Shell.SVsServiceProvider;

    internal class CSharpInheritanceAnalyzer : BackgroundParser
    {
        private static readonly Lazy<Type> DependentTypeFinder = new Lazy<Type>(() => typeof(SymbolFinder).Assembly.GetType("Microsoft.CodeAnalysis.FindSymbols.DependentTypeFinder"));

        private static readonly Lazy<Func<INamedTypeSymbol, Solution, IImmutableSet<Project>, CancellationToken, Task<IEnumerable<INamedTypeSymbol>>>> FindDerivedClassesAsync =
            new Lazy<Func<INamedTypeSymbol, Solution, IImmutableSet<Project>, CancellationToken, Task<IEnumerable<INamedTypeSymbol>>>>(() =>
            {
#if ROSLYN2
                return SymbolFinder.FindDerivedClassesAsync;
#else
                // Assume we are using Roslyn 1.3...
                var methodInfo = DependentTypeFinder.Value.GetMethod(
                    "FindTransitivelyDerivedClassesAsync",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(INamedTypeSymbol), typeof(Solution), typeof(IImmutableSet<Project>), typeof(CancellationToken) },
                    null);

                if (methodInfo == null)
                {
                    // If we are wrong, try the Roslyn 1.0-1.2 method
                    methodInfo = DependentTypeFinder.Value.GetMethod(
                        "FindDerivedClassesAsync",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new[] { typeof(INamedTypeSymbol), typeof(Solution), typeof(IImmutableSet<Project>), typeof(CancellationToken) },
                        null);
                }

                return (Func<INamedTypeSymbol, Solution, IImmutableSet<Project>, CancellationToken, Task<IEnumerable<INamedTypeSymbol>>>)Delegate.CreateDelegate(typeof(Func<INamedTypeSymbol, Solution, IImmutableSet<Project>, CancellationToken, Task<IEnumerable<INamedTypeSymbol>>>), methodInfo);
#endif
            });

        // Roslyn 1.0-1.1
        private static readonly Lazy<MethodInfo> FindDerivedInterfacesAsyncMethodInfo =
            new Lazy<MethodInfo>(() => DependentTypeFinder.Value.GetMethod(
                "FindDerivedInterfacesAsync",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(INamedTypeSymbol), typeof(Solution), typeof(IImmutableSet<Project>), typeof(CancellationToken) },
                null));

        // Roslyn 1.2
        private static readonly Lazy<MethodInfo> GetTypesImmediatelyDerivedFromInterfacesAsyncMethodInfo =
            new Lazy<MethodInfo>(() => DependentTypeFinder.Value.GetMethod(
                "GetTypesImmediatelyDerivedFromInterfacesAsync",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(INamedTypeSymbol), typeof(Solution), typeof(CancellationToken) },
                null));

        // Roslyn 1.3
        private static readonly Lazy<MethodInfo> FindImmediatelyDerivedAndImplementingTypesAsyncMethodInfo =
            new Lazy<MethodInfo>(() => DependentTypeFinder.Value.GetMethod(
                "FindImmediatelyDerivedAndImplementingTypesAsync",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(INamedTypeSymbol), typeof(Solution), typeof(CancellationToken) },
                null));

        /// <summary>
        /// Prior to Roslyn 1.2, <c>DependentTypeFinder.FindDerivedInterfacesAsync</c> can be used directly. In Roslyn
        /// 1.2, <c>DependentTypeFinder.GetTypesImmediatelyDerivedFromInterfacesAsync</c> must be used instead. In
        /// Roslyn 1.3, <c>DependentTypeFinder.FindImmediatelyDerivedAndImplementingTypesAsync</c> must be used instead.
        /// </summary>
        private static readonly Lazy<Func<INamedTypeSymbol, Solution, IImmutableSet<Project>, CancellationToken, Task<IEnumerable<INamedTypeSymbol>>>> FindDerivedInterfacesAsync =
            new Lazy<Func<INamedTypeSymbol, Solution, IImmutableSet<Project>, CancellationToken, Task<IEnumerable<INamedTypeSymbol>>>>(() =>
            {
#if ROSLYN2
                return (symbol, solution, projects, cancellationToken) => Task.FromResult(Enumerable.Empty<INamedTypeSymbol>());
#else
                MethodInfo method = FindDerivedInterfacesAsyncMethodInfo.Value;
                if (method == null)
                {
                    method = GetTypesImmediatelyDerivedFromInterfacesAsyncMethodInfo.Value;
                    if (method == null)
                    {
                        method = FindImmediatelyDerivedAndImplementingTypesAsyncMethodInfo.Value;
                    }

                    var immediatelyDerived = (Func<INamedTypeSymbol, Solution, CancellationToken, Task<IEnumerable<INamedTypeSymbol>>>)Delegate.CreateDelegate(typeof(Func<INamedTypeSymbol, Solution, CancellationToken, Task<IEnumerable<INamedTypeSymbol>>>), method);
                    return (symbol, solution, projects, cancellationToken) => GetDerivedInterfacesFromImmediatelyDerivedAsync(symbol, solution, projects, immediatelyDerived, cancellationToken);
                }

                return (Func<INamedTypeSymbol, Solution, IImmutableSet<Project>, CancellationToken, Task<IEnumerable<INamedTypeSymbol>>>)Delegate.CreateDelegate(typeof(Func<INamedTypeSymbol, Solution, IImmutableSet<Project>, CancellationToken, Task<IEnumerable<INamedTypeSymbol>>>), method);
#endif
            });

        private static readonly Lazy<Func<INamedTypeSymbol, Solution, IImmutableSet<Project>, CancellationToken, Task<IEnumerable<INamedTypeSymbol>>>> FindImplementingTypesAsync =
            new Lazy<Func<INamedTypeSymbol, Solution, IImmutableSet<Project>, CancellationToken, Task<IEnumerable<INamedTypeSymbol>>>>(() =>
            {
#if ROSLYN2
                return async (symbol, solution, projects, cancellationToken) =>
                {
                    var symbols = await SymbolFinder.FindImplementationsAsync(symbol, solution, projects, cancellationToken).ConfigureAwait(false);
                    return symbols.OfType<INamedTypeSymbol>();
                };
#else
                // Assume we are using Roslyn 1.3...
                var methodInfo = DependentTypeFinder.Value.GetMethod(
                    "FindTransitivelyImplementingTypesAsync",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(INamedTypeSymbol), typeof(Solution), typeof(IImmutableSet<Project>), typeof(CancellationToken) },
                    null);

                if (methodInfo == null)
                {
                    // If we are wrong, try the Roslyn 1.0-1.2 method
                    methodInfo = DependentTypeFinder.Value.GetMethod(
                        "FindImplementingTypesAsync",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new[] { typeof(INamedTypeSymbol), typeof(Solution), typeof(IImmutableSet<Project>), typeof(CancellationToken) },
                        null);
                }

                return (Func<INamedTypeSymbol, Solution, IImmutableSet<Project>, CancellationToken, Task<IEnumerable<INamedTypeSymbol>>>)Delegate.CreateDelegate(typeof(Func<INamedTypeSymbol, Solution, IImmutableSet<Project>, CancellationToken, Task<IEnumerable<INamedTypeSymbol>>>), methodInfo);
#endif
            });

        private readonly SVsServiceProvider _serviceProvider;
        private readonly IInheritanceTagFactory _tagFactory;

        public CSharpInheritanceAnalyzer(ITextBuffer textBuffer, TaskScheduler taskScheduler, ITextDocumentFactoryService textDocumentFactoryService, IOutputWindowService outputWindowService, SVsServiceProvider serviceProvider, IInheritanceTagFactory tagFactory)
            : base(textBuffer, taskScheduler, textDocumentFactoryService, outputWindowService)
        {
            if (serviceProvider == null)
                throw new ArgumentNullException("serviceProvider");

            _serviceProvider = serviceProvider;
            _tagFactory = tagFactory;
        }

        /// <inheritdoc/>
        public override string Name
        {
            get
            {
                return "C# Inheritance Analyzer";
            }
        }

        public static void NavigateToSymbol(SourceTextContainer textContainer, ISymbol symbol, Project project)
        {
            Workspace workspace;
            if (!Workspace.TryGetWorkspace(textContainer, out workspace))
                return;

            VisualStudioWorkspace visualStudioWorkspace = workspace as VisualStudioWorkspace;
            if (visualStudioWorkspace == null)
                return;

            visualStudioWorkspace.TryGoToDefinition(symbol, project, CancellationToken.None);
        }

        private static async Task<IEnumerable<INamedTypeSymbol>> GetDerivedInterfacesFromImmediatelyDerivedAsync(
            INamedTypeSymbol type,
            Solution solution,
            IImmutableSet<Project> projects,
            Func<INamedTypeSymbol, Solution, CancellationToken, Task<IEnumerable<INamedTypeSymbol>>> immediatelyDerivedAsync,
            CancellationToken cancellationToken)
        {
            if (type.TypeKind != TypeKind.Interface)
            {
                return Enumerable.Empty<INamedTypeSymbol>();
            }

            type = type.OriginalDefinition;

            var result = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
            HashSet<INamedTypeSymbol> visited = new HashSet<INamedTypeSymbol>();
            visited.Add(type);
            Queue<INamedTypeSymbol> workList = new Queue<INamedTypeSymbol>();
            workList.Enqueue(type);
            while (workList.Count > 0)
            {
                INamedTypeSymbol current = workList.Dequeue();
                foreach (INamedTypeSymbol derived in await immediatelyDerivedAsync(current, solution, cancellationToken).ConfigureAwait(false))
                {
                    if (derived.TypeKind != TypeKind.Interface)
                        continue;

                    INamedTypeSymbol original = derived.OriginalDefinition;
                    if (!visited.Add(original))
                        continue;

                    result.Add(original);
                    workList.Enqueue(original);
                }
            }

            return result.ToImmutable();
        }

        /// <inheritdoc/>
        protected override void ReParseImpl()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            ITextSnapshot snapshot = TextBuffer.CurrentSnapshot;

            try
            {
                ITextDocument textDocument = TextDocument;
                string fileName = textDocument != null ? textDocument.FilePath : null;
                Document document = snapshot.GetOpenDocumentInCurrentContextWithChanges();
                SourceTextContainer textContainer = document != null ? document.GetTextAsync().Result.Container : null;
                Project project = document != null ? document.Project : null;
                Solution solution = project.Solution;

                List<ITagSpan<IInheritanceTag>> tags = new List<ITagSpan<IInheritanceTag>>();

                if (document != null && !string.IsNullOrEmpty(fileName))
                {
                    SyntaxTree syntaxTree = document.GetSyntaxTreeAsync().Result;
                    SyntaxNode syntaxRoot = syntaxTree.GetRoot();
                    SemanticModel semanticModel = document.GetSemanticModelAsync().Result;
                    Compilation compilation = semanticModel.Compilation;

                    IDictionary<ISymbol, ISet<ISymbol>> interfaceImplementations = new Dictionary<ISymbol, ISet<ISymbol>>();

                    List<CSharpSyntaxNode> allMembers = new List<CSharpSyntaxNode>();
                    IEnumerable<BaseTypeDeclarationSyntax> typeNodes = syntaxRoot.DescendantNodes().OfType<BaseTypeDeclarationSyntax>();
                    foreach (var typeNode in typeNodes)
                    {
                        ISymbol symbol = semanticModel.GetDeclaredSymbol(typeNode);
                        if (symbol == null)
                        {
                            MarkDirty(true);
                            return;
                        }

                        INamedTypeSymbol typeSymbol = symbol as INamedTypeSymbol;
                        if (typeSymbol == null)
                            continue;

                        // get implemented interface symbols
                        foreach (INamedTypeSymbol namedTypeSymbol in typeSymbol.AllInterfaces)
                        {
                            foreach (ISymbol member in namedTypeSymbol.GetMembers())
                            {
                                ISymbol implementation = typeSymbol.FindImplementationForInterfaceMember(member);
                                if (implementation == null || !implementation.ContainingSymbol.Equals(typeSymbol))
                                    continue;

                                ISet<ISymbol> symbols;
                                if (!interfaceImplementations.TryGetValue(implementation, out symbols))
                                {
                                    symbols = new HashSet<ISymbol>();
                                    interfaceImplementations[implementation] = symbols;
                                }

                                symbols.Add(member);
                            }
                        }

                        TypeDeclarationSyntax typeDeclarationSyntax = typeNode as TypeDeclarationSyntax;
                        if (typeDeclarationSyntax != null)
                            allMembers.AddRange(typeDeclarationSyntax.Members);

                        if (typeSymbol.IsSealed)
                            continue;

                        // types which implement or derive from this type
                        ISet<ITypeSymbol> derivedTypes = new HashSet<ITypeSymbol>();
                        derivedTypes.UnionWith(FindDerivedClassesAsync.Value(typeSymbol, solution, null, CancellationToken.None).Result);
                        derivedTypes.UnionWith(FindDerivedInterfacesAsync.Value(typeSymbol, solution, null, CancellationToken.None).Result);
                        derivedTypes.UnionWith(FindImplementingTypesAsync.Value(typeSymbol, solution, null, CancellationToken.None).Result);

                        if (derivedTypes.Count == 0)
                            continue;

                        StringBuilder builder = new StringBuilder();
                        string elementKindDisplayName =
                            "types";

                        builder.AppendLine("Derived " + elementKindDisplayName + ":");
                        foreach (var derived in derivedTypes)
                            builder.AppendLine("    " + derived.ToString());

                        SyntaxToken identifier = typeNode.Accept(IdentifierSyntaxVisitor.Instance);
                        SnapshotSpan span = new SnapshotSpan(snapshot, new Span(identifier.SpanStart, identifier.Span.Length));

                        InheritanceGlyph tag = typeSymbol.TypeKind == TypeKind.Interface ? InheritanceGlyph.HasImplementations : InheritanceGlyph.Overridden;

                        var targets = derivedTypes.Select(i => new TypeTarget(textContainer, i, solution));
                        tags.Add(new TagSpan<IInheritanceTag>(span, _tagFactory.CreateTag(tag, builder.ToString().TrimEnd(), targets)));
                    }

                    foreach (var eventFieldDeclarationSyntax in allMembers.OfType<EventFieldDeclarationSyntax>().ToArray())
                        allMembers.AddRange(eventFieldDeclarationSyntax.Declaration.Variables);

                    foreach (CSharpSyntaxNode memberNode in allMembers)
                    {
                        if (!(memberNode is MethodDeclarationSyntax)
                            && !(memberNode is PropertyDeclarationSyntax)
                            && !(memberNode is EventDeclarationSyntax)
                            && !(memberNode is VariableDeclaratorSyntax))
                        {
                            continue;
                        }

                        ISymbol symbol = semanticModel.GetDeclaredSymbol(memberNode);
                        if (symbol == null)
                        {
                            MarkDirty(true);
                            return;
                        }

                        // members which this member implements
                        ISet<ISymbol> implementedMethods = new HashSet<ISymbol>();
                        if (!interfaceImplementations.TryGetValue(symbol, out implementedMethods))
                            implementedMethods = new HashSet<ISymbol>();

                        ISet<ISymbol> overriddenMethods = new HashSet<ISymbol>();

                        IMethodSymbol methodSymbol = symbol as IMethodSymbol;
                        if (methodSymbol != null)
                        {
                            // methods which this method overrides
                            for (IMethodSymbol current = methodSymbol.OverriddenMethod;
                            current != null;
                            current = current.OverriddenMethod)
                            {
                                overriddenMethods.Add(current);
                            }
                        }
                        else
                        {
                            IPropertySymbol propertySymbol = symbol as IPropertySymbol;
                            if (propertySymbol != null)
                            {
                                // properties which this property overrides
                                for (IPropertySymbol current = propertySymbol.OverriddenProperty;
                                current != null;
                                current = current.OverriddenProperty)
                                {
                                    overriddenMethods.Add(current);
                                }
                            }
                            else
                            {
                                IEventSymbol eventSymbol = symbol as IEventSymbol;
                                if (eventSymbol != null)
                                {
                                    // events which this event overrides
                                    for (IEventSymbol current = eventSymbol.OverriddenEvent;
                                    current != null;
                                    current = current.OverriddenEvent)
                                    {
                                        overriddenMethods.Add(current);
                                    }
                                }
                            }
                        }

                        ISet<ISymbol> implementingMethods = new HashSet<ISymbol>(SymbolFinder.FindImplementationsAsync(symbol, solution).Result);

                        ISet<ISymbol> overridingMethods = new HashSet<ISymbol>(SymbolFinder.FindOverridesAsync(symbol, solution).Result);

                        if (implementingMethods.Count == 0 && implementedMethods.Count == 0 && overriddenMethods.Count == 0 && overridingMethods.Count == 0)
                            continue;

                        StringBuilder builder = new StringBuilder();
                        string elementKindDisplayName =
                            symbol is IPropertySymbol ? "properties" :
                            symbol is IEventSymbol ? "events" :
                            "methods";

                        if (implementedMethods.Count > 0)
                        {
                            builder.AppendLine("Implemented " + elementKindDisplayName + ":");
                            foreach (var methodId in implementedMethods)
                                builder.AppendLine("    " + methodId.ToString());
                        }

                        if (overriddenMethods.Count > 0)
                        {
                            builder.AppendLine("Overridden " + elementKindDisplayName + ":");
                            foreach (var methodId in overriddenMethods)
                                builder.AppendLine("    " + methodId.ToString());
                        }

                        if (implementingMethods.Count > 0)
                        {
                            builder.AppendLine("Implementing " + elementKindDisplayName + " in derived types:");
                            foreach (var methodId in implementingMethods)
                                builder.AppendLine("    " + methodId.ToString());
                        }

                        if (overridingMethods.Count > 0)
                        {
                            builder.AppendLine("Overriding " + elementKindDisplayName + " in derived types:");
                            foreach (var methodId in overridingMethods)
                                builder.AppendLine("    " + methodId.ToString());
                        }

                        SyntaxToken identifier = memberNode.Accept(IdentifierSyntaxVisitor.Instance);
                        SnapshotSpan span = new SnapshotSpan(snapshot, new Span(identifier.SpanStart, identifier.Span.Length));

                        InheritanceGlyph tag;
                        if (implementedMethods.Count > 0)
                        {
                            if (overridingMethods.Count > 0)
                                tag = InheritanceGlyph.ImplementsAndOverridden;
                            else if (implementingMethods.Count > 0)
                                tag = InheritanceGlyph.ImplementsAndHasImplementations;
                            else
                                tag = InheritanceGlyph.Implements;
                        }
                        else if (implementingMethods.Count > 0)
                        {
                            tag = InheritanceGlyph.HasImplementations;
                        }
                        else if (overriddenMethods.Count > 0)
                        {
                            if (overridingMethods.Count > 0)
                                tag = InheritanceGlyph.OverridesAndOverridden;
                            else
                                tag = InheritanceGlyph.Overrides;
                        }
                        else
                        {
                            tag = InheritanceGlyph.Overridden;
                        }

                        List<ISymbol> members = new List<ISymbol>();
                        members.AddRange(implementedMethods);
                        members.AddRange(overriddenMethods);
                        members.AddRange(implementingMethods);
                        members.AddRange(overridingMethods);

                        var targets = members.Select(i => new MemberTarget(textContainer, i, solution));
                        tags.Add(new TagSpan<IInheritanceTag>(span, _tagFactory.CreateTag(tag, builder.ToString().TrimEnd(), targets)));
                    }
                }

                InheritanceParseResultEventArgs result = new InheritanceParseResultEventArgs(snapshot, stopwatch.Elapsed, tags);
                OnParseComplete(result);
            }
            catch (InvalidOperationException)
            {
                MarkDirty(true);
                throw;
            }
        }

        private class IdentifierSyntaxVisitor : CSharpSyntaxVisitor<SyntaxToken>
        {
            public static readonly IdentifierSyntaxVisitor Instance = new IdentifierSyntaxVisitor();

            private IdentifierSyntaxVisitor()
            {
            }

            public override SyntaxToken VisitClassDeclaration(ClassDeclarationSyntax node)
            {
                return node.Identifier;
            }

            public override SyntaxToken VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
            {
                return node.Identifier;
            }

            public override SyntaxToken VisitDelegateDeclaration(DelegateDeclarationSyntax node)
            {
                return node.Identifier;
            }

            public override SyntaxToken VisitDestructorDeclaration(DestructorDeclarationSyntax node)
            {
                return node.Identifier;
            }

            public override SyntaxToken VisitEnumDeclaration(EnumDeclarationSyntax node)
            {
                return node.Identifier;
            }

            public override SyntaxToken VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node)
            {
                return node.Identifier;
            }

            public override SyntaxToken VisitEventDeclaration(EventDeclarationSyntax node)
            {
                return node.Identifier;
            }

            public override SyntaxToken VisitIndexerDeclaration(IndexerDeclarationSyntax node)
            {
                return node.ThisKeyword;
            }

            public override SyntaxToken VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
            {
                return node.Identifier;
            }

            public override SyntaxToken VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                return node.Identifier;
            }

            public override SyntaxToken VisitOperatorDeclaration(OperatorDeclarationSyntax node)
            {
                return node.OperatorToken;
            }

            public override SyntaxToken VisitPropertyDeclaration(PropertyDeclarationSyntax node)
            {
                return node.Identifier;
            }

            public override SyntaxToken VisitStructDeclaration(StructDeclarationSyntax node)
            {
                return node.Identifier;
            }

            public override SyntaxToken VisitVariableDeclarator(VariableDeclaratorSyntax node)
            {
                return node.Identifier;
            }
        }
    }
}
