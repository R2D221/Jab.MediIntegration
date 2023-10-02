using Microsoft.AspNetCore.Mvc;
using System.Data;

namespace WebApplication1
{
	public class Program
	{
		public static void Main(string[] args)
		{
			var myServiceProvider = new MyServiceProvider();

			var builder = WebApplication.CreateBuilder(args);

			builder.Host.UseServiceProviderFactory(myServiceProvider.Factory);

			// Add services to the container.
			builder.Services.AddAuthorization();

			var app = builder.Build();

			// Configure the HTTP request pipeline.

			app.UseHttpsRedirection();

			app.UseAuthorization();

			var summaries = new[]
			{
				"Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
			};

			app.MapGet("/weatherforecast", (HttpContext httpContext, [FromServices] MySingleton singleton, [FromServices] MyScoped scoped) =>
			{
				_ = singleton;
				_ = scoped;

				var forecast = Enumerable.Range(1, 5).Select(index =>
					new WeatherForecast
					{
						Date = DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
						TemperatureC = Random.Shared.Next(-20, 55),
						Summary = summaries[Random.Shared.Next(summaries.Length)]
					})
					.ToArray();
				return forecast;
			});

			app.Run();
		}
	}
}