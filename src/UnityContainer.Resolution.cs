using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.Builder;
using Unity.Exceptions;
using Unity.Lifetime;
using Unity.Policy;
using Unity.Registration;
using Unity.Resolution;
using Unity.Storage;
using Unity.Strategies;

namespace Unity
{
    /// <summary>
    /// A simple, extensible dependency injection container.
    /// </summary>
    [SecuritySafeCritical]
    public partial class UnityContainer
    {
        #region Dynamic Registrations

        private IPolicySet GetDynamicRegistration(Type type, string name)
        {
            var registration = _get(type, name);
            if (null != registration) return registration;

            var info = type.GetTypeInfo();
            return !info.IsGenericType
                ? _root.GetOrAdd(type, name)
                : GetOrAddGeneric(type, name, info.GetGenericTypeDefinition());
        }

        private IPolicySet CreateRegistration(Type type, string name)
        {

            var registration = new InternalRegistration(type, name);

            if (type.GetTypeInfo().IsGenericType)
            {
                var factory = (InternalRegistration)_get(type.GetGenericTypeDefinition(), name);
                if (null != factory)
                {
                    registration.InjectionMembers = factory.InjectionMembers;
                    registration.Map = factory.Map;
                }
            }

            registration.BuildChain = GetBuilders(type, registration);
            return registration;
        }

        private IPolicySet CreateRegistration(Type type, Type policyInterface, object policy)
        {
            var registration = new InternalRegistration(policyInterface, policy);
            registration.BuildChain = GetBuilders(type, registration);
            return registration;
        }

        #endregion


        #region Resolving Collections

        internal static object ResolveEnumerable<TElement>(ref BuilderContext context)
        {
            var type = typeof(TElement);
#if NETSTANDARD1_0 || NETCOREAPP1_0
            var generic = type.GetTypeInfo().IsGenericType ? type.GetGenericTypeDefinition() : type;
#else
            var generic = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
#endif
            var set = generic == type ? GetRegistrations((UnityContainer)context.Container, type)
                                      : GetRegistrations((UnityContainer)context.Container, type, generic);

            return ResolveRegistrations<TElement>(ref context, set);
        }

        internal static object ResolveArray<TElement>(ref BuilderContext context)
        {
            var type = typeof(TElement);
#if NETSTANDARD1_0 || NETCOREAPP1_0
            var generic = type.GetTypeInfo().IsGenericType ? type.GetGenericTypeDefinition() : type;
#else
            var generic = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
#endif
            var set = generic == type ? GetNamedRegistrations((UnityContainer)context.Container, type)
                                      : GetNamedRegistrations((UnityContainer)context.Container, type, generic);
            return ResolveRegistrations<TElement>(ref context, set).ToArray();
        }

        private static IList<TElement> ResolveRegistrations<TElement>(ref BuilderContext context, RegistrationSet registrations)
        {
            var type = typeof(TElement);
            var list = new List<TElement>();
            for (var i = 0; i < registrations.Count; i++)
            {
                ref var entry = ref registrations[i];
                try
                {
#if NETSTANDARD1_0 || NETCOREAPP1_0
                    if (entry.RegisteredType.GetTypeInfo().IsGenericTypeDefinition)
#else
                    if (entry.RegisteredType.IsGenericTypeDefinition)
#endif
                        list.Add((TElement)context.Resolve(type, entry.Name));
                    else
                        list.Add((TElement)context.Resolve(type, entry.Name, entry.Registration));
                }
                catch (ArgumentException ex) when (ex.InnerException is TypeLoadException)
                {
                    // Ignore
                }
            }

            return list;
        }

        #endregion


        #region Resolving Generic Collections

        internal static object ResolveGenericEnumerable<TElement>(ref BuilderContext context, Type type)
        {
            var set = GetRegistrations((UnityContainer)context.Container, typeof(TElement), type);
            return ResolveGenericRegistrations<TElement>(ref context, set);
        }

        internal static object ResolveGenericArray<TElement>(ref BuilderContext context, Type type)
        {
            var set = GetNamedRegistrations((UnityContainer)context.Container, typeof(TElement), type);
            return ResolveGenericRegistrations<TElement>(ref context, set).ToArray();
        }

