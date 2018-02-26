// ******************************************************************
// Copyright � 2015-2018 nventive inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// ******************************************************************
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Uno.CodeGen.ClassLifecycle.Utils;
using Uno.SourceGeneration;
using ExpressionStatementSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionStatementSyntax;
using IdentifierNameSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax;
using InvocationExpressionSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax;
using StatementSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax;

namespace Uno.CodeGen.ClassLifecycle
{
    public class ClassLifecycleGenerator : SourceGenerator
	{
		private INamedTypeSymbol _constructorAttribute;
		private INamedTypeSymbol _disposeAttribute;
		private INamedTypeSymbol _finalizerAttribute;
		private ISymbol _iDisposable_Dispose;
		private ISymbol _extentibleDisposable_RegisterExtension;
		private SourceGeneratorContext _context;

		/// <inheritdoc />
		public override void Execute(SourceGeneratorContext context)
		{
			_context = context;

			_constructorAttribute = context.Compilation.GetTypeByMetadataName("Uno.ConstructorMethodAttribute");
			_disposeAttribute = context.Compilation.GetTypeByMetadataName("Uno.DisposeMethodAttribute");
			_finalizerAttribute = context.Compilation.GetTypeByMetadataName("Uno.FinalizerMethodAttribute");
			
			_iDisposable_Dispose = context.Compilation.GetTypeByMetadataName("System.IDisposable").GetMembers("Dispose").Single();
			_extentibleDisposable_RegisterExtension = context.Compilation.GetTypeByMetadataName("Uno.Disposables.IExtensibleDisposable").GetMembers("RegisterExtension").SingleOrDefault();

			foreach (var methods in GetLifecycleMethods())
			{
				Generate(methods);
			}
		}

		private IEnumerable<LifecycleMethods> GetLifecycleMethods()
		{
			var allTypes = _context
				.Compilation
				.SourceModule
				.GlobalNamespace
				.GetNamedTypes()
				.Select(type =>
				{
					var methods = type.GetMembers().OfType<IMethodSymbol>().ToList();

					return new LifecycleMethods(
						type,
						methods,
						methods.Where(method => method.HasAttribute(_constructorAttribute)).ToList(),
						methods.Where(method => method.HasAttribute(_disposeAttribute)).ToList(),
						methods.Where(method => method.HasAttribute(_finalizerAttribute)).ToList());
				})
				.Where(methods => methods.HasLifecycleMethods)
				.ToImmutableDictionary(methods => methods.Owner);

			foreach (var methodsDefinition in allTypes.Values)
			{
				methodsDefinition.Bases = GetBaseMethods(methodsDefinition.Owner.BaseType);
			}

			IEnumerable<LifecycleMethods> GetBaseMethods(INamedTypeSymbol type)
			{
				while (type != null && type.SpecialType != SpecialType.System_Object)
				{
					if (allTypes.TryGetValue(type, out var methods))
					{
						yield return methods;
					}

					type = type.BaseType;
				}
			}

			return allTypes.Values;
		}
			

		private void Generate(LifecycleMethods methods)
		{
			var names = methods.Owner.GetSymbolNames();

			var (dispose, appendToConstructor, appendToFinalizer) = methods.Disposes.Any()
				? GetDispose(names, methods)
				: (null, null, null);

			var constructor = methods.Constructors.Any() || appendToConstructor.HasValue()
				? GetConstructor(names, methods, appendToConstructor)
				: null;

			var finalize = methods.Finalizers.Any() || appendToFinalizer.HasValue()
				? GetFinalizer(names, methods, appendToFinalizer)
				: null;

			var code = new StringWriter();
			using (var writer = new IndentedTextWriter(code))
			{
				writer.WriteLine("using global::System;");

				using (writer.NameSpaceOf(methods.Owner))
				{
					using (writer.Block($"partial class {names.NameWithGenerics} {(methods.Disposes.Any() ? ": global::System.IDisposable" : "")}"))
					{
						writer.WriteLine(constructor);
						writer.WriteLine(dispose);
						writer.WriteLine(finalize);
					}
				}
			}

			_context.AddCompilationUnit(names.FilePath, code.ToString());
		}

