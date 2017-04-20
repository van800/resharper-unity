﻿using System.Collections.Generic;
using System.Linq;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.ExtensionsAPI.Resolve;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Util;

namespace JetBrains.ReSharper.Plugins.Unity.Psi.Resolve
{
    public class UnityEventFunctionReferenceFactory : StringLiteralReferenceFactoryBase
    {
        private readonly IPredefinedTypeCache myPredefinedTypeCache;

        private static readonly HashSet<string> InvokeMethodNames = new HashSet<string>
        {
            "Invoke", "InvokeRepeating", "CancelInvoke", "IsInvoking"
        };

        public UnityEventFunctionReferenceFactory(IPredefinedTypeCache predefinedTypeCache)
        {
            myPredefinedTypeCache = predefinedTypeCache;
        }

        public override ReferenceCollection GetReferences(ITreeNode element, ReferenceCollection oldReferences)
        {
            if (ResolveUtil.CheckThatAllReferencesBelongToElement<UnityEventFunctionReference>(oldReferences, element))
            {
                return oldReferences;
            }

            var literal = element as ILiteralExpression;
            if (literal == null || !literal.ConstantValue.IsString())
                return ReferenceCollection.Empty;

            if (!IsStringLiteralFirstArgument(literal))
                return ReferenceCollection.Empty;

            var invocationExpression = literal.GetContainingNode<IInvocationExpression>();
            var invocationReference = invocationExpression?.Reference;
            var invokedMethod = invocationReference?.Resolve().DeclaredElement as IMethod;
            if (invokedMethod == null)
                return ReferenceCollection.Empty;

            var isInvokedFunction = InvokeMethodNames.Contains(invokedMethod.ShortName);
            var isCoroutine = IsCoroutine(invokedMethod);

            if (isInvokedFunction || isCoroutine)
            {
                var containingType = invokedMethod.GetContainingType();
                if (containingType != null && Equals(containingType.GetClrName(), KnownTypes.MonoBehaviour))
                {
                    var targetType = invocationExpression.ExtensionQualifier?.GetExpressionType()
                                         .ToIType()?.GetTypeElement()
                                     ??
                                     literal.GetContainingNode<IMethodDeclaration>()?
                                         .DeclaredElement?.GetContainingType();

                    if (targetType != null)
                    {
                        var methodSignature = GetMethodSignature(invocationExpression, invokedMethod, isCoroutine);
                        var reference = new UnityEventFunctionReference(targetType, literal, methodSignature);
                        return new ReferenceCollection(reference);
                    }
                }
            }

            return ReferenceCollection.Empty;
        }

        private static bool IsStringLiteralFirstArgument(ILiteralExpression literal)
        {
            var argument = CSharpArgumentNavigator.GetByValue(literal as ICSharpExpression);
            var argumentsOwner = CSharpArgumentsOwnerNavigator.GetByArgument(argument);
            return argumentsOwner != null && argumentsOwner.ArgumentsEnumerable.FirstOrDefault() == argument;
        }

        private static bool IsCoroutine(IMethod invokedMethod)
        {
            return invokedMethod.ShortName == "StartCoroutine" || invokedMethod.ShortName == "StopCoroutine";
        }

        private MethodSignature GetMethodSignature(IInvocationExpression invocationExpression, IMethod invokedMethod, bool isCoroutine)
        {
            var predefinedType = myPredefinedTypeCache.GetOrCreatePredefinedType(invocationExpression.GetPsiModule());
            var returnType = isCoroutine ? predefinedType.IEnumerator : predefinedType.Void;

            if (invokedMethod.ShortName == "StartCoroutine")
            {
                var arguments = invocationExpression.ArgumentList.Arguments;
                if (arguments.Count == 2)
                {
                    var argumentValue = arguments[1].Value;
                    if (argumentValue != null)
                    {
                        var argumentType = argumentValue.Type();
                        return new MethodSignature(returnType, argumentType);
                    }
                }
            }

            return new MethodSignature(returnType);
        }
    }
}