        private static IList<TElement> ResolveGenericRegistrations<TElement>(ref BuilderContext context, RegistrationSet registrations)
        {
            var list = new List<TElement>();
            for (var i = 0; i < registrations.Count; i++)
            {
                ref var entry = ref registrations[i];
                try
                {
                    list.Add((TElement)context.Resolve(typeof(TElement), entry.Name));
                }
                catch (MakeGenericTypeFailedException) { /* Ignore */ }
                catch (InvalidOperationException ex) when (ex.InnerException is InvalidRegistrationException)
                {
                    // Ignore
                }
            }

            return list;
        }

        #endregion


        #region Resolve Delegate Factories

        private static ResolveDelegate<BuilderContext> OptimizingFactory(ref BuilderContext context)
        {
            var counter = 3;
            var type = context.Type;
            var registration = context.Registration;
            ResolveDelegate<BuilderContext> seed = null;
            var chain = ((UnityContainer) context.Container)._processorsChain;

            // Generate build chain
            foreach (var processor in chain)
                seed = processor.GetResolver(type, registration, seed);

            // Return delegate
            return (ref BuilderContext c) => 
            {
                // Check if optimization is required
                if (0 == Interlocked.Decrement(ref counter))
                {
                    Task.Factory.StartNew(() => {

                        // Compile build plan on worker thread
                        var expressions = new List<Expression>();
                        foreach (var processor in chain)
                        {
                            foreach (var step in processor.GetExpressions(type, registration))
                                expressions.Add(step);
                        }

                        expressions.Add(BuilderContextExpression.Existing);

                        var lambda = Expression.Lambda<ResolveDelegate<BuilderContext>>(
                            Expression.Block(expressions), BuilderContextExpression.Context);

                        // Replace this build plan with compiled
                        registration.Set(typeof(ResolveDelegate<BuilderContext>), lambda.Compile());
                    });
                }

                return seed?.Invoke(ref c);
            };
        }

        private ResolveDelegate<BuilderContext> CompilingFactory(ref BuilderContext context)
        {
            var expressions = new List<Expression>();
            var type = context.Type;
            var registration = context.Registration;

            foreach (var processor in _processorsChain)
            {
                foreach (var step in processor.GetExpressions(type, registration))
                    expressions.Add(step);
            }

            expressions.Add(BuilderContextExpression.Existing);

            var lambda = Expression.Lambda<ResolveDelegate<BuilderContext>>(
                Expression.Block(expressions), BuilderContextExpression.Context);

            return lambda.Compile();
        }

        private ResolveDelegate<BuilderContext> ResolvingFactory(ref BuilderContext context)
        {
            ResolveDelegate<BuilderContext> seed = null;
            var type = context.Type;
            var registration = context.Registration;

            foreach (var processor in _processorsChain)
                seed = processor.GetResolver(type, registration, seed);

            return seed;
        }

        #endregion


        #region Build Plans

        private ResolveDelegate<BuilderContext> ExecutePlan { get; set; } =
            (ref BuilderContext context) =>
            {
                var i = -1;
                BuilderStrategy[] chain = ((InternalRegistration)context.Registration).BuildChain;

                try
                {
                    while (!context.BuildComplete && ++i < chain.Length)
                    {
                        chain[i].PreBuildUp(ref context);
                    }

                    while (--i >= 0)
                    {
                        chain[i].PostBuildUp(ref context);
                    }
                }
                catch (Exception ex) 
                {
                    context.RequiresRecovery?.Recover();

                    throw new ResolutionFailedException(context.RegistrationType, context.Name,
                        "For more information add Diagnostic extension: Container.AddExtension(new Diagnostic())", ex);
                }

                return context.Existing;
            };


        private object ExecuteValidatingPlan(ref BuilderContext context)
        {
            var i = -1;
            BuilderStrategy[] chain = ((InternalRegistration)context.Registration).BuildChain;

            try
            {
                while (!context.BuildComplete && ++i < chain.Length)
                {
                    chain[i].PreBuildUp(ref context);
                }

                while (--i >= 0)
                {
                    chain[i].PostBuildUp(ref context);
                }
            }
            catch (Exception ex)
            {
                context.RequiresRecovery?.Recover();
                ex.Data.Add(Guid.NewGuid(), null == context.Name
                    ? context.RegistrationType == context.Type
                        ? (object)context.Type
                        : new Tuple<Type, Type>(context.RegistrationType, context.Type)
                    : context.RegistrationType == context.Type
                        ? (object)new Tuple<Type, string>(context.Type, context.Name)
                        : new Tuple<Type, Type, string>(context.RegistrationType, context.Type, context.Name));

                var builder = new StringBuilder();
                builder.AppendLine(ex.Message);
                builder.AppendLine("_____________________________________________________");
                builder.AppendLine("Exception occurred while:");
                builder.AppendLine();

                var indent = 0;
                foreach (DictionaryEntry item in ex.Data)
                {
                    for (var c = 0; c < indent; c++) builder.Append(" ");
                    builder.AppendLine(CreateErrorMessage(item.Value));
                    indent += 1;
                }

                var message = builder.ToString();

                throw new ResolutionFailedException( context.RegistrationType, context.Name, message, ex);
            }