		private string GetConstructor(SymbolNames names, LifecycleMethods methods, string appendToConstructor = null)
		{
			var declaredConstructors = methods
				.Owner
				.Constructors
				.Where(ctor => !ctor.IsImplicitlyDeclared)
				.ToList();

			var declaredParameterlessConstructor = declaredConstructors
				.FirstOrDefault(ctor => !ctor.Parameters.Any());

			var parameters = methods
				.Constructors
				.SelectMany(ctor => ctor.Parameters)
				.GroupBy(parameter => parameter.Name)
				.Select(parameter =>
				{
					var type = parameter.First().Type;
					var isTypeMismatch = parameter.Any(p => p.Type != type);

					var isOptional = parameter.All(p => p.IsOptional);
					var defaultValue = default(object);
					var isDefaultValueMismatch = false;
					if (isOptional)
					{
						defaultValue = parameter.First().ExplicitDefaultValue;
						isDefaultValueMismatch = parameter.Any(p => p.ExplicitDefaultValue?.ToString() != defaultValue?.ToString());
					}

					return new
					{
						name = parameter.Key,
						references = parameter as IEnumerable<IParameterSymbol>,

						isOptional = parameter.All(p => p.IsOptional),
						isDefaultValueMismatch = isDefaultValueMismatch,
						defaultValue = isDefaultValueMismatch ? null : defaultValue,

						isTypeMismatch = isTypeMismatch,
						type = isTypeMismatch ? null : type
					};
				})
				.ToList();

			var initializeParameters = parameters
				.Where(parameter => !parameter.isTypeMismatch && !parameter.isDefaultValueMismatch)
				.OrderBy(parameter => parameter.isOptional ? 1 : 0)
				.ThenBy(parameter => parameter.name)
				.Select(parameter => $"{parameter.type.GlobalTypeDefinitionText()} {parameter.name} {(parameter.isOptional ? $"= {parameter.defaultValue?.ToString() ?? "null"}" : "")}")
				.JoinBy(", ");

			var initializeParametersAreOptionalOrEmpty = parameters.None(p => !p.isOptional)
				&& (methods.Owner.BaseType == null
					|| methods.Owner.BaseType.SpecialType == SpecialType.System_Object
					|| methods.Owner.BaseType.Constructors.Any(ctor => ctor.Parameters.None(p => !p.IsOptional)));

			var result = string.Empty;

			// Validate that the return type each conctructors is void
			result += methods
				.Constructors
				.Where(ctor => !ctor.ReturnsVoid)
				.Select(ctor => $"\r\n#error The return type of {ctor.SimpleLocationText()} must be 'void'.")
				.JoinByEmpty();

			// Validate that each constructor declared invokes the 'Initialize' method
			result += declaredConstructors
				.Where(ctor => !InvokesInitialize(ctor))
				.Select(ctor =>
					$"\r\n#error Constructor {ctor.SimpleLocationText()} does not invoke the 'Initialize' method. " +
					"As you marked (or used some builders that marked) some method with the '[ConstructorMethod]' and since you have defined some constructors in your code, " +
					"you must invoke this method in all your constructor (either directly, or by invoking another contructor). "
					+ (initializeParametersAreOptionalOrEmpty ? "Tips: The easiet way is to invoke the parameterless constructor (i.e. add ': this()' to your constructor)" : ""))
				.JoinByEmpty();

			// Validate that all constructors parameters are compatibles
			result += parameters
				.Where(parameter => parameter.isTypeMismatch || parameter.isDefaultValueMismatch)
				.SelectMany(parameter =>
				{
					return Errors();
					IEnumerable<string> Errors()
					{
						if (parameter.isTypeMismatch)
						{
							yield return
								$"\r\n#error There is a type mismatch for the parameter named '{parameter.name}' beetween your constructor methods " +
								"(i.e. methods marked with the '[ConstructorMethod]' attribute). " +
								parameter.references.Select(p => $"It is of type '{p.Type}' in {p.ContainingSymbol.SimpleLocationText()}").JoinBy("; ");
						}
						if (parameter.isDefaultValueMismatch)
						{
							yield return
								$"\r\n#error There is a default value mismatch for the optional parameter named '{parameter.name}' beetween your constructor methods " +
								"(i.e. methods marked with the '[ConstructorMethod]' attribute). " +
								parameter.references.Select(p => $"It has default value '{p.ExplicitDefaultValue}' in {p.ContainingSymbol.SimpleLocationText()}").JoinBy("; ");
						}
					}
				})
				.JoinByEmpty();

			// Try to generate the parameterless constructor if possible
			if (declaredParameterlessConstructor == null && initializeParametersAreOptionalOrEmpty)
			{
				result += $@"
					{(declaredConstructors.Any() ? "private" : "public")} {names.NameWithGenerics}()
					{{
						Initialize();
					}}";
			}

			// Generate the initialize method
			result += $@"
				[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
				private int __lifecycleIsInitialized;

				/// <summary>
				/// Completes construction of this class.
				/// </summary>
				/// <remarks>This method MUST be invoked in the constructor of this class</remarks>
				private void Initialize({initializeParameters})
				{{
					if (global::System.Threading.Interlocked.Exchange(ref __lifecycleIsInitialized, 1) == 0)
					{{
						{appendToConstructor ?? ""}
						{methods.Constructors.Select(ctor => ctor.GetInvocationText(ctor.Parameters)).JoinBy("\r\n")}
					}}
				}}";

			return result;

			bool InvokesInitialize(ISymbol constructor)
			{
				return constructor
					.DeclaringSyntaxReferences
					.Select(reference => reference.GetSyntax())
					.OfType<ConstructorDeclarationSyntax>()
					.Any(constructorSyntax =>
					{
						// Check if any body statements is 'Initialize();'
						if (constructorSyntax.Body?.Statements.Any(IsInitializeStatement) ?? false)
						{
							return true;
						}

						// Check if any expression body is 'Initialize();'
						// The property appears to be public ... but is innacessible by code ...
						var expressionBody = constructorSyntax.GetType().GetProperty("ExpressionBody", BindingFlags.Instance | BindingFlags.Public)?.GetValue(constructorSyntax) as ArrowExpressionClauseSyntax;
						if (expressionBody?.Expression is InvocationExpressionSyntax expressionBodyInvocation && IsInitializeInvocation(expressionBodyInvocation))
						{
							return true;
						}


						// Check if constructor is invoking another constructor
						var initializer = constructorSyntax.Initializer;
						if (initializer == null)
						{
							return false;
						}

						// Check if this other constructor is invoking 'Initialize'
						var parentConstructor = _context.Compilation.GetSemanticModel(initializer.SyntaxTree).GetSymbolInfo(initializer).Symbol;
						if (parentConstructor == null)
						{
							// It is invoking the parameter less contructor we are generating !
							return declaredParameterlessConstructor == null && initializer.ArgumentList.Arguments.None();
						}
						else if (parentConstructor.ContainingSymbol != constructor.ContainingSymbol)
						{
							// Currently we don't support Intialize inheritance (the issue is that as each inheriance layer may add some parameters,
							// the base class cannot invoke a single method that is overriden by children)
							// We could allow this scenario for parameter-less Initialize(), but it would propably be more confusing.
							return false;
						}
						else
						{
							return InvokesInitialize(parentConstructor);
						}
					});
			}

			bool IsInitializeStatement(StatementSyntax syntax)
			{
				var statement = syntax as ExpressionStatementSyntax;
				var invocation = statement?.Expression as InvocationExpressionSyntax;

				return IsInitializeInvocation(invocation);
			}

			bool IsInitializeInvocation(InvocationExpressionSyntax invocation)
			{
				var identifier = invocation?.Expression as IdentifierNameSyntax;

				return identifier?.Identifier.Text == "Initialize";
			}
		}

		private (string code, string appendToConstructor, string appendToFinalizer) GetDispose(SymbolNames names, LifecycleMethods methods)
		{
			string result = string.Empty, appendToConstructor = string.Empty, appendToFinalizer = string.Empty;

			// Validate that dispose methods does not have any parameter
			result += methods
				.Disposes
				.Where(dispose => dispose.Parameters.Any())
				.Select(dispose => $"\r\n#error You cannot define any parameter on your dispose method ({dispose.SimpleLocationText()}).")
				.JoinByEmpty();

			// Validate that the return type each dispose is void
			result += methods
				.Disposes
				.Where(dispose => !dispose.ReturnsVoid)
				.Select(dispose => $"\r\n#error The return type of {dispose.SimpleLocationText()} must be 'void'.")
				.JoinByEmpty();

			var iDisposableImplementation = methods.Owner.FindImplementationForInterfaceMember(_iDisposable_Dispose);
			var iExtensibleDisposableImplementation = methods.Owner.FindImplementationForInterfaceMember(_extentibleDisposable_RegisterExtension) as IMethodSymbol;
			var (patternKind, patternMethod) = methods.Owner.GetDisposablePatternImplementation();
			var (disposeKind, disposeMethod) = methods.Owner.GetDisposableImplementation();

			// If the class inherits from another class that has [DisposeMtehod], we know that it will implement the dispose pattern
			var baseHasDisposableMethods = methods.Bases.Any(b => b.Disposes.Any());
			if (baseHasDisposableMethods)
			{
				patternKind = DisposePatternImplementationKind.DisposePatternOnBase;
			}

			// Finaliy generate the code
			if (iDisposableImplementation == null && !baseHasDisposableMethods)
			{
				ImplementIDisposable();
			}
			else if (patternKind != DisposePatternImplementationKind.None || baseHasDisposableMethods)
			{
				ExtendDisposePattern();
			}
			else if (iExtensibleDisposableImplementation != null)
			{
				AppendToExtensibleDisposable();
			}
			else if (disposeKind != DisposeImplementationKind.None)
			{
				ExtendDispose();
			}
			else
			{
				// OUPS! something went wrong: we found that there is an implementation of IDisposable (FindImplementationForInterfaceMember(_iDisposable_Dispose)),
				// but we fail to determine the kind ... fail kind of gracefully.
				result += "\r\n#error Something went wrong in the ClassLifecycle generator (failed to determine the kind of implementation of IDispose)";
			}

			return (result, appendToConstructor, appendToFinalizer);

			void ImplementIDisposable()
			{
				if (methods.Owner.IsSealed)
				{
					result += $@"
						[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
						private int __lifecycleIsDisposed;

						/// <inheritdoc cref=""global::System.IDisposable.Dispose""/>
						public void Dispose()
						{{
							if (global::System.Threading.Interlocked.Exchange(ref __lifecycleIsDisposed, 1) == 0)
							{{
								{methods.Disposes.Select(m => m.GetInvocationText()).JoinBy("\r\n")}
							}}
						}}";
				}
				else
				{
					appendToFinalizer = "Dispose(false);";
					result += $@"
						[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
						private int __lifecycleIsDisposed;

						/// <summary>
						/// Overridable method to perform application-defined tasks associated with freeing, releasing, or resetting resources.
						/// </summary>
						/// <param name=""isDisposing"" >
						/// A boolean which indicates if this is invoked by the <see cref=""gloabl::System.IDisposable.Dispose""/> (so you should release all ressources), 
						/// or by the class finalizer (so you should release only unmanaged ressources).
						/// </param>
						protected virtual void Dispose(bool isDisposing)
						{{
							if (global::System.Threading.Interlocked.Exchange(ref __lifecycleIsDisposed, 1) == 0
								&& isDisposing)
							{{
								{methods.Disposes.Select(m => m.GetInvocationText()).JoinBy("\r\n")}
							}}
						}}

						/// <inheritdoc cref=""global::System.IDisposable.Dispose""/>
						public void Dispose()
						{{
							Dispose(true);
							{(methods.Finalizers.None() ? "global::System.GC.SuppressFinalize(this);" : "")}
						}}";
				}
			}

			void ExtendDisposePattern()
			{
				switch (patternKind)
				{
					case DisposePatternImplementationKind.DisposePattern:
						result +=
							"\r\n#error Your class has some methods marked with the '[DisposeMethod]' attribute. " +
							"The 'Dispose' pattern will be generated, and you must not implement it in your code. " +
							"If you want to add some code to the dispose, rename your method and mark it with the '[DisposeMethod]' attribute. " +
							$"You get this error because you defined {patternMethod.SimpleLocationText()}.";
						break;

					case DisposePatternImplementationKind.DisposePatternOnBase:
						result += $@"
							[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
							private int __lifecycleIsDisposed;

							/// <inheritdoc />
							{patternMethod?.DeclaredAccessibility.ToString().ToLowerInvariant() ?? "protected"} override void Dispose(bool isDisposing)
							{{
								base.Dispose(isDisposing);

								if (global::System.Threading.Interlocked.Exchange(ref __lifecycleIsDisposed, 1) == 0)
								{{
									{methods.Disposes.Select(m => m.GetInvocationText()).JoinBy("\r\n")}
								}}
							}}";
						break;

					case DisposePatternImplementationKind.SealedDisposePatternOnBase:
						result +=
							"\r\n#error Your class has some methods marked with the '[DisposeMethod]' attribute, " +
							$"but the base class {patternMethod.ContainingType.Name} sealed its implementation. " +
							$"You have either to remove the inheritance, or remove the method marked with the '[DisposeMethod]' attribute.";
						break;

					case DisposePatternImplementationKind.NonOverridableDisposePatternOnBase:
						result +=
							"\r\n#error Your class has some methods marked with the '[DisposeMethod]' attribute, " +
							$"but the base class {patternMethod.ContainingType.Name} did not implemented the dispose pattern properly. " +
							$"You have either to make the Dispose(bool) method 'virtual' on {patternMethod.ContainingType.Name}, " +
							$"remove the inheritance, or remove the method marked with the '[DisposeMethod]' attribute.";
						break;

					default:
						throw new NotSupportedException($"Invalid dispose pattern kind : '{patternKind}'");
				}
			}

			void AppendToExtensibleDisposable()
			{
				appendToConstructor = $"{iExtensibleDisposableImplementation.GetInvocationText(parameters: "new __LifecycleDisposables(this)")}";
				result += $@"
					[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
					private class __LifecycleDisposables : global::System.IDisposable
					{{
						private readonly {names.NameWithGenerics} _parent;
						private int _isDisposed;

						public __LifecycleDisposables({names.NameWithGenerics} parent)
						{{
							_parent = parent;
						}}

						public void Dispose()
						{{
							if (global::System.Threading.Interlocked.Exchange(ref _isDisposed, 1) == 0)
							{{
								{methods.Disposes.Select(m => m.GetInvocationText(target: "_parent")).JoinBy("\r\n")}
							}}
						}}
					}}";
			}

			void ExtendDispose()
			{
				switch (disposeKind)
				{
					case DisposeImplementationKind.Dispose:
						result +=
							"\r\n#error Your class has some methods marked with the '[DisposeMethod]' attribute. " +
							"The 'Dispose' pattern will be generated, and you must not implement it in your code. " +
							"If you want to add some code to the dispose, rename your method and mark it with the '[DisposeMethod]' attribute. " +
							$"You get this error because you defined {disposeMethod.SimpleLocationText()}.";
						break;

					case DisposeImplementationKind.DisposeOnBase:
						result +=
							"\r\n#error Your class has some methods marked with the '[DisposeMethod]' attribute, " +
							$"but the base class {disposeMethod.ContainingType.Name} sealed its implementation. " +
							$"You have either to remove the inheritance, or remove the method marked with the '[DisposeMethod]' attribute.";
						break;

					case DisposeImplementationKind.OverridableDisposeOnBase:
						result += $@"
							/// <inheritdoc />
							{disposeMethod.DeclaredAccessibility.ToString().ToLowerInvariant()} override void Dispose()
							{{
								base.Dispose();
								{methods.Disposes.Select(m => m.GetInvocationText()).JoinBy("\r\n")}
							}}";
						break;

					default:
						throw new NotSupportedException($"Invalid dispose pattern kind : '{disposeKind}'");
				}
			}
		}

		private string GetFinalizer(SymbolNames names, LifecycleMethods methods, string appendToFinalizer = null)
		{
			var result = string.Empty;

			// Validate that dispose methods does not have any parameter
			result += methods
				.Disposes
				.Where(dispose => dispose.Parameters.Any())
				.Select(dispose => $"\r\n#error You cannot define any parameter on your finalizer method ({dispose.SimpleLocationText()}).")
				.JoinByEmpty();

			// Validate that the return type of each dispose is void
			result += methods
				.Finalizers
				.Where(finalizer => !finalizer.ReturnsVoid)
				.Select(finalizer => $"\r\n#error The return type of {finalizer.SimpleLocationText()} must be 'void'.")
				.JoinByEmpty();

			if (methods
				.AllMethods
				.Any(method => method.Name == $"~{names.Name}"))
			{
				result +=
					"\r\n#error Your class has some methods marked with the '[FinalizerMethod]' attribute. " +
					"The finalizer will be generated, and you must not implement it in your code. " +
					"If you want to add some code to the dispose, rename your method and mark it with the '[FinalizerMethod]' attribute.";
			}
			else
			{
				result += $@"
					~{names.Name}()
					{{
						{appendToFinalizer ?? ""}
						{methods.Finalizers.Select(m => m.GetInvocationText()).JoinBy("\r\n")}
					}}";
			}

			return result;
		}
	}
}