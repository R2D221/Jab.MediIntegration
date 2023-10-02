namespace WebApplication1
{
	public class MySingleton : IDisposable
	{
		private readonly ILogger<MySingleton> logger;

		public MySingleton(ILogger<MySingleton> logger)
		{
			this.logger = logger;
		}

		public void Dispose() { }
	}
}