            return context.Existing;


            string CreateErrorMessage(object value)
            {
                switch (value)
                {
                    case ParameterInfo parameter:
                        return $" for parameter:  '{parameter.Name}'";

                    case ConstructorInfo constructor:
                        var ctorSignature = string.Join(", ", constructor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        return $"on constructor:  {constructor.DeclaringType.Name}({ctorSignature})";

                    case MethodInfo method:
                        var methodSignature = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        return $"     on method:  {method.Name}({methodSignature})";

                    case PropertyInfo property:
                        return $"  for property:  '{property.Name}'";

                    case FieldInfo field:
                        return $"     for field:  '{field.Name}'";

                    case Type type:
                        return $"·resolving type:  '{type.Name}'";

                    case Tuple<Type, string> tuple:
                        return $"•resolving type:  '{tuple.Item1.Name}' registered with name: '{tuple.Item2}'";

                    case Tuple<Type, Type> tuple:
                        return $"•resolving type:  '{tuple.Item1?.Name}' mapped to '{tuple.Item2?.Name}'";

                    case Tuple<Type, Type, string> tuple:
                        return $"•resolving type:  '{tuple.Item1?.Name}' mapped to '{tuple.Item2?.Name}' and registered with name: '{tuple.Item3}'";
                }

                return value.ToString();
            }
        }

        internal BuilderContext.ExecutePlanDelegate ContextExecutePlan { get; set; } =
            (BuilderStrategy[] chain, ref BuilderContext context) =>
            {
                var i = -1;

                try
                {
                    while (!context.BuildComplete && ++i < chain.Length)
                    {
                        chain[i].PreBuildUp(ref context);
                    }

                    while (--i >= 0)
                    {
                        chain[i].PostBuildUp(ref context);
                    }
                }
                catch when (null != context.RequiresRecovery)
                {
                    context.RequiresRecovery.Recover();
                    throw;
                }

                return context.Existing;
            };

        internal static object ContextValidatingPlan(BuilderStrategy[] chain, ref BuilderContext context)
        {
            var i = -1;
#if !NET40
            var value = GetPerResolveValue(context.Parent, context.RegistrationType, context.Name);
            if (null != value) return value;
#endif
            try
            {
                while (!context.BuildComplete && ++i < chain.Length)
                {
                    chain[i].PreBuildUp(ref context);
                }

                while (--i >= 0)
                {
                    chain[i].PostBuildUp(ref context);
                }
            }
            catch (Exception ex)
            {
                context.RequiresRecovery?.Recover();
                ex.Data.Add(Guid.NewGuid(), null == context.Name
                    ? context.RegistrationType == context.Type 
                        ? (object)context.Type 
                        : new Tuple<Type, Type>(context.RegistrationType, context.Type)
                    : context.RegistrationType == context.Type 
                        ? (object)new Tuple<Type, string>(context.Type, context.Name) 
                        : new Tuple<Type, Type, string>(context.RegistrationType, context.Type, context.Name));
                throw;
            }

            return context.Existing;

#if !NET40
            object GetPerResolveValue(IntPtr parent, Type registrationType, string name)
            {
                if (IntPtr.Zero == parent) return null;

                unsafe
                {
                    var c = Unsafe.AsRef<BuilderContext>(parent.ToPointer());
                    if (registrationType != c.RegistrationType || name != c.Name)
                        return GetPerResolveValue(c.Parent, registrationType, name);

                    var lifetimeManager = (LifetimeManager)c.Get(typeof(LifetimeManager));
                    var result = lifetimeManager?.GetValue();
                    if (null != result) return result;

                    throw new InvalidOperationException($"Circular reference for type: {c.Type}");
                }
            }
#endif
        }

        #endregion
    }
}
