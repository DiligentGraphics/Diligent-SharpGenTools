﻿using Microsoft.CodeAnalysis.CSharp;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharpGen.Model;
using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;

namespace SharpGen.Generator
{
    class NativeInvocationCodeGenerator : ICodeGenerator<CsCallable, ExpressionSyntax>
    {
        public NativeInvocationCodeGenerator(IGeneratorRegistry generators, GlobalNamespaceProvider globalNamespace)
        {
            Generators = generators;
            this.globalNamespace = globalNamespace;
        }

        readonly GlobalNamespaceProvider globalNamespace;

        public IGeneratorRegistry Generators { get; }

        private static ExpressionSyntax GetCastedReturn(ExpressionSyntax invocation, CsReturnValue returnValue)
        {
            var fundamentalPublic = returnValue.PublicType as CsFundamentalType;

            if (fundamentalPublic?.Type == typeof(bool))
                return BinaryExpression(SyntaxKind.NotEqualsExpression,
                    LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0)),
                    invocation);
            if (returnValue.PublicType is CsInterface)
                return ObjectCreationExpression(ParseTypeName(returnValue.PublicType.QualifiedName),
                    ArgumentList(
                        SingletonSeparatedList(
                            Argument(
                                CastExpression(QualifiedName(IdentifierName("System"), IdentifierName("IntPtr")), invocation)))),
                    InitializerExpression(SyntaxKind.ObjectInitializerExpression));
            if (fundamentalPublic?.Type == typeof(string))
            {
                var marshalMethodName = "PtrToString" + (returnValue.IsWideChar ? "Uni" : "Ansi");
                return InvocationExpression(
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                        ParseTypeName("System.Runtime.InteropServices.Marshal"), IdentifierName(marshalMethodName)),
                        ArgumentList(
                            SingletonSeparatedList(
                                Argument(
                                    invocation
                                    ))));
            }
            return invocation;
        }
        
        public ExpressionSyntax GenerateCode(CsCallable callable)
        {
            var arguments = new List<ArgumentSyntax>();

            if (!(callable is CsFunction))
            {
                arguments.Add(Argument(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                            ThisExpression(),
                                            IdentifierName("_nativePointer"))));
            }

            if (callable.IsReturnStructLarge)
            {
                arguments.Add(Argument(CastExpression(PointerType(PredefinedType(Token(SyntaxKind.VoidKeyword))),
                                        PrefixUnaryExpression(SyntaxKind.AddressOfExpression,
                                            IdentifierName("__result__")))));
            }

            arguments.AddRange(callable.Parameters.Select(param => Generators.Argument.GenerateCode(param)));

            if (callable is CsMethod method)
            {
                arguments.Add(Argument(
                    ElementAccessExpression(
                        ParenthesizedExpression(
                            PrefixUnaryExpression(SyntaxKind.PointerIndirectionExpression,
                                CastExpression(PointerType(PointerType(PointerType(PredefinedType(Token(SyntaxKind.VoidKeyword))))),
                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        ThisExpression(),
                                        IdentifierName("_nativePointer"))))),
                        BracketedArgumentList(
                            SingletonSeparatedList(
                                Argument(method.CustomVtbl ?
                                (ExpressionSyntax)MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                    ThisExpression(),
                                    IdentifierName($"{callable.Name}__vtbl_index"))
                                : LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(method.Offset))
                                )
                            )))));
            }

            return GetCastedReturn(
                InvocationExpression(
                    IdentifierName(callable is CsFunction ?
                        callable.CppElementName + "_"
                    : callable.GetParent<CsAssembly>().QualifiedName + ".LocalInterop." + callable.Interop.Name),
                    ArgumentList(SeparatedList(arguments))),
                callable.ReturnValue
            );
        }

    }
}
