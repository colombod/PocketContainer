﻿// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// THIS FILE IS NOT INTENDED TO BE EDITED. 
// 
// It has been imported using NuGet from the PocketContainer project (https://github.com/jonsequitur/PocketContainer). 
// 
// This file can be updated in-place using the Package Manager Console. To check for updates, run the following command:
// 
// PM> Get-Package -Updates

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

#pragma warning disable CS0436 // Type conflicts with imported type

namespace Pocket
{
    /// <summary>
    /// An embedded dependency injection container, for when you want to use a container without adding an assembly dependency.
    /// </summary>
    /// <remarks>The default resolution strategy the following conventions: 
    /// * A concrete type can be resolved without explicit registration. 
    /// * PocketContainer will choose the longest constructor and resolve the types to satisfy its arguments. This continues recursively until the graph is built.
    /// * If it fails to build a dependency somewhere in the graph, an ArgumentException is thrown.</remarks>
#if !SourceProject
    [System.Diagnostics.DebuggerStepThrough]
#endif
    internal partial class PocketContainer : IEnumerable<KeyValuePair<Type, Func<PocketContainer, object>>>
    {
        private static readonly MethodInfo resolveMethod =
            typeof (PocketContainer).GetMethod(nameof(Resolve), Type.EmptyTypes);

        private static readonly MethodInfo registerMethod =
            typeof (PocketContainer).GetMethods().Single(m => m.Name == nameof(Register) && m.IsGenericMethod);

        private static readonly MethodInfo registerSingleMethod =
            typeof (PocketContainer).GetMethods().Single(m => m.Name == nameof(RegisterSingle) && m.IsGenericMethod);

        private ConcurrentDictionary<Type, Func<PocketContainer, object>> resolvers = new ConcurrentDictionary<Type, Func<PocketContainer, object>>();

        private ConcurrentDictionary<Type, object> singletons = new ConcurrentDictionary<Type, object>();

        private Func<Type, Func<PocketContainer, object>> strategyChain = type => null;

        /// <summary>
        /// Initializes a new instance of the <see cref="PocketContainer"/> class.
        /// </summary>
        public PocketContainer()
        {
            RegisterSingle(c => this);

            AddStrategy(type =>
            {
                // add a default strategy for Func<T> to resolve by convention to return a Func that does a resolve when invoked
                if (type.GetTypeInfo().IsGenericType && type.GetGenericTypeDefinition() == typeof(Func<>))
                {
                    return (Func<PocketContainer, object>)
                        GetType()
                            .GetMethod(nameof(MakeResolverFunc), BindingFlags.Instance | BindingFlags.NonPublic)
                            .MakeGenericMethod(type.GetGenericArguments().Single())
                            .Invoke(this, null);
                }

                return null;
            });

            AfterConstructor();
        }

        private Type resolving;

        /// <summary>
        /// Resolves an instance of the specified type.
        /// </summary>
        public T Resolve<T>()
        {
            T resolved;

            if (resolving != typeof(T))
            {
                resolving = typeof(T);
                resolved = (T) resolvers.GetOrAdd(typeof(T), t =>
                {
                    var customFactory = strategyChain(t);
                    if (customFactory != null)
                    {
                        BeforeRegister?.Invoke(customFactory);
                        return customFactory;
                    }

                    Func<PocketContainer, T> defaultFactory;
                    try
                    {
                        defaultFactory = Factory<T>.Default;
                    }
                    catch (TypeInitializationException ex)
                    {
                        var ex2 = OnFailedResolve(typeof(T), ex);

                        if (ex2 != null)
                        {
                            throw ex2;
                        }

                        defaultFactory = c => default(T);
                    }

                    BeforeRegister?.Invoke(defaultFactory);

                    return c => defaultFactory(c);
                })(this);
                resolving = null;
            }
            else
            {
                resolved = Activator.CreateInstance<T>();
            }

            AfterResolve?.Invoke(typeof(T), resolved);

            return resolved;
        }

        /// <summary>
        /// Returns an exception to be thrown when resolve fails.
        /// </summary>
        public Func<Type, Exception, Exception> OnFailedResolve =
            (type, exception) =>
                new ArgumentException(
                    $"PocketContainer can't construct a {type} unless you register it first. ☹", exception);

        /// <summary>
        /// Resolves an instance of the specified type.
        /// </summary>
        public object Resolve(Type type)
        {
            object resolved;

            if (!resolvers.TryGetValue(type, out var func))
            {
                try
                {
                    resolved = resolveMethod.MakeGenericMethod(type).Invoke(this, null);
                }
                catch (TargetInvocationException ex)
                {
                    if (ex.InnerException != null)
                    {
                        throw ex.InnerException;
                    }
                    throw;
                }
            }
            else
            {
                resolved = func(this);
            }

            AfterResolve?.Invoke(type, resolved);

            return resolved;
        }

        /// <remarks>When an unregistered type is resolved for the first time, the strategies are checked until one returns a delegate. This delegate will be used in the future to resolve the specified type.</remarks>
        public PocketContainer AddStrategy(
            Func<Type, Func<PocketContainer, object>> strategy,
            bool executeFirst = true)
        {
            var previousStrategy = strategyChain;
            if (executeFirst)
            {
                strategyChain = type => strategy(type) ?? previousStrategy(type);
            }
            else
            {
                strategyChain = type => previousStrategy(type) ?? strategy(type);
            }
            return this;
        }

