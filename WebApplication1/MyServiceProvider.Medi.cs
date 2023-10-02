using Jab;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace WebApplication1
{
	public partial class MyServiceProvider : MyServiceProvider.IMediServiceProvider
	{
		private interface IMediServiceProvider
		{
			public T GetMediService<T>();
		}

		private interface IMyServiceProviderScope
		{
			public ScopeWrapper ScopeWrapper { get; }
			public void TryAddDisposable(object? value);
		}

		private interface IMyServiceProviderWrapper
		{
			public MyServiceProvider MyServiceProvider { get; }
		}

		private static readonly ConditionalWeakTable<MyServiceProvider, Wrapper> wrappers = new();
		private static readonly ConditionalWeakTable<MyServiceProvider, WrapperFactory> factories = new();
		private static readonly MethodInfo EnumerableCast = typeof(Enumerable).GetMethod("Cast") ?? throw new Exception();
		private static readonly MethodInfo ImmutableArrayToImmutableArray = typeof(ImmutableArray).GetMethods().Where(x => x.Name == "ToImmutableArray" && x.IsGenericMethodDefinition && x.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>)).Single();

		private ILookup<Type, ServiceDescriptor> mediServicesDict = new ServiceCollection().ToLookup(x => x.ServiceType);

		private Wrapper wrapper => wrappers.GetValue(this, @this => new Wrapper(@this));

		private static bool IsSupported(IServiceProvider @this, Type serviceType)
		{
			var myServiceProvider = @this switch
			{
				IMyServiceProviderWrapper x => x.MyServiceProvider,
				_ => throw new Exception(),
			};

			if (typeof(IServiceProvider<>).MakeGenericType(serviceType).IsAssignableFrom(myServiceProvider.GetType()))
			{
				return true;
			}

			if (myServiceProvider.mediServicesDict[serviceType].Any())
			{
				return true;
			}
			else if (serviceType.IsConstructedGenericType)
			{
				var typeDefinition = serviceType.GetGenericTypeDefinition();

				if (typeDefinition == typeof(IEnumerable<>))
				{
					// R2D221 - 2023-10-01
					// IEnumerables are always supported.
					// If no serivces are registered, we'll just
					// return an empty array.

					return true;
				}
				else
				{
					var result = myServiceProvider.mediServicesDict[serviceType.GetGenericTypeDefinition()].Any();
					return result;
				}
			}

			return false;
		}

		private static T GetMediService<T>(IServiceProvider serviceProvider)
		{
			switch (serviceProvider)
			{
			case IMediServiceProvider mediServiceProvider: return mediServiceProvider.GetMediService<T>();
			default: throw new NotSupportedException();
			}
		}

		private static object? GetMediServiceImpl(IServiceProvider @this, ServiceDescriptor descriptor, Type[]? typeArguments = null)
		{
			if (descriptor.ImplementationInstance is {/*notnull*/} instance)
			{
				return instance;
			}
			else if (descriptor.ImplementationFactory is {/*notnull*/} factory)
			{
				return factory(@this);
			}
			else if (descriptor.ImplementationType is {/*notnull*/} type)
			{
				if (typeArguments is not null)
				{
					type = type.MakeGenericType(typeArguments);
				}

				var constructors = type.GetConstructors();

				var constructorsByParamLength = constructors.ToLookup(x => x.GetParameters().Length);

				foreach (var constructorGroup in constructorsByParamLength.OrderByDescending(x => x.Key))
				{
					var constructorTuple =
						constructorGroup
						.Select(constructor =>
							(
								constructor,
								isSupported:
									constructor.GetParameters()
									.Select(x => IsSupported(@this, x.ParameterType))
									.All(x => x)
							)
						)
						.Where(tuple => tuple.isSupported)
						.SingleOrDefault();

					if (constructorTuple.constructor is not null)
					{
						var parameters =
							constructorTuple.constructor.GetParameters()
							.Select(x => @this.GetService(x.ParameterType))
							.ToArray()
							;

						return constructorTuple.constructor.Invoke(parameters);
					}
				}

				throw new Exception();
			}

			throw new Exception();
		}

		T IMediServiceProvider.GetMediService<T>() =>
			(T)(wrapper.GetMediService(typeof(T)) ?? throw new Exception());

		public IServiceProviderFactory<IServiceCollection> Factory =>
			factories.GetValue(this, @this => new WrapperFactory(@this));

		class WrapperFactory : IServiceProviderFactory<IServiceCollection>
		{
			private MyServiceProvider myServiceProvider;

			public WrapperFactory(MyServiceProvider myServiceProvider)
			{
				this.myServiceProvider = myServiceProvider;
			}

			public IServiceCollection CreateBuilder(IServiceCollection services) => services;

			public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder)
			{
				myServiceProvider.mediServicesDict = containerBuilder.ToLookup(x => x.ServiceType);
				return myServiceProvider.wrapper;
			}
		}

		class Wrapper : IServiceProvider, IServiceScopeFactory, IMyServiceProviderWrapper, IDisposable, IAsyncDisposable
		{
			private readonly MyServiceProvider myServiceProvider;
			private readonly ConcurrentDictionary<ServiceDescriptor, Lazy<object?>> singletons = new();
			private readonly ConcurrentDictionary<ServiceDescriptor, ConcurrentDictionary<Type, Lazy<object?>>> genericSingletons = new();

			public Wrapper(MyServiceProvider myServiceProvider)
			{
				this.myServiceProvider = myServiceProvider;
			}

			MyServiceProvider IMyServiceProviderWrapper.MyServiceProvider => myServiceProvider;

			object? IServiceProvider.GetService(Type serviceType)
			{
				if (serviceType == typeof(IServiceProvider))
				{
					return this;
				}

				if (serviceType == typeof(IServiceScopeFactory))
				{
					return this;
				}

				if (((IServiceProvider)myServiceProvider).GetService(serviceType) is {/*notnull*/} jabService)
				{
					return jabService;
				}

				return GetMediService(serviceType);
			}

			public object? GetMediService(Type serviceType)
			{
				if (myServiceProvider.mediServicesDict[serviceType].SingleOrDefault() is {/*notnull*/} descriptor)
				{
					return GetMediService(descriptor);
				}
				else if (serviceType.IsConstructedGenericType)
				{
					var typeDefinition = serviceType.GetGenericTypeDefinition();

					if (typeDefinition == typeof(IEnumerable<>))
					{
						var innerType = serviceType.GetGenericArguments()[0];
						var services =
							myServiceProvider.mediServicesDict[innerType]
							.Select(x => GetMediService(x))
							.ToList();

						var castT = EnumerableCast.MakeGenericMethod(innerType);
						object casted = castT.Invoke(null, new object?[] { services }) ?? throw new Exception();

						var toImmutableArrayT = ImmutableArrayToImmutableArray.MakeGenericMethod(innerType);
						object @return = toImmutableArrayT.Invoke(null, new object?[] { casted }) ?? throw new Exception();

						return @return;
					}
					else if (myServiceProvider.mediServicesDict[serviceType.GetGenericTypeDefinition()].SingleOrDefault() is {/*notnull*/} genericDescriptor)
					{
						return GetMediService(genericDescriptor, serviceType.GetGenericArguments());
					}
				}

				return null;
			}

			public object? GetMediService(ServiceDescriptor descriptor, Type[]? typeArguments = null)
			{
				switch (descriptor.Lifetime)
				{
				case ServiceLifetime.Singleton:
				{
					if (typeArguments is null)
					{
						return singletons
							.GetOrAdd(descriptor, _ => new(() =>
							{
								var service = GetMediServiceImpl(this, descriptor);
								myServiceProvider.TryAddDisposable(service);
								return service;
							}))
							.Value;
					}
					else
					{
						return genericSingletons
							.GetOrAdd(descriptor, _ => new())
							.GetOrAdd(typeArguments.Single(), _ => new(() =>
							{
								var service = GetMediServiceImpl(this, descriptor, typeArguments);
								myServiceProvider.TryAddDisposable(service);
								return service;
							}))
							.Value;
					}
				}

				case ServiceLifetime.Scoped:
				{
					return ((IMyServiceProviderScope)myServiceProvider.GetRootScope()).ScopeWrapper.GetMediService(descriptor, typeArguments);
				}

				case ServiceLifetime.Transient:
				{
					var service = GetMediServiceImpl(this, descriptor, typeArguments);
					myServiceProvider.TryAddDisposable(service);
					return service;
				}

				default: throw new NotSupportedException();
				}
			}

			IServiceScope IServiceScopeFactory.CreateScope()
			{
				var scope = myServiceProvider.CreateScope();
				return ((IMyServiceProviderScope)scope).ScopeWrapper;
			}

			void IDisposable.Dispose()
			{
				myServiceProvider.Dispose();
			}

			ValueTask IAsyncDisposable.DisposeAsync()
			{
				return myServiceProvider.DisposeAsync();
			}
		}

		public partial class Scope : IMyServiceProviderScope, IMediServiceProvider
		{
			private static readonly ConditionalWeakTable<Scope, ScopeWrapper> scopeWrappers = new();

			ScopeWrapper IMyServiceProviderScope.ScopeWrapper =>
				scopeWrappers.GetValue(this, @this => new ScopeWrapper(@this._root.wrapper, @this._root, @this));

			void IMyServiceProviderScope.TryAddDisposable(object? value)
			{
				TryAddDisposable(value);
			}

			T IMediServiceProvider.GetMediService<T>() =>
				(T)(((IMyServiceProviderScope)this).ScopeWrapper.GetMediService(typeof(T)) ?? throw new Exception());
		}

		class ScopeWrapper : IServiceProvider, IServiceScope, IDisposable, IAsyncDisposable, IMyServiceProviderWrapper
		{
			private readonly Wrapper wrapper;
			private readonly MyServiceProvider myServiceProvider;
			private readonly Scope scope;
			private readonly ConcurrentDictionary<ServiceDescriptor, Lazy<object?>> scopeds = new();
			private readonly ConcurrentDictionary<ServiceDescriptor, ConcurrentDictionary<Type, Lazy<object?>>> genericScopeds = new();

			public ScopeWrapper(Wrapper wrapper, MyServiceProvider myServiceProvider, Scope scope)
			{
				this.wrapper = wrapper;
				this.myServiceProvider = myServiceProvider;
				this.scope = scope;
			}

			MyServiceProvider IMyServiceProviderWrapper.MyServiceProvider => myServiceProvider;

			object? IServiceProvider.GetService(Type serviceType)
			{
				if (serviceType == typeof(IServiceProvider))
				{
					return this;
				}

				if (serviceType == typeof(IServiceScopeFactory))
				{
					return wrapper;
				}

				if (((IServiceProvider)scope).GetService(serviceType) is {/*notnull*/} jabService)
				{
					return jabService;
				}

				return GetMediService(serviceType);
			}

			public object? GetMediService(Type serviceType)
			{
				if (myServiceProvider.mediServicesDict[serviceType].SingleOrDefault() is {/*notnull*/} descriptor)
				{
					return GetMediService(descriptor);
				}
				else if (serviceType.IsConstructedGenericType)
				{
					var typeDefinition = serviceType.GetGenericTypeDefinition();

					if (typeDefinition == typeof(IEnumerable<>))
					{
						var innerType = serviceType.GetGenericArguments()[0];
						var services =
							myServiceProvider.mediServicesDict[innerType]
							.Select(x => GetMediService(x))
							.ToList();

						var castT = EnumerableCast.MakeGenericMethod(innerType);
						object casted = castT.Invoke(null, new object?[] { services }) ?? throw new Exception();

						var toImmutableArrayT = ImmutableArrayToImmutableArray.MakeGenericMethod(innerType);
						object @return = toImmutableArrayT.Invoke(null, new object?[] { casted }) ?? throw new Exception();

						return @return;
					}
					else if (myServiceProvider.mediServicesDict[serviceType.GetGenericTypeDefinition()].SingleOrDefault() is {/*notnull*/} genericDescriptor)
					{
						return GetMediService(genericDescriptor, serviceType.GetGenericArguments());
					}
				}

				return null;
			}

			public object? GetMediService(ServiceDescriptor descriptor, Type[]? typeArguments = null)
			{
				switch (descriptor.Lifetime)
				{
				case ServiceLifetime.Singleton:
				{
					return wrapper.GetMediService(descriptor, typeArguments);
				}

				case ServiceLifetime.Scoped:
				{
					if (typeArguments is null)
					{
						return scopeds
							.GetOrAdd(descriptor, _ => new(() =>
							{
								var service = GetMediServiceImpl(this, descriptor);
								((IMyServiceProviderScope)scope).TryAddDisposable(service);
								return service;
							}))
							.Value;
					}
					else
					{
						return genericScopeds
							.GetOrAdd(descriptor, _ => new())
							.GetOrAdd(typeArguments.Single(), _ => new(() =>
							{
								var service = GetMediServiceImpl(this, descriptor, typeArguments);
								((IMyServiceProviderScope)scope).TryAddDisposable(service);
								return service;
							}))
							.Value;
					}
				}

				case ServiceLifetime.Transient:
				{
					var service = GetMediServiceImpl(this, descriptor, typeArguments);
					myServiceProvider.TryAddDisposable(service);
					return service;
				}

				default: throw new NotSupportedException();
				}
			}

			IServiceProvider IServiceScope.ServiceProvider => this;

			void IDisposable.Dispose()
			{
				scope.Dispose();
			}

			ValueTask IAsyncDisposable.DisposeAsync()
			{
				return scope.DisposeAsync();
			}
		}
	}
}
