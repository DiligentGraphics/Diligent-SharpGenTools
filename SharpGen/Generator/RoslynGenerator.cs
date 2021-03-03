﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharpGen.Logging;
using SharpGen.Model;
using SharpGen.Transform;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace SharpGen.Generator
{
    public sealed class RoslynGenerator
    {
        private const string AutoGeneratedCommentText = "// <auto-generated/>\n";

        private readonly IGeneratorRegistry generators;
        private readonly Logger logger;

        public RoslynGenerator(Logger logger, GlobalNamespaceProvider globalNamespace, IDocumentationLinker documentation, ExternalDocCommentsReader docReader, GeneratorConfig config)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            generators = new DefaultGenerators(globalNamespace, documentation, docReader, config, logger);
        }

        public void Run(CsAssembly csAssembly, string generatedCodeFolder)
        {
            if (string.IsNullOrEmpty(generatedCodeFolder))
                throw new ArgumentException("Value cannot be null or empty.", nameof(generatedCodeFolder));

            var directoryToCreate = new HashSet<string>(StringComparer.CurrentCulture);

            // Remove the generated directory before creating it
            if (!directoryToCreate.Contains(generatedCodeFolder))
            {
                directoryToCreate.Add(generatedCodeFolder);
                if (Directory.Exists(generatedCodeFolder))
                {
                    foreach (var oldGeneratedFile in Directory.EnumerateFiles(generatedCodeFolder, "*.cs", SearchOption.AllDirectories))
                    {
                        try
                        {
                            File.Delete(oldGeneratedFile);
                        }
                        catch
                        {
                            // ignored
                        }
                    }
                }
            }

            if (!Directory.Exists(generatedCodeFolder))
                Directory.CreateDirectory(generatedCodeFolder);

            logger.Message("Process Assembly {0} => {1}", csAssembly.Name, generatedCodeFolder);

            var trees = new[]
            {
                CreateTree("Enumerations", ns => ns.Enums, generators.Enum),
                CreateTree("Structures", ns => ns.Structs, generators.Struct),
                CreateTree("Functions", ns => ns.Classes, generators.Group),
                CreateTree("Interfaces", ns => ns.Interfaces, generators.Interface)
            };

            SyntaxTree CreateTree<T>(string fileName, Func<CsNamespace, IEnumerable<T>> membersFunc,
                                     IMultiCodeGenerator<T, MemberDeclarationSyntax> generator) where T : CsBase =>
                CSharpSyntaxTree.Create(
                    GenerateCompilationUnit(
                        csAssembly.Namespaces.Select(
                            ns => GenerateNamespaceDeclaration(ns, membersFunc(ns), generator)
                        )
                    ),
                    path: Path.Combine(generatedCodeFolder, $"{fileName}.cs")
                );

            foreach (var tree in trees)
                File.WriteAllText(tree.FilePath, tree.GetCompilationUnitRoot().ToFullString());
        }

        private static CompilationUnitSyntax GenerateCompilationUnit(
            IEnumerable<NamespaceDeclarationSyntax> namespaceDeclarations
        ) => CompilationUnit(
            default,
            default,
            default,
            List<MemberDeclarationSyntax>(
                namespaceDeclarations
            )
        ).NormalizeWhitespace(elasticTrivia: true);

        private static NamespaceDeclarationSyntax GenerateNamespaceDeclaration<T>(
            CsBase csNamespace, IEnumerable<T> elements, IMultiCodeGenerator<T, MemberDeclarationSyntax> generator
        ) where T : CsBase => NamespaceDeclaration(
            ParseName(csNamespace.Name),
            default,
            default,
            List(elements.OrderBy(element => element.Name).SelectMany(generator.GenerateCode))
        ).WithLeadingTrivia(Comment(AutoGeneratedCommentText));
    }
}