        partial void AfterConstructor();

        public event Action<Type, object> AfterResolve;

        public event Action<Delegate> BeforeRegister;

        /// <summary>
        /// Registers a delegate to retrieve instances of the specified type.
        /// </summary>
        public PocketContainer Register<T>(Func<PocketContainer, T> factory)
        {
            BeforeRegister?.Invoke(factory);
            resolvers[typeof (T)] = c => factory(c);
            resolvers[typeof (Lazy<T>)] = c => new Lazy<T>(c.Resolve<T>);
            return this;
        }

        /// <summary>
        /// Registers a delegate to retrieve instances of the specified type.
        /// </summary>
        public PocketContainer Register(Type type, Func<PocketContainer, object> factory)
        {
            registerMethod
                .MakeGenericMethod(type)
                .Invoke(this, new object[] { ConvertFunc(factory, type) });
            return this;
        }

        /// <summary>
        /// Registers a delegate to retrieve an instance of the specified type when it is first resolved. This instance will be reused for the lifetime of the container.
        /// </summary>
        public PocketContainer RegisterSingle<T>(Func<PocketContainer, T> factory)
        {
            Register(c => (T) singletons.GetOrAdd(typeof(T), t =>
            {
                var resolved = factory(c);

                TryRegisterSingle(resolved.GetType(), _ => resolved);

                return resolved;
            }));
            singletons.TryRemove(typeof(T), out object _);
            return this;
        }

        /// <summary>
        /// Registers a delegate to retrieve an instance of the specified type when it is first resolved. This instance will be reused for the lifetime of the container.
        /// </summary>
        public PocketContainer RegisterSingle(Type type, Func<PocketContainer, object> factory)
        {
            registerSingleMethod
                .MakeGenericMethod(type)
                .Invoke(this, new object[] { ConvertFunc(factory, type) });
            return this;
        }

        public PocketContainer TryRegister(
            Type type, 
            Func<PocketContainer, object> factory)
        {
            if (!resolvers.ContainsKey(type))
            {
                Register(type, factory);
            }

            return this;
        }

        public PocketContainer TryRegister<T>(Func<PocketContainer, T> factory)
        {
            if (!resolvers.ContainsKey(typeof(T)))
            {
                Register(factory);
            }

            return this;
        }

        public PocketContainer TryRegisterSingle(
            Type type, 
            Func<PocketContainer, object> factory)
        {
            if (!resolvers.ContainsKey(type))
            {
                RegisterSingle(type, factory);
            }

            return this;
        }

        public PocketContainer TryRegisterSingle<T>(Func<PocketContainer, T> factory)
        {
            if (!resolvers.ContainsKey(typeof(T)))
            {
                RegisterSingle(factory);
            }

            return this;
        }

        private Delegate ConvertFunc(Func<PocketContainer, object> func, Type resultType)
        {
            var containerParam = Expression.Parameter(typeof (PocketContainer), "c");

            ConstantExpression constantExpression = null;
            if (func.Target != null)
            {
                constantExpression = Expression.Constant(func.Target);
            }

            // ReSharper disable PossiblyMistakenUseOfParamsMethod
            var call = Expression.Call(constantExpression, func.GetMethodInfo(), containerParam);
            // ReSharper restore PossiblyMistakenUseOfParamsMethod
            var delegateType = typeof (Func<,>).MakeGenericType(typeof (PocketContainer), resultType);
            var body = Expression.Convert(call, resultType);
            var expression = Expression.Lambda(delegateType,
                                               body,
                                               containerParam);
            return expression.Compile();
        }

        internal static class Factory<T>
        {
            public static readonly Func<PocketContainer, T> Default = Build.UsingLongestConstructor<T>();
        }

        internal static class Build
        {
            public static Func<PocketContainer, T> UsingLongestConstructor<T>()
            {
                if (typeof (Delegate).IsAssignableFrom(typeof (T)))
                {
                    throw new TypeInitializationException(typeof (T).FullName, null);
                }

                var ctors = typeof (T).GetConstructors();

                var longestCtorParamCount = ctors.Max(c => c.GetParameters().Length);

                var chosenCtor = ctors.Single(c => c.GetParameters().Length == longestCtorParamCount);

                var container = Expression.Parameter(typeof (PocketContainer), "container");

                var factoryExpr = Expression.Lambda<Func<PocketContainer, T>>(
                    Expression.New(chosenCtor,
                                   chosenCtor.GetParameters()
                                             .Select(p =>
                                                     Expression.Call(container,
                                                                     resolveMethod
                                                                         .MakeGenericMethod(p.ParameterType)))),
                    container);

                return factoryExpr.Compile();
            }
        }

        public IEnumerator<KeyValuePair<Type, Func<PocketContainer, object>>> GetEnumerator() => resolvers.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // ReSharper disable UnusedMember.Local
        private Func<PocketContainer, Func<T>> MakeResolverFunc<T>()
        // ReSharper restore UnusedMember.Local
        {
            var container = Expression.Parameter(typeof (PocketContainer), "container");

            var resolve = Expression.Lambda<Func<PocketContainer, Func<T>>>(
                Expression.Lambda<Func<T>>(
                    Expression.Call(container,
                                    resolveMethod.MakeGenericMethod(typeof (T)))),
                container);

            return resolve.Compile();
        }
    }
}
