using Jab;

namespace WebApplication1
{
	[ServiceProvider]
	[Singleton<MySingleton>]
	[Singleton(typeof(ILogger<>), Factory = nameof(ILogger_Factory))]
	[Scoped<MyScoped>]
	[Transient<MyTransient>]
	public partial class MyServiceProvider
	{
		public ILogger<T> ILogger_Factory<T>(IServiceProvider serviceProvider) =>
			GetMediService<ILogger<T>>(serviceProvider);
	}
}